using Avalonia.Controls;
using RemoteManager.App.Views;

namespace RemoteManager.App.Connections;

/// <summary>Head of the connect card of a session window: picture, name, type, and host of the connection.</summary>
public partial class ConnectionHeader : UserControl
{
    public ConnectionHeader() => InitializeComponent();

    public string NameShown => NameLabel.Text ?? "";
    public string HostShown => HostLabel.Text ?? "";

    public void Load(ConnectionItem connection)
    {
        GlyphFrame.Background = connection.TypeSoftBrush;
        Glyph.Seed = connection.Id;
        Glyph.Brush = connection.TypeBrush;
        NameLabel.Text = connection.Name;
        HostLabel.Text = connection.HostLabel;
        TypeChip.Background = connection.TypeBrush;
        TypeChipText.Text = connection.TypeLabel;
    }
}
