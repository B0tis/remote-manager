using Avalonia.Controls;
using RemoteManager.Core.Model.Settings;

namespace RemoteManager.App.Connections.Vnc;

/// <summary>The VNC part of the connection editor. The jump host is kept as loaded (SSH tunnels come separately).</summary>
public partial class VncSettingsEditor : UserControl, ISettingsEditor
{
    private VncSettings loaded = new();

    public VncSettingsEditor() => InitializeComponent();

    public Control View => this;

    public CheckBox ViewOnly => ViewOnlyBox;
    public CheckBox External => ExternalBox;

    public void Load(ConnectionSettings settings)
    {
        loaded = (VncSettings)settings;
        QualityBox.SelectedIndex = (int)loaded.Quality;
        ScaleBox.IsChecked = loaded.ScaleToWindow;
        ViewOnlyBox.IsChecked = loaded.ViewOnly;
        ExternalBox.IsChecked = loaded.OpenExternally;
    }

    public string? Validate() => null;

    public ConnectionSettings Commit() => loaded with
    {
        Quality = (VncQuality)Math.Max(0, QualityBox.SelectedIndex),
        ScaleToWindow = ScaleBox.IsChecked == true,
        ViewOnly = ViewOnlyBox.IsChecked == true,
        OpenExternally = ExternalBox.IsChecked == true,
    };
}
