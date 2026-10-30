using System.Text;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.RemoteTools;

namespace RemoteManager.Core.ImportExport;

/// <summary>
/// Reads the client list exported from my.anydesk I (CSV). Columns are found by their header (ID,
/// Alias, name, comment; German and English), separator ';', ',' or tab. One-time import: no sync, no
/// passwords (the export has none). Connections come without a group; the caller picks one.
/// </summary>
public sealed class AnyDeskCsvImporter : IImporter
{
    private static readonly string[] IdHeaders = ["id", "anydesk id", "anydesk-id", "anydeskid", "cid", "client id", "client-id", "address", "adresse"];
    private static readonly string[] AliasHeaders = ["alias", "anydesk alias", "anydesk-alias"];
    private static readonly string[] NameHeaders = ["name", "computer", "computer name", "computername", "hostname", "rechnername", "gerät", "device"];
    private static readonly string[] CommentHeaders = ["comment", "kommentar", "description", "beschreibung", "notes", "notiz", "notizen"];

    public string Id => "anydesk-csv";
    public string FormatName => "AnyDesk (my.anydesk CSV)";
    public IReadOnlyList<string> FileExtensions { get; } = [".csv"];

    public bool CanImport(string path)
    {
        if (!path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            return false;
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? header = reader.ReadLine();
            if (header is null)
                return false;
            string[] columns = [.. SplitLine(header, DetectSeparator(header)).Select(Normalize)];
            bool hasId = columns.Any(IdHeaders.Contains);
            return hasId && (columns.Any(AliasHeaders.Contains) || columns.Any(c => c.Contains("anydesk", StringComparison.Ordinal))
                || Path.GetFileName(path).Contains("anydesk", StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return false;
        }
    }

    public async Task<ImportResult> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        string text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return Parse(text);
    }

    /// <summary>Parses CSV text (exposed for tests).</summary>
    public static ImportResult Parse(string text)
    {
        var result = new ImportResult();
        List<string> lines = [.. text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0)];
        if (lines.Count == 0)
            return result;

        char separator = DetectSeparator(lines[0]);
        string[] header = [.. SplitLine(lines[0], separator).Select(Normalize)];
        int id = IndexOf(header, IdHeaders), alias = IndexOf(header, AliasHeaders), name = IndexOf(header, NameHeaders), comment = IndexOf(header, CommentHeaders);
        int first = 1;
        if (id < 0 && alias < 0)
        {
            // No known header: first column ID, second alias, and the first line is data.
            (id, alias, first) = (0, 1, 0);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = first; i < lines.Count; i++)
        {
            string[] cells = SplitLine(lines[i], separator);
            string Cell(int index) => index >= 0 && index < cells.Length ? cells[index].Trim() : "";

            string remoteId = RemoteToolCommandLine.NormalizeId(Cell(id));
            string aliasText = Cell(alias);
            string target = remoteId.Length > 0 ? remoteId : aliasText;
            if (target.Length == 0)
            {
                result.Warnings.Add($"Line {i + 1}: no ID and no alias, skipped.");
                continue;
            }

            if (!RemoteToolCommandLine.IsValidId(ConnectionType.AnyDesk, target))
            {
                result.Warnings.Add($"Line {i + 1}: \"{target}\" is not an AnyDesk ID or alias, skipped.");
                continue;
            }

            if (!seen.Add(target))
            {
                result.Warnings.Add($"Line {i + 1}: {target} appears twice, skipped.");
                continue;
            }

            string display = aliasText.Length > 0 ? aliasText : Cell(name).Length > 0 ? Cell(name) : target;
            var connection = new Connection
            {
                Name = display,
                Type = ConnectionType.AnyDesk,
                Host = target,
                Description = Cell(comment),
            };
            connection.Settings = new RemoteToolSettings
            {
                Alias = aliasText, Source = RemoteToolSource.AnyDeskCsv, ExternalId = remoteId,
            };
            result.Connections.Add(connection);
        }

        return result;
    }

    /// <summary>
    /// Drops imported connections whose ID already exists as an AnyDesk connection (re-import of the same
    /// list). Returns how many were dropped.
    /// </summary>
    public static int RemoveKnown(IEnumerable<Connection> existing, ImportResult result)
    {
        var known = new HashSet<string>(existing.Where(c => c.Type == ConnectionType.AnyDesk).Select(c => RemoteToolCommandLine.NormalizeId(c.Host)),
            StringComparer.OrdinalIgnoreCase);
        return result.Connections.RemoveAll(c => known.Contains(RemoteToolCommandLine.NormalizeId(c.Host)));
    }

    private static int IndexOf(string[] header, string[] names) => Array.FindIndex(header, names.Contains);

    private static string Normalize(string header) => header.Trim().Trim('﻿').Trim().ToLowerInvariant();

    private static char DetectSeparator(string header)
    {
        char[] candidates = [';', ',', '\t'];
        return candidates.OrderByDescending(c => header.Count(h => h == c)).First();
    }

    /// <summary>One CSV line: double quotes group, "" inside quotes is a quote.</summary>
    private static string[] SplitLine(string line, char separator)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == separator)
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        cells.Add(current.ToString());
        return [.. cells];
    }
}
