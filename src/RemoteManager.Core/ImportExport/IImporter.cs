using RemoteManager.Core.Model;

namespace RemoteManager.Core.ImportExport;

/// <summary>
/// Reads connections from another tool's file (1Remote, RDCMan .rdg, mRemoteNG, .rdp, ...). Found by
/// <see cref="Extensibility.PluginCatalog"/>, so a new format is a new file in this folder.
/// An importer never writes secrets anywhere; it returns them in <see cref="ImportResult.Secrets"/>
/// and the caller stores them in a credential source.
/// </summary>
public interface IImporter
{
    /// <summary>Stable id, for example "1remote".</summary>
    string Id { get; }

    /// <summary>Name of the source format as shown in the file picker (a product name, not translated).</summary>
    string FormatName { get; }

    /// <summary>File extensions this importer reads, with dot (".rdg").</summary>
    IReadOnlyList<string> FileExtensions { get; }

    /// <summary>Whether this importer understands the file (cheap check, may peek at the content).</summary>
    bool CanImport(string path);

    Task<ImportResult> ImportAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>What an importer found. Ids are fresh unless the format carries stable ones.</summary>
public sealed class ImportResult
{
    public List<ConnectionGroup> Groups { get; } = [];
    public List<Connection> Connections { get; } = [];

    /// <summary>Passwords found in the source, by connection id. Store them, then drop the result.</summary>
    public Dictionary<string, string> Secrets { get; } = [];

    /// <summary>Entries skipped or partly read, for the import summary (not translated: technical detail).</summary>
    public List<string> Warnings { get; } = [];
}
