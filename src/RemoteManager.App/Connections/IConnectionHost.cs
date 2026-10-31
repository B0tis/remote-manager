using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Views;

namespace RemoteManager.App.Connections;

/// <summary>What a handler may use from the overview while it opens a connection (implemented by <see cref="MainWindow"/>).</summary>
public interface IConnectionHost
{
    /// <summary>The overview window (owner for dialogs).</summary>
    Window Owner { get; }

    AppHost App { get; }

    /// <summary>Copies the connection's stored password and clears the clipboard later.</summary>
    Task CopyPasswordAsync(ConnectionItem connection, TimeSpan? clearAfter = null);

    void ShowToast(string title, string message, FAInfoBarSeverity severity);

    /// <summary>Records a successful logon (last connected) and saves the connection.</summary>
    void MarkConnected(ConnectionItem connection);

    /// <summary>Saves a changed connection (credential reference, preview path, ...).</summary>
    void Save(ConnectionItem connection);
}

/// <summary>A window that shows one connection's session; the host keeps one per connection.</summary>
public interface ISessionWindow
{
    ConnectionItem Connection { get; }
}
