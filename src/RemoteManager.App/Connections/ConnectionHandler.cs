using Avalonia.Controls;
using Avalonia.Media;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;
using RemoteManager.Core.Model;
using RemoteManager.Core.Status;

namespace RemoteManager.App.Connections;

/// <summary>Defaults for <see cref="IConnectionHandler"/>: a host/IP target with a TCP port check and no settings editor.</summary>
public abstract class ConnectionHandler : IConnectionHandler
{
    public abstract ConnectionType Type { get; }
    public abstract string DisplayName { get; }
    public abstract string Initials { get; }
    public abstract Color AccentColor { get; }

    public virtual string HostPlaceholder => Strings.Editor_HostPlaceholder;
    public virtual bool HasPort => DefaultPorts.For(Type) > 0;
    public virtual bool UsesUserName => true;
    public virtual string PasswordHint => Strings.Editor_PasswordHint_CopyOnly;
    public virtual string StatusHint => Strings.Status_Hint_TcpPort;
    public virtual IStatusProbe? StatusProbe => TcpPortProbe.Instance;

    public virtual ISettingsEditor? CreateSettingsEditor() => null;

    public abstract Window? Open(ConnectionItem connection, IConnectionHost host);
}
