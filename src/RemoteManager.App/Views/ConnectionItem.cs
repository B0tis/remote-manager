using System.ComponentModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using RemoteManager.App.Connections;
using RemoteManager.App.Resources;
using RemoteManager.Core.Credentials;
using RemoteManager.Core.Model;
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
    public string TypeLabel => Handler.DisplayName;
    public string TypeInitials => Handler.Initials;
    public IBrush TypeBrush => new SolidColorBrush(Handler.AccentColor);
    public string StatusHint => Handler.StatusHint;

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
        using FileStream stream = File.OpenRead(path);
        Preview = new Bitmap(stream);
    }

    public bool Matches(string search) =>
        Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Model.Host.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Description.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Model.Notes.Contains(search, StringComparison.OrdinalIgnoreCase)
        || GroupPath.Contains(search, StringComparison.OrdinalIgnoreCase)
        || TypeLabel.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Model.Tags.Any(t => t.Contains(search.TrimStart('#'), StringComparison.OrdinalIgnoreCase));
}
