using System;
using System.IO;
using System.Threading.Tasks;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;

namespace SMusicBoxAutoScheduler
{
    public class YouTubeUploadService
    {
        public void ForceAuthenticationTest()
        {
            YouTubeAuthManager.GetCredentialsAsync()
                .GetAwaiter().GetResult();
        }

        public async Task SetThumbnailAsync(
            string videoId, string thumbnailPath)
        {
            if (string.IsNullOrWhiteSpace(videoId))
                throw new ArgumentException("YouTube video ID is required.");

            if (!File.Exists(thumbnailPath))
                throw new FileNotFoundException(
                    "Thumbnail file not found.", thumbnailPath);

            string mimeType = Path.GetExtension(thumbnailPath)
                .ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".webp" => "image/webp",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                _ => throw new InvalidOperationException(
                    "Unsupported thumbnail format.")
            };

            var credential =
                await YouTubeAuthManager.GetCredentialsAsync();

            using var youtube = new YouTubeService(
                new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "S-Music Box Auto Scheduler"
                });

            using var stream = new FileStream(
                thumbnailPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            var request = youtube.Thumbnails.Set(
                videoId, stream, mimeType);

            var result = await request.UploadAsync();

            if (result.Status != UploadStatus.Completed)
                throw new Exception(
                    result.Exception?.Message ??
                    "Thumbnail upload failed.");
        }

        public async Task<string> UploadVideoAsync(
            string filePath,
            string title,
            string description,
            string thumbnailPath,
            string scheduledTimeIso,
            Func<Task>? beforeUpload = null)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException(
                    "Video file not found.", filePath);

            if (!DateTimeOffset.TryParse(
                scheduledTimeIso, out var publishTime))
                throw new InvalidOperationException(
                    "Invalid scheduled publication date and time.");

            if (publishTime <= DateTimeOffset.UtcNow.AddMinutes(2))
                throw new InvalidOperationException(
                    "Publication time must be at least two minutes in the future.");

            var credential =
                await YouTubeAuthManager.GetCredentialsAsync();

            using var youtube = new YouTubeService(
                new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "S-Music Box Auto Scheduler"
                });

            var video = new Video
            {
                Snippet = new VideoSnippet
                {
                    Title = string.IsNullOrWhiteSpace(title)
                        ? "Untitled" : title,
                    Description = description ?? "",
                    CategoryId = "10"
                },
                Status = new VideoStatus
                {
                    PrivacyStatus = "private",
                    PublishAtDateTimeOffset =
                        publishTime.ToUniversalTime()
                }
            };

            string videoId;

            using (var stream = new FileStream(
                filePath, FileMode.Open,
                FileAccess.Read, FileShare.Read))
            {
                var request = youtube.Videos.Insert(
                    video, "snippet,status", stream, "video/*");

                if (beforeUpload != null)
                    await beforeUpload();

                var result = await request.UploadAsync();

                if (result.Status != UploadStatus.Completed)
                    throw new Exception(
                        result.Exception?.Message ??
                        "YouTube upload failed.");

                videoId = request.ResponseBody?.Id
                    ?? throw new Exception(
                        "YouTube returned no video ID.");
            }

            if (!string.IsNullOrWhiteSpace(thumbnailPath) &&
                File.Exists(thumbnailPath))
            {
                try
                {
                    await SetThumbnailAsync(
                        videoId, thumbnailPath);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "Thumbnail upload warning: " + ex.Message);
                }
            }

            return videoId;
        }
    }
}
