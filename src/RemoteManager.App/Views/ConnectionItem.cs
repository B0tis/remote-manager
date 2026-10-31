using System.ComponentModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RemoteManager.App.Connections;
using RemoteManager.App.Resources;
using RemoteManager.Core.Credentials;
using RemoteManager.Core.Model;
using RemoteManager.Core.Search;
using RemoteManager.Core.Ssh;
using RemoteManager.Core.Status;

namespace RemoteManager.App.Views;

/// <summary>
/// A <see cref="Connection"/> as the overview shows it: labels, brushes, status, and the preview
/// image. The model stays UI-free; everything visual comes from the type's <see cref="IConnectionHandler"/>.
/// </summary>
public sealed class ConnectionItem : INotifyPropertyChanged
{
    private readonly AppHost app;
    private OnlineStatus status;
    private Bitmap? preview;
    private DateTimeOffset? livePreviewAt;
    private bool hasStoredPassword;

    public ConnectionItem(Connection model, AppHost app)
    {
        Model = model;
        this.app = app;
        RefreshCredentialState();
    }

    public Connection Model { get; }
    public IConnectionHandler Handler => app.Handlers[Model.Type];

    public string Id => Model.Id;
    public string Name => Model.Name;
    public string Description => Model.Description;

    public bool IsFavorite
    {
        get => Model.IsFavorite;
        set
        {
            Model.IsFavorite = value;
            Changed(nameof(IsFavorite));
        }
    }

    public OnlineStatus Status
    {
        get => status;
        set
        {
            status = value;
            StatusCheckedAt = DateTime.Now;
            Changed(string.Empty);
        }
    }

    public Bitmap? Preview
    {
        get => preview;
        private set
        {
            preview = value;
            Changed(string.Empty);
        }
    }

    /// <summary>A password is available for this connection, its own or inherited from a group (cached, see <see cref="RefreshCredentialState"/>).</summary>
    public bool HasStoredPassword
    {
        get => hasStoredPassword;
        private set
        {
            hasStoredPassword = value;
            Changed(nameof(HasStoredPassword));
        }
    }

    public EffectiveCredential Credentials => app.Credentials.Resolve(Model);

    public bool HasPreview => Preview is not null;

    /// <summary>When the preview of the open session was last refreshed; null when the preview is not live.</summary>
    public DateTimeOffset? LivePreviewAt
    {
        get => livePreviewAt;
        set
        {
            livePreviewAt = value;
            Changed(nameof(IsLivePreview));
            Changed(nameof(LivePreviewAge));
        }
    }

    /// <summary>The preview shows the open session ("Live" badge on the tile and in the detail panel).</summary>
    public bool IsLivePreview => livePreviewAt is not null;

    /// <summary>Age of the live preview: "gerade eben", "vor 20 s", "vor 2 min".</summary>
    public string LivePreviewAge => livePreviewAt is { } at ? DescribeAge(DateTimeOffset.Now - at) : "";
    public string TypeLabel => Handler.DisplayName;
    public string TypeInitials => Handler.Initials;
    public IBrush TypeBrush => new SolidColorBrush(Handler.AccentColor);
    public string StatusHint => SshJumpChain.JumpHostOf(Model) is null ? Handler.StatusHint : Strings.Status_Hint_Tunneled;

    /// <summary>Local time of the last status result, null before the first check.</summary>
    public DateTime? StatusCheckedAt { get; private set; }

    /// <summary>Tooltip of the status dot: how the status is found out and when it was last checked.</summary>
    public string StatusTip => StatusCheckedAt is { } at
        ? $"{StatusLabel} · {StatusHint}\n{string.Format(CultureInfo.CurrentCulture, Strings.Status_CheckedAt, at.ToString("T", CultureInfo.CurrentCulture))}"
        : $"{StatusLabel} · {StatusHint}";

    /// <summary>Tile footer: group and last connection.</summary>
    public string FooterLabel => $"{GroupLabel} · {LastConnectedLabel}";

    public string HostLabel => Handler.HasPort && Model.Port > 0 && Model.Port != DefaultPorts.For(Model.Type)
        ? $"{Model.Host}:{Model.Port}"
        : Model.Host;

    public string GroupPath => app.Library.PathOf(Model.GroupId);
    public string GroupLabel => GroupPath is { Length: > 0 } path ? path : Strings.Group_None;

    public string StatusLabel => Status switch
    {
        OnlineStatus.Online => Strings.Status_Online,
        OnlineStatus.Offline => Strings.Status_Offline,
        _ => Strings.Status_Unknown,
    };

    public IBrush StatusBrush => new SolidColorBrush(Status switch
    {
        OnlineStatus.Online => Color.FromRgb(0x22, 0xC5, 0x5E),
        OnlineStatus.Offline => Color.FromRgb(0xEF, 0x44, 0x44),
        _ => Color.FromRgb(0x9C, 0xA3, 0xAF),
    });

    /// <summary>Up to three tags, then "+n".</summary>
    public IReadOnlyList<string> VisibleTags =>
        Model.Tags.Count <= 3 ? Model.Tags : [.. Model.Tags.Take(3), $"+{Model.Tags.Count - 3}"];

    public string LastConnectedLabel => Model.LastConnectedAt is { } at
        ? at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : Strings.LastConnected_Never;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises a change for every property, used after the model changed.</summary>
    public void Changed(string property = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    public void RefreshCredentialState() => HasStoredPassword = Credentials.HasSecret;

    public void ReloadPreview()
    {
        string path = Model.ResolvedPreviewPath;
        if (!File.Exists(path))
        {
            Preview = null;
            return;
        }

        // Decode from a stream we close right away, so the next snapshot can overwrite the file.
        Bitmap? previous = preview;
        using (FileStream stream = File.OpenRead(path))
            Preview = new Bitmap(stream);

        // A live preview reloads every few seconds: free the old picture once nothing shows it any more.
        if (previous is not null)
            Dispatcher.UIThread.Post(previous.Dispose, DispatcherPriority.Background);
    }

    internal static string DescribeAge(TimeSpan age) =>
        age < TimeSpan.FromSeconds(5) ? Strings.LivePreview_AgeNow
        : age < TimeSpan.FromMinutes(1) ? string.Format(CultureInfo.CurrentCulture, Strings.LivePreview_AgeSeconds, (int)age.TotalSeconds)
        : string.Format(CultureInfo.CurrentCulture, Strings.LivePreview_AgeMinutes, (int)age.TotalMinutes);

    /// <summary>What the quick launcher searches, weighted: name first, then host and tags.</summary>
    public IEnumerable<SearchField> SearchFields =>
    [
        new(Name, 4), new(Model.Host, 3), .. Model.Tags.Select(t => new SearchField(t, 2)),
        new(GroupPath, 2), new(Description, 1), new(TypeLabel, 1),
    ];

    public bool Matches(string search) =>
        Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Model.Host.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Description.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Model.Notes.Contains(search, StringComparison.OrdinalIgnoreCase)
        || GroupPath.Contains(search, StringComparison.OrdinalIgnoreCase)
        || TypeLabel.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Model.Tags.Any(t => t.Contains(search.TrimStart('#'), StringComparison.OrdinalIgnoreCase));
}
