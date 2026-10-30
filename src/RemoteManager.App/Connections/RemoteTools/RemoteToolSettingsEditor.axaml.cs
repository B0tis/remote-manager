using Avalonia.Controls;
using RemoteManager.Core.Model.Settings;

namespace RemoteManager.App.Connections.RemoteTools;

/// <summary>Alias, mode, and extra command-line arguments of a TeamViewer, AnyDesk, or RustDesk connection.</summary>
public partial class RemoteToolSettingsEditor : UserControl, ISettingsEditor
{
    private RemoteToolSettings loaded = new();

    public RemoteToolSettingsEditor() => InitializeComponent();

    public Control View => this;

    public void Load(ConnectionSettings settings)
    {
        loaded = (RemoteToolSettings)settings;
        AliasBox.Text = loaded.Alias;
        ModeBox.SelectedIndex = (int)loaded.Mode;
        ArgumentsBox.Text = loaded.ExtraArguments;
    }

    public string? Validate() => null;

    public ConnectionSettings Commit() => loaded with
    {
        Alias = AliasBox.Text?.Trim() ?? "",
        Mode = (RemoteToolMode)Math.Max(0, ModeBox.SelectedIndex),
        ExtraArguments = ArgumentsBox.Text?.Trim() ?? "",
    };
}
