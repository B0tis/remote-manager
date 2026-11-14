using Avalonia.Controls;
using RemoteManager.App.Views;

namespace RemoteManager.App.Services;

/// <summary>
/// A window can only own a dialog while it is shown. With "close to tray" the overview may be hidden when a
/// connection started from the notification area or the launcher has to ask something there (the master
/// password of a KeePass database, an SSH host key): it comes back first, and the dialog opens over it.
/// </summary>
public static class DialogOwner
{
    /// <summary><paramref name="owner"/>, shown again when it is the hidden overview.</summary>
    public static Window Shown(Window owner)
    {
        if (!owner.IsVisible && owner is MainWindow overview)
            overview.ShowFromTray();
        return owner;
    }
}
