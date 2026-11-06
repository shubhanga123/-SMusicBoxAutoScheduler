using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace SMusicBoxAutoScheduler
{
    public class VideoIngestService
    {
        public async Task<bool> IngestYouTubeLinkCustomScheduleAsync(
            string videoUrl,
            string customScheduleNpt,
            Action<string, int> progressCallback)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(videoUrl))
                    throw new ArgumentException("Video URL is required.");

                if (!Uri.TryCreate(videoUrl, UriKind.Absolute, out var uri)
                    || !(uri.Host == "youtube.com"
                        || uri.Host.EndsWith(".youtube.com")
                        || uri.Host == "youtu.be"))
                    throw new ArgumentException("Enter a valid YouTube URL.");

                if (!DateTimeOffset.TryParse(
                    customScheduleNpt, out var scheduledTime))
                    throw new ArgumentException("Invalid scheduled date and time.");

                if (scheduledTime <= DateTimeOffset.Now.AddMinutes(2))
                    throw new ArgumentException(
                        "Schedule must be at least two minutes in the future.");

                AppPaths.EnsureDirectories();

                string downloadDirectory = AppPaths.Downloads;
                Directory.CreateDirectory(downloadDirectory);

                using (var connection = new SqliteConnection(
                    $"Data Source={AppPaths.Database}"))
                {
                    await connection.OpenAsync();

                    using var command = connection.CreateCommand();
                    command.CommandText = @"
                        SELECT COUNT(*)
                        FROM upload_queue
                        WHERE description LIKE @url
                          AND status != 'failed';";

                    command.Parameters.AddWithValue("@url", $"%{videoUrl}%");

                    long count = Convert.ToInt64(
                        await command.ExecuteScalarAsync() ?? 0L);

                    if (count > 0)
                    {
                        progressCallback(
                            "This video already exists in your queue.", 0);
                        return false;
                    }
                }

                // Record existing MP4 files so we can identify the new download.
                var existingFiles = Directory.GetFiles(
                    downloadDirectory, "*.mp4", SearchOption.TopDirectoryOnly)
                    .ToDictionary(
                        path => path,
                        path => new FileInfo(path).Length,
                        StringComparer.OrdinalIgnoreCase);

                DateTime downloadStarted = DateTime.Now;

                progressCallback("Preparing authorized video download...", 5);

                var startInfo = new ProcessStartInfo
                {
                    FileName = "yt-dlp",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                startInfo.ArgumentList.Add("--no-playlist");
                startInfo.ArgumentList.Add("--no-mtime");
                startInfo.ArgumentList.Add("--write-thumbnail");
                startInfo.ArgumentList.Add("--convert-thumbnails");
                startInfo.ArgumentList.Add("jpg");
                startInfo.ArgumentList.Add("--merge-output-format");
                startInfo.ArgumentList.Add("mp4");
                startInfo.ArgumentList.Add("--newline");
                startInfo.ArgumentList.Add("--progress");
                startInfo.ArgumentList.Add("--print");
                startInfo.ArgumentList.Add("after_move:filepath");
                startInfo.ArgumentList.Add("-o");
                startInfo.ArgumentList.Add(
                    Path.Combine(downloadDirectory, "%(title)s.%(ext)s"));
                startInfo.ArgumentList.Add(videoUrl);

                using var process = new Process { StartInfo = startInfo };

                progressCallback("Starting downloader...", 8);

                if (!process.Start())
                    throw new Exception("Could not start yt-dlp.");

                string? printedPath = null;
                var stderrTask = process.StandardError.ReadToEndAsync();

                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromHours(2));

                try
                {
                    while (true)
                    {
                        string? line = await process.StandardOutput.ReadLineAsync(
                            timeout.Token);

                        if (line == null)
                            break;

                        line = line.Trim();

                        // Capture a printed path, including quoted paths.
                        string possiblePath = line.Trim('"');

                        if (Path.IsPathRooted(possiblePath))
                            printedPath = possiblePath;

                        var match = Regex.Match(
                            line, @"(\d{1,3}(?:\.\d+)?)%");

                        if (match.Success &&
                            double.TryParse(
                                match.Groups[1].Value,
                                CultureInfo.InvariantCulture,
                                out double percent))
                        {
                            int progress = (int)Math.Clamp(percent, 8, 90);

                            progressCallback(
                                $"Downloading: {percent:0.0}%", progress);
                        }
                    }

                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(true); } catch { }

                    throw new TimeoutException(
                        "Download exceeded the two-hour time limit.");
                }

                string errorOutput = await stderrTask;

                if (process.ExitCode != 0)
                    throw new Exception(
                        string.IsNullOrWhiteSpace(errorOutput)
                            ? "yt-dlp download failed."
                            : errorOutput);

                // Detection method 1: exact path printed by yt-dlp.
                string? downloadedPath = null;

                if (!string.IsNullOrWhiteSpace(printedPath))
                {
                    string candidate = printedPath.Trim().Trim('"');

                    if (File.Exists(candidate))
                        downloadedPath = Path.GetFullPath(candidate);
                }

                // Detection method 2: find a newly created or changed MP4.
                if (downloadedPath == null)
                {
                    var candidates = Directory.GetFiles(
                        downloadDirectory, "*.mp4",
                        SearchOption.TopDirectoryOnly)
                        .Select(path => new FileInfo(path))
                        .Where(info =>
                        {
                            bool existedBefore = existingFiles.TryGetValue(
                                info.FullName, out long oldLength);

                            return !existedBefore
                                || info.Length != oldLength
                                || info.LastWriteTime >= downloadStarted.AddSeconds(-5);
                        })
                        .OrderByDescending(info => info.LastWriteTime)
                        .ToList();

                    downloadedPath = candidates.FirstOrDefault()?.FullName;
                }

                if (string.IsNullOrWhiteSpace(downloadedPath)
                    || !File.Exists(downloadedPath))
                    throw new Exception(
                        "Download completed, but no new MP4 file could be identified.");

                var videoInfo = new FileInfo(downloadedPath);

                if (videoInfo.Length == 0)
                    throw new Exception("Downloaded video file is empty.");

                progressCallback(
                    $"Video detected: {videoInfo.Name}", 93);

                string title = Path.GetFileNameWithoutExtension(downloadedPath);

                string baseName = Path.Combine(
                    Path.GetDirectoryName(downloadedPath)!,
                    title);

                string thumbnailPath = new[] { ".jpg", ".png", ".webp" }
                    .Select(extension => baseName + extension)
                    .FirstOrDefault(File.Exists) ?? "";

                progressCallback(
                    "Registering video in the central queue...", 97);

                using (var connection = new SqliteConnection(
                    $"Data Source={AppPaths.Database}"))
                {
                    await connection.OpenAsync();

                    using var command = connection.CreateCommand();

                    command.CommandText = @"
                        INSERT INTO upload_queue
                        (
                            file_path,
                            title,
                            description,
                            thumbnail_path,
                            scheduled_time,
                            status,
                            retry_count
                        )
                        VALUES
                        (
                            @path,
                            @title,
                            @description,
                            @thumbnail,
                            @scheduled,
                            'pending',
                            0
                        );";

                    command.Parameters.AddWithValue("@path", downloadedPath);
                    command.Parameters.AddWithValue("@title", title);
                    command.Parameters.AddWithValue(
                        "@description", $"Source URL: {videoUrl}");

                    command.Parameters.AddWithValue(
                        "@thumbnail",
                        string.IsNullOrEmpty(thumbnailPath)
                            ? (object)DBNull.Value
                            : thumbnailPath);

                    command.Parameters.AddWithValue(
                        "@scheduled", scheduledTime.ToString("O"));

                    await command.ExecuteNonQueryAsync();
                }

                progressCallback(
                    "Video successfully added to upload queue!", 100);

                return true;
            }
            catch (Exception ex)
            {
                progressCallback(
                    $"Ingestion failed: {ex.Message}", 0);

                return false;
            }
        }
    }
}
