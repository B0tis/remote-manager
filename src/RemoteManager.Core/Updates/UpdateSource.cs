namespace RemoteManager.Core.Updates;

public enum UpdateSourceKind { GitHub, Web, Folder }

/// <summary>
/// Where updates come from, entered as one string (setting "update.source"): a GitHub repository URL
/// (its releases), any other http(s) URL (a folder of Velopack release files on a web server), or a local or
/// network folder. Empty means <see cref="Default"/>.
/// </summary>
public sealed record UpdateSource(UpdateSourceKind Kind, string Location)
{
    public const string DefaultRepository = "https://github.com/B0tis/remote-manager";

    public static UpdateSource Default { get; } = new(UpdateSourceKind.GitHub, DefaultRepository);

    /// <summary>Parses user input; null when it is neither a URL nor a rooted path.</summary>
    public static UpdateSource? Parse(string? text)
    {
        string value = text?.Trim().Trim('"') ?? "";
        if (value.Length == 0)
            return Default;

        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            if (uri.Scheme is "http" or "https")
            {
                bool github = uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                    && uri.AbsolutePath.Trim('/').Split('/').Length == 2;
                return new UpdateSource(github ? UpdateSourceKind.GitHub : UpdateSourceKind.Web, value.TrimEnd('/'));
            }

            if (uri.IsFile)
                return new UpdateSource(UpdateSourceKind.Folder, uri.LocalPath);
        }

        return Path.IsPathFullyQualified(value) ? new UpdateSource(UpdateSourceKind.Folder, value) : null;
    }

    /// <summary>A GitHub source needs a token while the repository is private.</summary>
    public bool CanUseToken => Kind == UpdateSourceKind.GitHub;

    public override string ToString() => Location;
}
