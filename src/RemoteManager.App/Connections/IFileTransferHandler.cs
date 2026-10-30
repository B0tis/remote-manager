using RemoteManager.App.Views;

namespace RemoteManager.App.Connections;

/// <summary>
/// A handler that can start a file transfer session directly (TeamViewer, AnyDesk, RustDesk). The
/// overview offers "Dateiübertragung" in the context menu for connections whose handler implements it.
/// </summary>
public interface IFileTransferHandler
{
    void OpenFileTransfer(ConnectionItem connection, IConnectionHost host);
}
