using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Resources;
using RemoteManager.App.Services;
using RemoteManager.Core.Model;

namespace RemoteManager.App.Views;

/// <summary>One piece of a shortcut: a key cap ("Strg") or the text between two caps ("+").</summary>
public sealed record ShortcutPart(string Text, bool IsKey)
{
    public bool IsJoin => !IsKey;
}

/// <summary>One line of the shortcut overview: what happens, and the keys for it.</summary>
public sealed class ShortcutRow
{
    public required string Label { get; init; }
    public required IReadOnlyList<ShortcutPart> Parts { get; init; }

    /// <summary>The connection type a session shortcut belongs to ("RDP"), drawn as a plate in the type's color.</summary>
    public string? Chip { get; init; }
    public IBrush? ChipBrush { get; init; }
    public bool HasChip => Chip is not null;

    /// <summary>A line above every row but the first of its area.</summary>
    public Thickness Divider { get; set; }

    /// <summary>"Strg+Umschalt+N": the keys as one text, as a menu shows them.</summary>
    public string GestureText => string.Concat(Parts.Select(p => p.Text));
}

/// <summary>The shortcuts of one part of the app.</summary>
public sealed record ShortcutArea(string Title, IReadOnlyList<ShortcutRow> Rows, string? Hint = null)
{
    public bool HasHint => Hint is not null;
}

/// <summary>
/// "Tastenkürzel" (F1, "Mehr" menu): every shortcut of the app, grouped by where it works. The list is written by
/// hand next to the code that handles the keys: MainWindow.OnKeyDown and the list KeyDown handler (overview and
/// connection), MainWindow.Groups.cs (sidebar), QuickLauncherWindow.OnPreviewKeyDown (launcher), terminal.html and
/// SftpWindow (sessions). A new shortcut needs a line in <see cref="Create"/>; the self-test compares the list with
/// the gestures the menus show.
/// </summary>
public partial class ShortcutsView : UserControl
{
    public ShortcutsView() : this(null)
    {
    }

    public ShortcutsView(KeyGesture? globalHotkey)
    {
        InitializeComponent();
        Areas = Create(globalHotkey);
        LeftColumn.ItemsSource = Areas.Take(2);
        RightColumn.ItemsSource = Areas.Skip(2);
    }

    /// <summary>The dialog and its content while open (self-test, and F1 does not open a second one).</summary>
    public static FAContentDialog? OpenDialog { get; private set; }
    public static ShortcutsView? OpenContent { get; private set; }

    public IReadOnlyList<ShortcutArea> Areas { get; }

    public IEnumerable<ShortcutRow> Rows => Areas.SelectMany(a => a.Rows);

    /// <summary>Shows the overview in <paramref name="window"/>; the launcher row shows the hotkey that is registered right now.</summary>
    public static async Task ShowAsync(MainWindow window)
    {
        if (OpenDialog is not null)
            return;

        var content = new ShortcutsView(window.Hotkey?.Gesture);
        var dialog = new FAContentDialog
        {
            Title = Strings.Shortcuts_Title,
            Content = content,
            CloseButtonText = Strings.Action_Close,
            DefaultButton = FAContentDialogButton.Close,
        };
        // Two columns need more room than the default dialog width.
        dialog.Resources["ContentDialogMaxWidth"] = 860.0;

        OpenDialog = dialog;
        OpenContent = content;
        try
        {
            // Named window: without it the dialog opens in whichever window of the app is active.
            await dialog.ShowAsync(window);
        }
        finally
        {
            OpenDialog = null;
            OpenContent = null;
        }
    }

