using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Connections;
using RemoteManager.App.Resources;
using RemoteManager.App.Services;

namespace RemoteManager.App.Views;

/// <summary>The quick launcher and its global hotkey (setting "launcher.hotkey", default Ctrl+Alt+R).</summary>
public partial class MainWindow
{
    private const string HotkeySetting = "launcher.hotkey";

    private QuickLauncherWindow? launcher;

    public GlobalHotkey? Hotkey { get; private set; }
    public QuickLauncherWindow? QuickLauncher => launcher;

    /// <summary>Shows the launcher (hotkey, toolbar button, Ctrl+K).</summary>
    public void OpenLauncher()
    {
        launcher ??= new QuickLauncherWindow();
        launcher.ShowFor(this);
    }

    /// <summary>Registers <paramref name="gesture"/> as the global hotkey and saves it; false (old one kept) when Windows refuses it.</summary>
    public bool TrySetHotkey(KeyGesture gesture)
    {
        if (Hotkey is null)
            return false;
        KeyGesture? previous = Hotkey.Gesture;
        if (!Hotkey.Register(gesture))
        {
            if (previous is not null)
                Hotkey.Register(previous);
            return false;
        }

        app.Library.SetSetting(HotkeySetting, gesture.ToString());
        UpdateLauncherTip();
        return true;
    }

    /// <summary>Enter in the launcher: connect, and bring the overview up when the handler shows its dialog there.</summary>
    internal void LaunchFromLauncher(ConnectionItem connection)
    {
        OpenSession(connection);
        if (PlannedConnectionDialog.Current is not null)
            BringToFront();
    }

    /// <summary>Ctrl+Enter in the launcher: show the connection in the overview with its details.</summary>
    internal void Reveal(ConnectionItem connection)
    {
        BringToFront();
        if (filter != "all")
        {
            filter = "all";
            BuildNavigation();
        }

        SearchBox.Text = "";
        if (connection.Model.GroupId is { } groupId)
        {
            // Expand the sections on the way to it.
            foreach (var group in app.Library.AncestorsOf(groupId).Where(g => g.IsCollapsed).ToList())
            {
                group.IsCollapsed = false;
                app.Library.UpdateGroup(group);
            }

            BuildNavigation();
        }

        Select([connection]);
        ActiveList.ScrollIntoView(connection);
        SetDetailsVisible(true);
    }

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    private void StartHotkey()
    {
        if (Hotkey is not null)
            return;
        Hotkey = new GlobalHotkey(this);
        Hotkey.Pressed += (_, _) => OpenLauncher();
        KeyGesture gesture = GlobalHotkey.Parse(app.Library.GetSetting(HotkeySetting));
        if (!Hotkey.Register(gesture))
        {
            ShowToast(Strings.Launcher_Title, string.Format(CultureInfo.CurrentCulture, Strings.Launcher_HotkeyTaken, GlobalHotkey.Format(gesture)),
                FAInfoBarSeverity.Warning);
        }

        UpdateLauncherTip();
    }

    private void UpdateLauncherTip() =>
        ToolTip.SetTip(LauncherButton, Hotkey?.Gesture is { } gesture
            ? string.Format(CultureInfo.CurrentCulture, Strings.Launcher_ButtonTip, GlobalHotkey.Format(gesture))
            : Strings.Launcher_Title);

    private void StopLauncher()
    {
        Hotkey?.Dispose();
        Hotkey = null;
        launcher?.CloseForGood();
        launcher = null;
    }
}
