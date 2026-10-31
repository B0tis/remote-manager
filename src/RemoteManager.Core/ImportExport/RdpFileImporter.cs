using System.Globalization;
using RemoteManager.Core.Model;
using RemoteManager.Core.Rdp;
using RemoteManager.Core.Resources;

namespace RemoteManager.Core.ImportExport;

/// <summary>
/// One connection from an .rdp file (mstsc, RD Web, other managers): address, port, user, display,
/// gateway, redirections, RemoteApp. A "password 51:b:" blob is ignored: it only decrypts for the user
/// and machine that wrote it.
/// </summary>
public sealed class RdpFileImporter : IImporter
{
    public string Id => "rdp-file";
    public string FormatName => "Remotedesktop (.rdp)";
    public IReadOnlyList<string> FileExtensions { get; } = [".rdp"];

    public bool CanImport(string path) =>
        File.Exists(path) && Path.GetExtension(path).Equals(".rdp", StringComparison.OrdinalIgnoreCase);

    public Task<ImportResult> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        RdpFile file = RdpFile.Load(path);
        var result = new ImportResult();
        Connection connection = RdpFileMapping.ToConnection(file, Path.GetFileNameWithoutExtension(path));
        if (connection.Host.Length == 0)
        {
            result.Warnings.Add(string.Format(CultureInfo.CurrentCulture, ImportStrings.RdpFile_NoAddress, Path.GetFileName(path)));
            return Task.FromResult(result);
        }

        if (file.Contains("password 51"))
            result.Warnings.Add(string.Format(CultureInfo.CurrentCulture, ImportStrings.RdpFile_PasswordIgnored, connection.Name));
        result.Connections.Add(connection);
        return Task.FromResult(result);
    }
}