    private static IReadOnlyList<ShortcutArea> Create(KeyGesture? globalHotkey)
    {
        const KeyModifiers Ctrl = KeyModifiers.Control, Shift = KeyModifiers.Shift, Alt = KeyModifiers.Alt;

        ShortcutArea[] areas =
        [
            // MainWindow.OnKeyDown
            new(Strings.Shortcuts_Area_Overview,
            [
                Row(Strings.Shortcuts_Search, new KeyGesture(Key.F, Ctrl)),
                Row(Strings.Shortcuts_NewConnection, new KeyGesture(Key.N, Ctrl)),
                Row(Strings.Shortcuts_NewGroup, new KeyGesture(Key.N, Ctrl | Shift)),
                Row(Strings.Shortcuts_Tiles, new KeyGesture(Key.D1, Ctrl)),
                Row(Strings.Shortcuts_List, new KeyGesture(Key.D2, Ctrl)),
                Row(Strings.Shortcuts_Sidebar, new KeyGesture(Key.B, Ctrl)),
                Row(Strings.Shortcuts_Details, new KeyGesture(Key.I, Ctrl)),
                Row(Strings.Shortcuts_Refresh, new KeyGesture(Key.F5)),
                Row(Strings.Shortcuts_Help, new KeyGesture(Key.F1)),
            ]),
            // The KeyDown handler of the tile and row list, Ctrl+A of the list box itself, MainWindow.Groups.cs.
            new(Strings.Shortcuts_Area_Connection,
            [
                Row(Strings.Shortcuts_Connect, new KeyGesture(Key.Enter)),
                Row(Strings.Shortcuts_Edit, new KeyGesture(Key.F2)),
                Row(Strings.Shortcuts_Delete, new KeyGesture(Key.Delete)),
                Row(Strings.Shortcuts_SelectAll, new KeyGesture(Key.A, Ctrl)),
                Row(Strings.Shortcuts_SidebarDelete, new KeyGesture(Key.Delete)),
            ], Strings.Shortcuts_Area_Connection_Hint),
            // GlobalHotkey, MainWindow.OnKeyDown (Ctrl+K), QuickLauncherWindow.OnPreviewKeyDown
            new(Strings.Shortcuts_Area_Launcher,
            [
                globalHotkey is null
                    ? new ShortcutRow { Label = Strings.Shortcuts_LauncherGlobal, Parts = [new(Strings.Launcher_HotkeyNone, false)] }
                    : Row(Strings.Shortcuts_LauncherGlobal, globalHotkey),
                Row(Strings.Shortcuts_LauncherLocal, new KeyGesture(Key.K, Ctrl)),
                new ShortcutRow { Label = Strings.Shortcuts_LauncherChoose, Parts = [new(KeyCaps.Name(Key.Up), true), new(KeyCaps.Name(Key.Down), true)] },
                Row(Strings.Shortcuts_LauncherConnect, new KeyGesture(Key.Enter)),
                Row(Strings.Shortcuts_LauncherReveal, new KeyGesture(Key.Enter, Ctrl)),
                Row(Strings.Shortcuts_LauncherClose, new KeyGesture(Key.Escape)),
            ], Strings.Shortcuts_Area_Launcher_Hint),
            // The RDP client's own full-screen key, terminal.html (xterm key handler), SftpWindow (Entries.KeyDown)
            new(Strings.Shortcuts_Area_Session,
            [
                Row(Strings.Shortcuts_RdpFullScreen, new KeyGesture(Key.Pause, Ctrl | Alt), ConnectionType.Rdp),
                Row(Strings.Shortcuts_TerminalCopy, new KeyGesture(Key.C, Ctrl | Shift), ConnectionType.Ssh),
                Row(Strings.Shortcuts_TerminalPaste, new KeyGesture(Key.V, Ctrl | Shift), ConnectionType.Ssh),
                Row(Strings.Shortcuts_TerminalPasteAlt, new KeyGesture(Key.Insert, Shift), ConnectionType.Ssh),
                Row(Strings.Shortcuts_SftpOpen, new KeyGesture(Key.Enter), ConnectionType.Sftp),
                Row(Strings.Shortcuts_SftpUp, new KeyGesture(Key.Back), ConnectionType.Sftp),
                Row(Strings.Shortcuts_SftpDelete, new KeyGesture(Key.Delete), ConnectionType.Sftp),
            ]),
        ];

        foreach (ShortcutArea area in areas)
        {
            foreach (ShortcutRow row in area.Rows.Skip(1))
                row.Divider = new Thickness(0, 1, 0, 0);
        }

        return areas;
    }

    private static ShortcutRow Row(string label, KeyGesture gesture, ConnectionType? type = null)
    {
        var parts = new List<ShortcutPart>();
        foreach (string cap in KeyCaps.For(gesture))
        {
            if (parts.Count > 0)
                parts.Add(new ShortcutPart("+", false));
            parts.Add(new ShortcutPart(cap, true));
        }

        if (type is not { } t)
            return new ShortcutRow { Label = label, Parts = parts };

        var handler = AppHost.Current.Handlers[t];
        return new ShortcutRow { Label = label, Parts = parts, Chip = handler.DisplayName, ChipBrush = new SolidColorBrush(handler.AccentColor) };
    }
}
