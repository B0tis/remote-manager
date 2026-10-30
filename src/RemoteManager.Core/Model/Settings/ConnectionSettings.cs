using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteManager.Core.Model.Settings;

/// <summary>
/// Base of the per-type options. Each type has one record (see <see cref="TypeFor"/>), stored as
/// JSON in connections.settings_json, so adding an option needs no schema migration.
/// Never put a secret in a settings record: reference one with <see cref="CredentialRef"/>.
/// </summary>
public abstract record ConnectionSettings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The settings record of a connection type.</summary>
    public static Type TypeFor(ConnectionType type) => type switch
    {
        ConnectionType.Rdp => typeof(RdpSettings),
        ConnectionType.TeamViewer or ConnectionType.AnyDesk or ConnectionType.RustDesk => typeof(RemoteToolSettings),
        ConnectionType.Ssh or ConnectionType.Sftp => typeof(SshSettings),
        ConnectionType.Vnc => typeof(VncSettings),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static ConnectionSettings CreateDefault(ConnectionType type) =>
        (ConnectionSettings)Activator.CreateInstance(TypeFor(type))!;

    public static bool Fits(ConnectionType type, ConnectionSettings settings) => settings.GetType() == TypeFor(type);

    public static string Serialize(ConnectionSettings settings) => JsonSerializer.Serialize(settings, settings.GetType(), Json);

    /// <summary>Reads stored settings; empty or unreadable JSON gives the defaults (unknown fields are ignored).</summary>
    public static ConnectionSettings Deserialize(ConnectionType type, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return CreateDefault(type);
        try
        {
            return (ConnectionSettings?)JsonSerializer.Deserialize(json, TypeFor(type), Json) ?? CreateDefault(type);
        }
        catch (JsonException)
        {
            return CreateDefault(type);
        }
    }
}
