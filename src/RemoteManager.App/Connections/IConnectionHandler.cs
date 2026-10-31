using Avalonia.Controls;
using Avalonia.Media;
using RemoteManager.App.Views;
using RemoteManager.Core.Model;
using RemoteManager.Core.Status;

namespace RemoteManager.App.Connections;

/// <summary>
/// Everything the app knows about one <see cref="ConnectionType"/>: how it looks, which fields the
/// editor shows, its settings editor, its status probe, and how it connects. One handler per type;
/// <see cref="ConnectionHandlerRegistry"/> finds them (public, non-abstract, parameterless), so a new
/// type is a new folder under Connections/ and no edit to the main window, editor, or registry.
/// Derive from <see cref="ConnectionHandler"/> for the defaults.
/// </summary>
public interface IConnectionHandler
{
    ConnectionType Type { get; }

    /// <summary>Type badge text ("RDP", "TeamViewer").</summary>
    string DisplayName { get; }

    /// <summary>Short text in the round icon when there is no preview ("RDP", "TV").</summary>
    string Initials { get; }

    Color AccentColor { get; }

    /// <summary>Placeholder of the host field ("Host oder IP", "Remote-ID oder Alias").</summary>
    string HostPlaceholder { get; }

    /// <summary>Whether the editor shows the port field (with <see cref="DefaultPorts"/> as placeholder).</summary>
    bool HasPort { get; }

    /// <summary>Whether the editor shows user and domain (remote tools and VNC only use a password).</summary>
    bool UsesUserName { get; }

    /// <summary>Explains below the password field what the stored password is used for.</summary>
    string PasswordHint { get; }

    /// <summary>Tooltip of the status pill: how the status is found out.</summary>
    string StatusHint { get; }

    /// <summary>Probe for the background status check, or null (status stays "Unbekannt").</summary>
    IStatusProbe? StatusProbe { get; }

    /// <summary>Editor for the type's <see cref="Core.Model.Settings.ConnectionSettings"/>, or null when there is nothing to set.</summary>
    ISettingsEditor? CreateSettingsEditor();

    /// <summary>
    /// Connects. An embedded session returns its window (the host tracks it, reuses it on the next
    /// open, and closes it with the app); an external launch or dialog returns null.
    /// </summary>
    Window? Open(ConnectionItem connection, IConnectionHost host);
}
