using Avalonia.Controls;
using RemoteManager.App.Connections.Ssh;
using RemoteManager.Core.Model.Settings;

namespace RemoteManager.App.Connections.Rdp;

/// <summary>The RDP part of the connection editor. Options without a field here are kept as loaded.</summary>
public partial class RdpSettingsEditor : UserControl, ISettingsEditor
{
    // Display choices in list order: (mode, width, height).
    private static readonly (RdpDisplayMode Mode, int Width, int Height)[] Displays =
    [
        (RdpDisplayMode.FitToWindow, 0, 0),
        (RdpDisplayMode.SmartSizing, 0, 0),
        (RdpDisplayMode.FixedResolution, 1920, 1080),
        (RdpDisplayMode.FixedResolution, 1280, 720),
    ];

    private RdpSettings loaded = new();

    public RdpSettingsEditor() => InitializeComponent();

    public Control View => this;

    public void Load(ConnectionSettings settings)
    {
        loaded = (RdpSettings)settings;
        int index = Array.FindIndex(Displays, d => d.Mode == loaded.Display
            && (d.Mode != RdpDisplayMode.FixedResolution || (d.Width, d.Height) == (loaded.DesktopWidth, loaded.DesktopHeight)));
        DisplayBox.SelectedIndex = Math.Max(0, index);
        WinKeysBox.IsChecked = loaded.Keyboard == RdpKeyboardMode.Session;
        JumpHostPicker.Fill(JumpBox, loaded.JumpHostId);
    }

    public string? Validate() => null;

    public ConnectionSettings Commit()
    {
        (RdpDisplayMode mode, int width, int height) = Displays[Math.Max(0, DisplayBox.SelectedIndex)];
        return loaded with
        {
            Display = mode,
            DesktopWidth = width,
            DesktopHeight = height,
            Keyboard = WinKeysBox.IsChecked == true ? RdpKeyboardMode.Session
                : loaded.Keyboard == RdpKeyboardMode.Session ? RdpKeyboardMode.Local : loaded.Keyboard,
            JumpHostId = JumpHostPicker.SelectedId(JumpBox),
        };
    }
}
