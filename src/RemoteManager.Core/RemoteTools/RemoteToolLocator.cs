using Microsoft.Win32;
using RemoteManager.Core.Model;

namespace RemoteManager.Core.RemoteTools;

/// <summary>Where a tool's executable is and how it was found.</summary>
/// <param name="Path">The executable, or null when none was found.</param>
/// <param name="FromSetting">The path comes from the user's setting (not detected).</param>
/// <param name="ConfiguredPathMissing">A path is set but the file is gone; detection was used instead.</param>
public sealed record ToolLocation(string? Path, bool FromSetting, bool ConfiguredPathMissing)
{
    public bool Found => Path is not null;
}

/// <summary>Static facts of a tool: executable name, install folders, registry names, download page.</summary>
public sealed record RemoteToolInfo(ConnectionType Type, string ProductName, string ExeName, string[] Folders, string DownloadUrl)
{
    /// <summary>Key in app_settings for the user's path (never a secret).</summary>
    public string SettingKey => $"tools.path.{Type}";
}

/// <summary>
/// Finds TeamViewer, AnyDesk, and RustDesk: the path set by the user wins (if the file exists), then
/// the registry (App Paths, uninstall entries), then the usual install folders.
/// </summary>
public sealed class RemoteToolLocator
{
    public static readonly IReadOnlyList<RemoteToolInfo> Tools =
    [
        new(ConnectionType.TeamViewer, "TeamViewer", "TeamViewer.exe", ["TeamViewer"], "https://www.teamviewer.com/de/download/windows/"),
        new(ConnectionType.AnyDesk, "AnyDesk", "AnyDesk.exe", ["AnyDesk"], "https://anydesk.com/de/downloads/windows"),
        new(ConnectionType.RustDesk, "RustDesk", "rustdesk.exe", ["RustDesk"], "https://rustdesk.com/download"),
    ];

    private readonly Func<string, string?> getSetting;
    private readonly Func<string, bool> fileExists;
    private readonly Func<RemoteToolInfo, IEnumerable<string>> candidates;

    /// <param name="getSetting">Reads app_settings (the store).</param>
    /// <param name="fileExists">File check (tests pass a fake).</param>
    /// <param name="candidates">Detected candidates; default: registry plus Program Files.</param>
    public RemoteToolLocator(Func<string, string?> getSetting, Func<string, bool>? fileExists = null,
        Func<RemoteToolInfo, IEnumerable<string>>? candidates = null)
    {
        this.getSetting = getSetting;
        this.fileExists = fileExists ?? File.Exists;
        this.candidates = candidates ?? DefaultCandidates;
    }

    public static RemoteToolInfo InfoFor(ConnectionType type) =>
        Tools.FirstOrDefault(t => t.Type == type) ?? throw new ArgumentOutOfRangeException(nameof(type), type, "Not a remote tool.");

    public ToolLocation Locate(ConnectionType type)
    {
        RemoteToolInfo info = InfoFor(type);
        string? configured = getSetting(info.SettingKey);
        if (configured is { Length: > 0 } && fileExists(configured))
            return new ToolLocation(configured, true, false);

        string? detected = Detect(type);
        return new ToolLocation(detected, false, configured is { Length: > 0 });
    }

    /// <summary>The first detected installation, ignoring the user's setting.</summary>
    public string? Detect(ConnectionType type) => candidates(InfoFor(type)).FirstOrDefault(fileExists);

    private static IEnumerable<string> DefaultCandidates(RemoteToolInfo info)
    {
        foreach (string path in RegistryCandidates(info))
            yield return path;

        foreach (Environment.SpecialFolder root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData })
        {
            string rootPath = Environment.GetFolderPath(root);
            if (rootPath.Length == 0)
                continue;
            foreach (string folder in info.Folders)
                yield return Path.Combine(rootPath, folder, info.ExeName);
            if (root == Environment.SpecialFolder.LocalApplicationData)
                yield return Path.Combine(rootPath, "Programs", info.ProductName, info.ExeName);
        }
    }

    private static List<string> RegistryCandidates(RemoteToolInfo info)
    {
        var result = new List<string>();
        if (!OperatingSystem.IsWindows())
            return result;

        try
        {
            foreach (RegistryKey hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using (RegistryKey? appPath = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{info.ExeName}"))
                {
                    if (appPath?.GetValue(null) is string exe)
                        result.Add(exe.Trim('"'));
                }

                foreach (string uninstall in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                {
                    using RegistryKey? root = hive.OpenSubKey(uninstall);
                    if (root is null)
                        continue;
                    foreach (string name in root.GetSubKeyNames())
                    {
                        using RegistryKey? entry = root.OpenSubKey(name);
                        if (entry?.GetValue("DisplayName") is not string display || !display.StartsWith(info.ProductName, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (entry.GetValue("InstallLocation") is string { Length: > 0 } location)
                            result.Add(Path.Combine(location.Trim('"'), info.ExeName));
                        if (entry.GetValue("DisplayIcon") is string { Length: > 0 } icon)
                        {
                            string iconPath = icon.Split(',')[0].Trim('"');
                            if (iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                result.Add(Path.Combine(Path.GetDirectoryName(iconPath) ?? "", info.ExeName));
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // An unreadable registry key only means fewer candidates.
        }

        return result;
    }
}
