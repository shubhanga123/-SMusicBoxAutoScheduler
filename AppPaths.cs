using System;
using System.IO;

namespace SMusicBoxAutoScheduler;

public static class AppPaths
{
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SMusicBoxAutoScheduler");

    public static readonly string DatabaseDirectory =
        Path.Combine(Root, "Data", "Database");

    public static readonly string Database =
        Path.Combine(DatabaseDirectory, "smusicbox.db");

    public static readonly string Downloads =
        Path.Combine(Root, "Data", "Downloads");

    public static readonly string Credentials =
        Path.Combine(Root, "Data", "Credentials");

    public static readonly string ClientSecret =
        Path.Combine(Credentials, "client_secret.json");

    public static readonly string Tokens =
        Path.Combine(Credentials, "token");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DatabaseDirectory);
        Directory.CreateDirectory(Downloads);
        Directory.CreateDirectory(Credentials);
    }
}
