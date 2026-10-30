using System.ComponentModel;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace RemoteManager.App;

public enum ConnectionType { Rdp, TeamViewer, AnyDesk, RustDesk }

public enum OnlineStatus { Unknown, Online, Offline }

/// <summary>
/// One saved connection. Secrets are never part of it: the password is typed in the session window
/// and handed straight to the RDP control (a credential store follows in Phase 1).
/// </summary>
public sealed class Connection : INotifyPropertyChanged
{
    private bool isFavorite;
    private OnlineStatus status;
    private Bitmap? preview;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public ConnectionType Type { get; set; }
    /// <summary>Host name or IP for RDP, the remote ID for TeamViewer, AnyDesk, and RustDesk.</summary>
    public string Host { get; set; } = "";
    public int Port { get; set; } = 3389;
    public string Description { get; set; } = "";
    public string Group { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public string UserName { get; set; } = "";
    public string Domain { get; set; } = "";
    /// <summary>0 fit to window, 1 smart sizing, 2 1920×1080, 3 1280×720.</summary>
    public int DisplayMode { get; set; }
    public bool WinKeysToSession { get; set; } = true;
    public DateTime? LastConnectedAt { get; set; }

    public bool IsFavorite
    {
        get => isFavorite;
        set { isFavorite = value; Changed(nameof(IsFavorite)); }
    }

    [JsonIgnore]
    public OnlineStatus Status
    {
        get => status;
        set { status = value; Changed(string.Empty); }
    }

    [JsonIgnore]
    public Bitmap? Preview
    {
        get => preview;
        private set { preview = value; Changed(string.Empty); }
    }

    [JsonIgnore] public bool HasPreview => Preview is not null;
    [JsonIgnore] public string ThumbnailPath => Path.Combine(AppPaths.Thumbnails, $"{Id}.png");

    [JsonIgnore]
    public string TypeLabel => Type switch
    {
        ConnectionType.Rdp => "RDP",
        ConnectionType.TeamViewer => "TeamViewer",
        ConnectionType.AnyDesk => "AnyDesk",
        _ => "RustDesk",
    };

    [JsonIgnore]
    public string TypeInitials => Type switch
    {
        ConnectionType.Rdp => "RDP",
        ConnectionType.TeamViewer => "TV",
        ConnectionType.AnyDesk => "AD",
        _ => "RD",
    };

    [JsonIgnore]
    public IBrush TypeBrush => new SolidColorBrush(Type switch
    {
        ConnectionType.Rdp => Color.FromRgb(0x3B, 0x82, 0xF6),
        ConnectionType.TeamViewer => Color.FromRgb(0x0E, 0xA5, 0xE9),
        ConnectionType.AnyDesk => Color.FromRgb(0xEF, 0x44, 0x44),
        _ => Color.FromRgb(0x8B, 0x5C, 0xF6),
    });

    [JsonIgnore]
    public string HostLabel => Type == ConnectionType.Rdp && Port != 3389 ? $"{Host}:{Port}" : Host;

    [JsonIgnore] public string GroupLabel => Group.Length == 0 ? "No group" : Group;

    [JsonIgnore]
    public string StatusLabel => Status switch
    {
        OnlineStatus.Online => "Online",
        OnlineStatus.Offline => "Offline",
        _ => "Unknown",
    };

    [JsonIgnore]
    public IBrush StatusBrush => new SolidColorBrush(Status switch
    {
        OnlineStatus.Online => Color.FromRgb(0x22, 0xC5, 0x5E),
        OnlineStatus.Offline => Color.FromRgb(0xEF, 0x44, 0x44),
        _ => Color.FromRgb(0x9C, 0xA3, 0xAF),
    });

    /// <summary>Up to three tags, then "+n".</summary>
    [JsonIgnore]
    public IReadOnlyList<string> VisibleTags =>
        Tags.Count <= 3 ? Tags : [.. Tags.Take(3), $"+{Tags.Count - 3}"];

    [JsonIgnore]
    public string LastConnectedLabel => LastConnectedAt is { } at ? at.ToLocalTime().ToString("g") : "Never";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises a change for every property, used after the edit dialog.</summary>
    public void Changed(string property = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    public void ReloadPreview()
    {
        if (!File.Exists(ThumbnailPath))
        {
            Preview = null;
            return;
        }

        // Decode from a stream we close right away, so the next snapshot can overwrite the file.
        using FileStream stream = File.OpenRead(ThumbnailPath);
        Preview = new Bitmap(stream);
    }

    /// <summary>
    /// TCP check of the RDP port. A refused connection means the host answers but RDP is not there
    /// (offline); timeouts and DNS errors stay unknown, because firewalls often drop silently.
    /// Remote-tool devices stay unknown until their APIs are wired up (Phase 2).
    /// </summary>
    public async Task CheckStatusAsync()
    {
        if (Type != ConnectionType.Rdp || Host.Length == 0)
        {
            Status = OnlineStatus.Unknown;
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(Host, Port, timeout.Token);
            Status = OnlineStatus.Online;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            Status = OnlineStatus.Offline;
        }
        catch (Exception)
        {
            Status = OnlineStatus.Unknown;
        }
    }
}

/// <summary>Stores connections as JSON in %LOCALAPPDATA%\RemoteManager\connections.json.</summary>
internal static class ConnectionStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string FilePath => Path.Combine(AppPaths.Root, "connections.json");

    public static List<Connection> Load()
    {
        if (!File.Exists(FilePath))
        {
            List<Connection> seed = CreateSamples();
            Save(seed);
            return seed;
        }

        return JsonSerializer.Deserialize<List<Connection>>(File.ReadAllText(FilePath), Options) ?? [];
    }

    public static void Save(IEnumerable<Connection> connections)
    {
        Directory.CreateDirectory(AppPaths.Root);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(connections, Options));
        File.Move(temp, FilePath, overwrite: true);
    }

    private static List<Connection> CreateSamples() =>
    [
        new() { Name = "dc01", Host = "10.0.0.10", Description = "Domain controller", Group = "Kunde A", Tags = ["prod", "ad"], UserName = "admin", Domain = "CORP", IsFavorite = true },
        new() { Name = "fileserver", Host = "10.0.0.20", Description = "Shares and backup target", Group = "Kunde A", Tags = ["prod"], UserName = "admin", Domain = "CORP" },
        new() { Name = "Buchhaltung-PC", Type = ConnectionType.TeamViewer, Host = "123 456 789", Port = 0, Description = "Only after calling ahead", Group = "Kunde A", Tags = ["kunde-a"] },
        new() { Name = "Kasse 2", Type = ConnectionType.AnyDesk, Host = "987 654 321", Port = 0, Description = "Shop, unattended access", Group = "Laden", Tags = ["kasse"], IsFavorite = true },
        new() { Name = "hyperv01", Host = "192.168.1.10", Description = "Hyper-V host", Group = "Homelab", Tags = ["homelab", "hyper-v"] },
        new() { Name = "nas", Type = ConnectionType.RustDesk, Host = "1 234 567 890", Port = 0, Description = "Synology, RustDesk relay", Group = "Homelab", Tags = ["homelab"] },
        new() { Name = "dev-vm", Host = "192.168.1.50", Description = "Test VM", Group = "Homelab", Tags = ["dev", "test", "local", "vm"] },
    ];
}
