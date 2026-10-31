using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;
using RemoteManager.Core.Model.Settings;

namespace RemoteManager.App.Connections.Vnc;

/// <summary>An installed VNC viewer and how it takes "nur ansehen".</summary>
/// <param name="ViewOnlyArgument">Switch for view-only mode, or null when the viewer has none on its command line.</param>
public sealed record VncViewer(string Name, string Path, string? ViewOnlyArgument);

/// <summary>
/// The external fallback: starts TightVNC, TigerVNC, UltraVNC, or RealVNC Viewer with "host::port". The
/// password is never put on the command line (every process could read it there); when one is stored it goes
/// to the clipboard (cleared after 30 s) for the viewer's password prompt.
/// </summary>
public static class ExternalVncViewer
{
    /// <summary>app_settings key with a viewer path that wins over the search (optional).</summary>
    public const string PathSetting = "vnc.viewerPath";

    /// <summary>Overrides the search (used by the self-test).</summary>
    internal static Func<VncViewer?>? FindOverride { get; set; }

    /// <summary>The first installed viewer: the configured one, then TightVNC, TigerVNC, UltraVNC, RealVNC.</summary>
    public static VncViewer? Find()
    {
        if (FindOverride is { } find)
            return find();

        string? configured = AppHost.Current.Library.GetSetting(PathSetting);
        if (configured is { Length: > 0 } && File.Exists(configured))
            return new VncViewer(System.IO.Path.GetFileNameWithoutExtension(configured), configured, null);

        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];
        (string Name, string RelativePath, string? ViewOnly)[] known =
        [
            ("TightVNC Viewer", @"TightVNC\tvnviewer.exe", "-viewonly=yes"),
            ("TigerVNC Viewer", @"TigerVNC\vncviewer.exe", "-ViewOnly"),
            ("UltraVNC Viewer", @"uvnc bvba\UltraVNC\vncviewer.exe", "-viewonly"),
            ("UltraVNC Viewer", @"UltraVNC\vncviewer.exe", "-viewonly"),
            ("RealVNC Viewer", @"RealVNC\VNC Viewer\vncviewer.exe", "-ViewOnly"),
        ];
        foreach ((string name, string relative, string? viewOnly) in known)
        {
            foreach (string root in roots.Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string path = System.IO.Path.Combine(root, relative);
                if (File.Exists(path))
                    return new VncViewer(name, path, viewOnly);
            }
        }

        return null;
    }

    /// <summary>Arguments for <paramref name="viewer"/>: the target and, if wanted and known, view-only.</summary>
    public static IReadOnlyList<string> ArgumentsFor(VncViewer viewer, string host, int port, VncSettings settings)
    {
        var arguments = new List<string>();
        if (settings.ViewOnly && viewer.ViewOnlyArgument is { } viewOnly)
            arguments.Add(viewOnly);
        // "host::port" is understood by all four viewers; IPv6 needs brackets.
        string target = host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[') ? $"[{host}]" : host;
        arguments.Add($"{target}::{port.ToString(CultureInfo.InvariantCulture)}");
        return arguments;
    }

    public static async Task LaunchAsync(ConnectionItem connection, IConnectionHost host)
    {
        if (Find() is not { } viewer)
        {
            host.ShowToast(connection.Name, Strings.Vnc_ViewerMissing, FAInfoBarSeverity.Warning);
            return;
        }

        var start = new ProcessStartInfo(viewer.Path) { UseShellExecute = false };
        foreach (string argument in ArgumentsFor(viewer, connection.Model.Host.Trim(), connection.Model.EffectivePort, connection.Model.SettingsAs<VncSettings>()))
            start.ArgumentList.Add(argument);
        try
        {
            using Process? process = Process.Start(start);
        }
        catch (Win32Exception ex)
        {
            host.ShowToast(connection.Name, string.Format(CultureInfo.CurrentCulture, Strings.Vnc_ViewerFailed, ex.Message), FAInfoBarSeverity.Error);
            return;
        }

        Trace.WriteLine($"[VNC {connection.Name}] started {viewer.Name}");
        host.MarkConnected(connection);
        if (connection.HasStoredPassword)
            await host.CopyPasswordAsync(connection);
        else
            host.ShowToast(connection.Name, string.Format(CultureInfo.CurrentCulture, Strings.Vnc_ViewerStarted, viewer.Name), FAInfoBarSeverity.Success);
    }
}
