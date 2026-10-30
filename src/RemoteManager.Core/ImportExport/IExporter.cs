using RemoteManager.Core.Model;

namespace RemoteManager.Core.ImportExport;

/// <summary>
/// Writes connections to a file (JSON backup, .rdp, ...). Found by <see cref="Extensibility.PluginCatalog"/>.
/// Exports never contain secrets.
/// </summary>
public interface IExporter
{
    string Id { get; }
    string FormatName { get; }

    /// <summary>Extension of the written file, with dot.</summary>
    string FileExtension { get; }

    Task ExportAsync(ExportData data, Stream output, CancellationToken cancellationToken = default);
}

/// <summary>The part of the library to export.</summary>
public sealed record ExportData(IReadOnlyList<ConnectionGroup> Groups, IReadOnlyList<Connection> Connections);
