using RemoteManager.Core.Model.Settings;

namespace RemoteManager.Core.Model;

/// <summary>
/// One saved connection. Plain data: the UI wraps it, <see cref="Library.ConnectionLibrary"/> persists it.
/// Secrets are never part of it, only a <see cref="CredentialRef"/> to one.
/// </summary>
public sealed class Connection
{
    private ConnectionType type;
    private ConnectionSettings? settings;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>Changing the type replaces <see cref="Settings"/> when they belong to another type.</summary>
    public ConnectionType Type
    {
        get => type;
        set
        {
            type = value;
            if (settings is not null && !ConnectionSettings.Fits(value, settings))
                settings = null;
        }
    }

    /// <summary>Host name or IP; for TeamViewer, AnyDesk, and RustDesk the remote ID.</summary>
    public string Host { get; set; } = "";

    /// <summary>0 means the default port of the type (<see cref="DefaultPorts"/>).</summary>
    public int Port { get; set; }

    public string Description { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public bool IsFavorite { get; set; }
    public string? GroupId { get; set; }
    public int SortOrder { get; set; }
    public DateTime? LastConnectedAt { get; set; }

    /// <summary>Preview image; a bare file name is relative to <see cref="AppPaths.Thumbnails"/>.</summary>
    public string? PreviewPath { get; set; }

    /// <summary>Logon user. Empty together with an empty <see cref="Credential"/> means: inherit from the group.</summary>
    public string UserName { get; set; } = "";
    public string Domain { get; set; } = "";
    public CredentialRef? Credential { get; set; }

    /// <summary>Type-specific options, always of the record type that fits <see cref="Type"/>.</summary>
    public ConnectionSettings Settings
    {
        get => settings ??= ConnectionSettings.CreateDefault(type);
        set
        {
            if (!ConnectionSettings.Fits(type, value))
                throw new ArgumentException($"{value.GetType().Name} does not belong to {type}.", nameof(value));
            settings = value;
        }
    }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The settings as their concrete type, for example <c>connection.SettingsAs&lt;RdpSettings&gt;()</c>.</summary>
    public T SettingsAs<T>() where T : ConnectionSettings =>
        Settings as T ?? throw new InvalidOperationException($"{Type} connections carry {Settings.GetType().Name}, not {typeof(T).Name}.");

    /// <summary>The port to connect to: <see cref="Port"/>, or the type's default.</summary>
    public int EffectivePort => Port > 0 ? Port : DefaultPorts.For(Type);

    public string ResolvedPreviewPath => PreviewPath is { Length: > 0 } path
        ? Path.IsPathRooted(path) ? path : Path.Combine(AppPaths.Thumbnails, path)
        : Path.Combine(AppPaths.Thumbnails, $"{Id}.png");

    /// <summary>A copy with a new id and no secret reference (a copied connection never shares a password).</summary>
    public Connection Duplicate(string name) => new()
    {
        Name = name, Type = Type, Host = Host, Port = Port, Description = Description, Notes = Notes,
        Tags = [.. Tags], GroupId = GroupId, SortOrder = SortOrder, UserName = UserName, Domain = Domain,
        Settings = Settings,
    };
}

public static class DefaultPorts
{
    /// <summary>Default TCP port per type; 0 for the remote tools, which connect by ID.</summary>
    public static int For(ConnectionType type) => type switch
    {
        ConnectionType.Rdp => 3389,
        ConnectionType.Ssh or ConnectionType.Sftp => 22,
        ConnectionType.Vnc => 5900,
        _ => 0,
    };
}
