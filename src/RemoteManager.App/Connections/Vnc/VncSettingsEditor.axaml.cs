using Avalonia.Controls;
using RemoteManager.App.Connections.Ssh;
using RemoteManager.Core.Model.Settings;

namespace RemoteManager.App.Connections.Vnc;

/// <summary>The VNC part of the connection editor, including the SSH connection to tunnel through.</summary>
public partial class VncSettingsEditor : UserControl, ISettingsEditor
{
    private VncSettings loaded = new();

    public VncSettingsEditor() => InitializeComponent();

    public Control View => this;

    public CheckBox ViewOnly => ViewOnlyBox;
    public CheckBox External => ExternalBox;
    public ComboBox JumpHost => JumpBox;

    public void Load(ConnectionSettings settings)
    {
        loaded = (VncSettings)settings;
        QualityBox.SelectedIndex = (int)loaded.Quality;
        ScaleBox.IsChecked = loaded.ScaleToWindow;
        ViewOnlyBox.IsChecked = loaded.ViewOnly;
        ExternalBox.IsChecked = loaded.OpenExternally;
        JumpHostPicker.Fill(JumpBox, loaded.JumpHostId);
    }

    public string? Validate() => null;

    public ConnectionSettings Commit() => loaded with
    {
        Quality = (VncQuality)Math.Max(0, QualityBox.SelectedIndex),
        ScaleToWindow = ScaleBox.IsChecked == true,
        ViewOnly = ViewOnlyBox.IsChecked == true,
        OpenExternally = ExternalBox.IsChecked == true,
        JumpHostId = JumpHostPicker.SelectedId(JumpBox),
    };
}
