using System.Reflection;
using RemoteManager.Core.Extensibility;
using RemoteManager.Core.Model;

namespace RemoteManager.App.Connections;

/// <summary>
/// The one place that maps a <see cref="ConnectionType"/> to its <see cref="IConnectionHandler"/>.
/// Handlers are discovered in the app assembly; the main window, editor, sessions, and status checks
/// only ever go through here.
/// </summary>
public sealed class ConnectionHandlerRegistry
{
    private readonly Dictionary<ConnectionType, IConnectionHandler> handlers = [];

    public ConnectionHandlerRegistry(IEnumerable<IConnectionHandler> handlers)
    {
        foreach (IConnectionHandler handler in handlers)
        {
            if (!this.handlers.TryAdd(handler.Type, handler))
                throw new InvalidOperationException($"Two handlers for {handler.Type}: {this.handlers[handler.Type].GetType().Name} and {handler.GetType().Name}.");
        }
    }

    /// <summary>All handlers found in <paramref name="assemblies"/> (default: the app assembly).</summary>
    public static ConnectionHandlerRegistry Discover(params Assembly[] assemblies) =>
        new(PluginCatalog.Create<IConnectionHandler>(assemblies.Length > 0 ? assemblies : [typeof(ConnectionHandlerRegistry).Assembly]));

    /// <summary>Handlers in <see cref="ConnectionType"/> order (the editor's type list).</summary>
    public IEnumerable<IConnectionHandler> All => handlers.Values.OrderBy(h => h.Type);

    public IConnectionHandler this[ConnectionType type] =>
        handlers.TryGetValue(type, out IConnectionHandler? handler)
            ? handler
            : throw new KeyNotFoundException($"No connection handler for {type}. Add a class deriving from ConnectionHandler.");

    public bool Supports(ConnectionType type) => handlers.ContainsKey(type);
}
