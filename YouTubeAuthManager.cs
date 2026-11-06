using System.IO;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Util.Store;
using Google.Apis.YouTube.v3;

namespace SMusicBoxAutoScheduler
{
    public static class YouTubeAuthManager
    {
        public static async Task<UserCredential> GetCredentialsAsync()
        {
            AppPaths.EnsureDirectories();

            if (!File.Exists(AppPaths.ClientSecret))
                throw new FileNotFoundException(
                    "Google client_secret.json is missing.",
                    AppPaths.ClientSecret);

            using var stream = new FileStream(
                AppPaths.ClientSecret,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            return await GoogleWebAuthorizationBroker.AuthorizeAsync(
                GoogleClientSecrets.FromStream(stream).Secrets,
                new[] { YouTubeService.Scope.YoutubeUpload },
                "smusicbox_user",
                CancellationToken.None,
                new FileDataStore(AppPaths.Tokens, true));
        }
    }
}
