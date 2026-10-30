using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;

namespace RemoteManager.Core.ImportExport;

/// <summary>
/// Reads connections.json of the pre-SQLite builds (Phase 0 and the first UI). Used once by
/// <see cref="Storage.DataStore"/> to migrate, and available as a normal importer. Ids are kept, so
/// passwords already in the Credential Manager (target "RemoteManager:&lt;id&gt;") stay attached.
/// </summary>
public sealed class LegacyJsonImporter : IImporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public string Id => "remote-manager-json-v0";
    public string FormatName => "Remote-Manager connections.json";
    public IReadOnlyList<string> FileExtensions { get; } = [".json"];

    public bool CanImport(string path) =>
        File.Exists(path) && Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
        && File.ReadAllText(path).TrimStart().StartsWith('[');

    public async Task<ImportResult> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        await using FileStream stream = File.OpenRead(path);
        List<LegacyConnection> legacy = await JsonSerializer.DeserializeAsync<List<LegacyConnection>>(stream, Options, cancellationToken).ConfigureAwait(false) ?? [];

        var result = new ImportResult();
        var groupsByName = new Dictionary<string, ConnectionGroup>(StringComparer.CurrentCultureIgnoreCase);
        foreach (LegacyConnection old in legacy)
        {
            if (string.IsNullOrWhiteSpace(old.Id) || string.IsNullOrWhiteSpace(old.Name))
            {
                result.Warnings.Add($"Skipped an entry without id or name ({old.Host}).");
                continue;
            }

            string? groupId = null;
            if (old.Group?.Trim() is { Length: > 0 } groupName)
            {
                if (!groupsByName.TryGetValue(groupName, out ConnectionGroup? group))
                {
                    group = new ConnectionGroup { Name = groupName, SortOrder = groupsByName.Count };
                    groupsByName[groupName] = group;
                    result.Groups.Add(group);
                }

                groupId = group.Id;
            }

            var connection = new Connection
            {
                Id = old.Id,
                Name = old.Name.Trim(),
                Type = old.Type,
                Host = old.Host ?? "",
                // Old files store 3389 for RDP and 0 for the remote tools; 0 now means "type default".
                Port = old.Port == DefaultPorts.For(old.Type) ? 0 : Math.Max(0, old.Port),
                Description = old.Description ?? "",
                GroupId = groupId,
                Tags = old.Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [],
                UserName = old.UserName ?? "",
                Domain = old.Domain ?? "",
                IsFavorite = old.IsFavorite,
                LastConnectedAt = old.LastConnectedAt?.ToUniversalTime(),
            };

            if (old.Type == ConnectionType.Rdp)
            {
                connection.Settings = new RdpSettings
                {
                    // 0 fit to window, 1 smart sizing, 2 1920×1080, 3 1280×720 (the latter two scaled).
                    Display = old.DisplayMode switch
                    {
                        1 => RdpDisplayMode.SmartSizing,
                        2 or 3 => RdpDisplayMode.FixedResolution,
                        _ => RdpDisplayMode.FitToWindow,
                    },
                    DesktopWidth = old.DisplayMode switch { 2 => 1920, 3 => 1280, _ => 0 },
                    DesktopHeight = old.DisplayMode switch { 2 => 1080, 3 => 720, _ => 0 },
                    Keyboard = old.WinKeysToSession ? RdpKeyboardMode.Session : RdpKeyboardMode.Local,
                };
            }

            result.Connections.Add(connection);
        }

        return result;
    }

    /// <summary>The shape of one entry in the old file.</summary>
    private sealed class LegacyConnection
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public ConnectionType Type { get; set; }
        public string? Host { get; set; }
        public int Port { get; set; }
        public string? Description { get; set; }
        public string? Group { get; set; }
        public List<string>? Tags { get; set; }
        public string? UserName { get; set; }
        public string? Domain { get; set; }
        public int DisplayMode { get; set; }
        public bool WinKeysToSession { get; set; } = true;
        public DateTime? LastConnectedAt { get; set; }
        public bool IsFavorite { get; set; }
    }
}
