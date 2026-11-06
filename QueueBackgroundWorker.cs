using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace SMusicBoxAutoScheduler
{
    public class QueueBackgroundWorker
    {
        private readonly YouTubeUploadService _uploadService = new();
        private CancellationTokenSource? _cts;
        private Task? _workerTask;
        private int _processing;
        private volatile bool _paused = true;

        public bool IsPaused => _paused;

        public void Pause()
        {
            _paused = true;
        }

        public void Resume()
        {
            _paused = false;
        }

        public QueueBackgroundWorker()
        {
            DatabaseManager.InitializeDatabase();
            RecoverInterruptedJobsAsync().GetAwaiter().GetResult();
        }

        public void Start()
        {
            if (_workerTask != null && !_workerTask.IsCompleted)
                return;

            _cts = new CancellationTokenSource();
            _workerTask = Task.Run(() => PollQueueAsync(_cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
        }

        private static async Task RecoverInterruptedJobsAsync()
        {
            using var connection = new SqliteConnection(
                $"Data Source={AppPaths.Database}");

            await connection.OpenAsync();

            using var command = connection.CreateCommand();

            command.CommandText = @"
                UPDATE upload_queue
                SET status = 'pending',
                    processing_started_utc = NULL,
                    error_message = 'Recovered after interruption before upload.'
                WHERE status = 'processing'
                  AND upload_started_utc IS NULL;

                UPDATE upload_queue
                SET status = 'needs_review',
                    error_message = 'Application stopped during an upload. Check YouTube Studio before retrying.'
                WHERE status = 'processing'
                  AND upload_started_utc IS NOT NULL;
            ";

            await command.ExecuteNonQueryAsync();
        }

        private async Task PollQueueAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!_paused)
                        await ProcessQueueAsync(token);
                    await Task.Delay(30000, token);
                }
                catch (OperationCanceledException)
                    when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Queue worker error: {ex}");

                    await Task.Delay(30000, token);
                }
            }
        }

        private async Task ProcessQueueAsync(CancellationToken token)
        {
            if (Interlocked.Exchange(ref _processing, 1) == 1)
                return;

            try
            {
                var jobs = await ReadPendingJobsAsync(token);

                foreach (var job in jobs)
                {
                    token.ThrowIfCancellationRequested();

                    if (_paused)
                        break;

                    if (!await ClaimJobAsync(job.Id, token))
                        continue;

                    await ProcessJobAsync(job, token);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _processing, 0);
            }
        }

        private static async Task<List<QueueJob>> ReadPendingJobsAsync(
            CancellationToken token)
        {
            var jobs = new List<QueueJob>();

            using var connection = new SqliteConnection(
                $"Data Source={AppPaths.Database}");

            await connection.OpenAsync(token);

            using var command = connection.CreateCommand();

            command.CommandText = @"
                SELECT id, file_path, title, description,
                       thumbnail_path, scheduled_time, retry_count
                FROM upload_queue
                WHERE status = 'pending'
                   OR (
                       status LIKE 'retry_pending%'
                       AND (
                           next_attempt_utc IS NULL
                           OR next_attempt_utc <= @now
                       )
                   )
                ORDER BY id;";

            command.Parameters.AddWithValue(
                "@now", DateTimeOffset.UtcNow.ToString("O"));

            using var reader = await command.ExecuteReaderAsync(token);

            while (await reader.ReadAsync(token))
            {
                jobs.Add(new QueueJob(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? "Untitled" : reader.GetString(2),
                    reader.IsDBNull(3) ? "" : reader.GetString(3),
                    reader.IsDBNull(4) ? "" : reader.GetString(4),
                    reader.IsDBNull(5) ? "" : reader.GetString(5),
                    reader.IsDBNull(6) ? 0 : reader.GetInt32(6)));
            }

            return jobs;
        }

        private static async Task<bool> ClaimJobAsync(
            long id, CancellationToken token)
        {
            using var connection = new SqliteConnection(
                $"Data Source={AppPaths.Database}");

            await connection.OpenAsync(token);

            using var command = connection.CreateCommand();

            command.CommandText = @"
                UPDATE upload_queue
                SET status = 'processing',
                    processing_started_utc = @now,
                    upload_started_utc = NULL
                WHERE id = @id
                  AND (
                      status = 'pending'
                      OR (
                          status LIKE 'retry_pending%'
                          AND (
                              next_attempt_utc IS NULL
                              OR next_attempt_utc <= @now
                          )
                      )
                  );";

            command.Parameters.AddWithValue(
                "@now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("@id", id);

            return await command.ExecuteNonQueryAsync(token) == 1;
        }

        private async Task ProcessJobAsync(
            QueueJob job, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(job.FilePath)
                || !File.Exists(job.FilePath))
            {
                await UpdateJobAsync(
                    job.Id, "failed", null,
                    "Local video file is missing.",
                    job.RetryCount, null, token);
                return;
            }

            bool uploadStarted = false;

            try
            {
                string youtubeId = await _uploadService.UploadVideoAsync(
                    job.FilePath,
                    job.Title,
                    job.Description,
                    job.ThumbnailPath,
                    job.ScheduledTime,
                    async () =>
                    {
                        uploadStarted = true;
                        await MarkUploadStartedAsync(job.Id, token);
                    });

                if (string.IsNullOrWhiteSpace(youtubeId))
                    throw new Exception("YouTube video ID is empty.");

                await UpdateJobAsync(
                    job.Id, "completed", youtubeId,
                    null, job.RetryCount, null, token);

                // Delete only after confirmed database completion.
                try
                {
                    if (File.Exists(job.FilePath))
                        File.Delete(job.FilePath);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Video cleanup warning: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                if (uploadStarted)
                {
                    await UpdateJobAsync(
                        job.Id,
                        "needs_review",
                        null,
                        "Upload outcome is uncertain. Check YouTube Studio before retrying. Details: " + ex.Message,
                        job.RetryCount,
                        null,
                        CancellationToken.None);
                    return;
                }

                int retries = job.RetryCount + 1;

                if (retries >= 3)
                {
                    await UpdateJobAsync(
                        job.Id, "failed", null,
                        ex.Message, retries, null,
                        CancellationToken.None);
                }
                else
                {
                    int delayMinutes = retries == 1 ? 5 :
                        retries == 2 ? 15 : 60;

                    string nextAttempt = DateTimeOffset.UtcNow
                        .AddMinutes(delayMinutes).ToString("O");

                    await UpdateJobAsync(
                        job.Id,
                        $"retry_pending ({retries}/3)",
                        null,
                        ex.Message,
                        retries,
                        nextAttempt,
                        CancellationToken.None);
                }
            }
        }

        private static async Task MarkUploadStartedAsync(
            long id, CancellationToken token)
        {
            using var connection = new SqliteConnection(
                $"Data Source={AppPaths.Database}");

            await connection.OpenAsync(token);

            using var command = connection.CreateCommand();

            command.CommandText = @"
                UPDATE upload_queue
                SET upload_started_utc = @now
                WHERE id = @id AND status = 'processing';";

            command.Parameters.AddWithValue(
                "@now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("@id", id);

            if (await command.ExecuteNonQueryAsync(token) != 1)
                throw new Exception(
                    "Could not record upload start. Upload was not attempted.");
        }

        private static async Task UpdateJobAsync(
            long id,
            string status,
            string? youtubeId,
            string? error,
            int retries,
            string? nextAttempt,
            CancellationToken token)
        {
            using var connection = new SqliteConnection(
                $"Data Source={AppPaths.Database}");

            await connection.OpenAsync(token);

            using var command = connection.CreateCommand();

            command.CommandText = @"
                UPDATE upload_queue
                SET status = @status,
                    youtube_id = COALESCE(@youtubeId, youtube_id),
                    error_message = @error,
                    retry_count = @retries,
                    next_attempt_utc = @nextAttempt,
                    processing_started_utc = NULL
                WHERE id = @id;";

            command.Parameters.AddWithValue("@status", status);
            command.Parameters.AddWithValue(
                "@youtubeId", (object?)youtubeId ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "@error", (object?)error ?? DBNull.Value);
            command.Parameters.AddWithValue("@retries", retries);
            command.Parameters.AddWithValue(
                "@nextAttempt", (object?)nextAttempt ?? DBNull.Value);
            command.Parameters.AddWithValue("@id", id);

            await command.ExecuteNonQueryAsync(token);
        }

        private sealed record QueueJob(
            long Id,
            string FilePath,
            string Title,
            string Description,
            string ThumbnailPath,
            string ScheduledTime,
            int RetryCount);
    }
}


