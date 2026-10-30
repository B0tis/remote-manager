using System.Diagnostics;
using System.Reflection;
using RemoteManager.App.Connections;
using RemoteManager.Core;
using RemoteManager.Core.Credentials;
using RemoteManager.Core.Extensibility;
using RemoteManager.Core.ImportExport;
using RemoteManager.Core.Library;
using RemoteManager.Core.Model;
using RemoteManager.Core.Status;
using RemoteManager.Core.Storage;

namespace RemoteManager.App;

/// <summary>
/// The app's services, created once at startup: library (SQLite), credentials, connection handlers,
/// importers/exporters, status checks. Windows reach it through <see cref="Current"/> (Avalonia windows
/// need parameterless constructors, so there is no constructor injection).
/// </summary>
public sealed class AppHost : IDisposable
{
    private static AppHost? current;

    private AppHost(ConnectionLibrary library, DataStoreReport report, IReadOnlyList<ICredentialSource> sources, Assembly[] extensionAssemblies)
    {
        Library = library;
        StoreReport = report;
        Credentials = new CredentialService(sources, library);
        Handlers = ConnectionHandlerRegistry.Discover(typeof(AppHost).Assembly);
        Importers = PluginCatalog.Create<IImporter>(extensionAssemblies);
        Exporters = PluginCatalog.Create<IExporter>(extensionAssemblies);
        Status = new StatusChecker(ProbeFor);
    }

    public static AppHost Current => current ?? throw new InvalidOperationException("AppHost.Start has not run.");

    public ConnectionLibrary Library { get; }
    public DataStoreReport StoreReport { get; }
    public CredentialService Credentials { get; }
    public ConnectionHandlerRegistry Handlers { get; }
    public IReadOnlyList<IImporter> Importers { get; }
    public IReadOnlyList<IExporter> Exporters { get; }
    public StatusChecker Status { get; }

    /// <summary>Opens the database in <see cref="AppPaths.Root"/> (migrating connections.json once) and wires the services.</summary>
    public static AppHost Start()
    {
        Assembly[] assemblies = [PluginCatalog.CoreAssembly, typeof(AppHost).Assembly];
        IReadOnlyList<ICredentialSource> sources = [.. PluginCatalog.Create<ICredentialSource>(assemblies).Where(s => s.IsAvailable)];
        ICredentialSource windows = sources.First(s => s.Id == WindowsCredentialSource.SourceId);

        // Runs before the first window. Everything inside is synchronous I/O (SQLite, Credential Manager)
        // that does not need the UI thread, so blocking here cannot deadlock.
        (ConnectionLibrary library, DataStoreReport report) = DataStore.OpenAsync(AppPaths.Root, windows).GetAwaiter().GetResult();
        Trace.WriteLine($"Store {report.DatabasePath}: migrations [{string.Join(',', report.AppliedMigrations)}], " +
            $"from json {report.MigratedFromJson}, backup {report.JsonBackupPath ?? "-"}, seeded {report.Seeded}");

        current = new AppHost(library, report, sources, assemblies);
        return current;
    }

    public void Dispose() => Status.Dispose();

    private IStatusProbe? ProbeFor(Connection connection) =>
        Handlers.Supports(connection.Type) ? Handlers[connection.Type].StatusProbe : null;
}
