using System.Globalization;
using Avalonia.Input;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Connections;
using RemoteManager.App.Resources;
using RemoteManager.App.Services;
using RemoteManager.Core.Model;
using RemoteManager.Core.Search;

namespace RemoteManager.App.Views;

// The rows of the quick launcher next to the connections themselves (ConnectionItem): section labels,
// app commands, and targets typed for a one-time connection.

/// <summary>A label above a group of rows ("Zuletzt verwendet", "Aktionen"). Never selected.</summary>
public sealed record LauncherSection(string Title);

/// <summary>
/// An app command the quick launcher offers under "Aktionen". It runs the same code as its entry in the
/// "Mehr" menu or the header (MainWindow.Launcher.cs builds the list).
/// </summary>
public sealed class LauncherCommand
{
    /// <summary>Stable name for the self-test and logs ("view.list", "theme.dark").</summary>
    public required string Id { get; init; }

    /// <summary>What the row says; the menu's label where the command has a menu entry.</summary>
    public required string Label { get; init; }

    public required FASymbol Symbol { get; init; }

    /// <summary>Extra search words, separated by spaces: synonyms and the English terms.</summary>
    public string Keywords { get; init; } = "";

    /// <summary>The command's shortcut in the main window, shown as key caps.</summary>
    public KeyGesture? Gesture { get; init; }

    /// <summary>For a choice (theme, tile size, view): whether it is the one in use, marked "Aktiv".</summary>
    public Func<bool>? IsActive { get; init; }

    /// <summary>The command needs the overview in front: it opens a dialog there or changes what it shows.</summary>
    public bool ShowsOverview { get; init; }

    /// <summary>The command opens a dialog in the overview; it does not run while another one is open there.</summary>
    public bool OpensDialog { get; init; }

    /// <summary>The launcher stays open (the command continues inside it).</summary>
    public bool KeepsLauncherOpen { get; init; }

    public required Func<Task> Run { get; init; }

    public IReadOnlyList<string> Keys => Gesture is { } gesture ? GlobalHotkey.Format(gesture).Split('+') : [];
    public bool HasKeys => Gesture is not null;
    public bool IsCurrent => IsActive?.Invoke() == true;

    /// <summary>The label counts most, a keyword half as much.</summary>
    public IEnumerable<SearchField> SearchFields =>
        [new(Label, 4), .. Keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(k => new SearchField(k, 2))];
}

/// <summary>
/// "Direkt verbinden mit 10.0.0.5 (RDP)": one type for a target typed into the launcher. Running it opens a
/// session for a connection that is never saved to the library.
/// </summary>
public sealed class LauncherTarget
{
    private readonly IConnectionHandler handler;

    public LauncherTarget(AdHocTarget target, ConnectionType type, IConnectionHandler handler)
    {
        Target = target;
        Type = type;
        this.handler = handler;
    }

    public AdHocTarget Target { get; }
    public ConnectionType Type { get; }

    /// <summary>The whole row as a sentence (screen readers, self-test).</summary>
    public string Label => string.Format(CultureInfo.CurrentCulture, Strings.Launcher_Direct_Label, Target.Label, TypeLabel);

    /// <summary>"root@10.0.0.5:2222", in the mono face like every host.</summary>
    public string TargetLabel => Target.Label;

    public string TypeLabel => handler.DisplayName;
    public IBrush TypeBrush => new SolidColorBrush(handler.AccentColor);

    /// <summary>The port the session will use, so a default is visible before connecting.</summary>
    public string PortLabel => string.Format(CultureInfo.CurrentCulture, Strings.Launcher_Direct_Port,
        Target.Port > 0 ? Target.Port : DefaultPorts.For(Type));
}
