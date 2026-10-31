namespace RemoteManager.Core;

/// <summary>
/// Where Remote-Manager keeps its data: %LOCALAPPDATA%\RemoteManager. The environment variable
/// REMOTE_MANAGER_DATA points everything at another folder (tests, a second profile).
/// </summary>
public static class AppPaths
{
    public static string Root { get; } =
        Environment.GetEnvironmentVariable("REMOTE_MANAGER_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteManager");

    public static string Database => Path.Combine(Root, "remote-manager.db");

    /// <summary>The store before the SQLite database; migrated once, then renamed to .bak.</summary>
    public static string LegacyJson => Path.Combine(Root, "connections.json");

    public static string Thumbnails => Ensure(Path.Combine(Root, "thumbnails"));
    public static string Spike => Ensure(Path.Combine(Root, "spike"));

    /// <summary>Database copies taken before an update (see Updates/DatabaseBackup).</summary>
    public static string Backups => Path.Combine(Root, "backups");

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
