using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteManager.Core.Credentials.KeePass;

/// <summary>
/// What a <see cref="Model.CredentialRef"/> into KeePass points at (stored as JSON in its key): the database
/// file, an optional key file, and the entry. No secret: the master password is asked at connect time.
/// </summary>
/// <param name="Entry">Entry UUID (32 hex characters), "Group/Subgroup/Title", or a title that is unique in the database.</param>
/// <param name="Field">Field to read; default the password.</param>
public sealed record KeePassReference(
    [property: JsonPropertyName("database")] string Database,
    [property: JsonPropertyName("entry")] string Entry,
    [property: JsonPropertyName("keyFile")] string? KeyFile = null,
    [property: JsonPropertyName("field")] string? Field = null)
{
    public const string PasswordField = "Password";

    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string FieldName => string.IsNullOrWhiteSpace(Field) ? PasswordField : Field;

    public string ToKey() => JsonSerializer.Serialize(this with { KeyFile = NullIfEmpty(KeyFile), Field = NullIfEmpty(Field) }, Options);

    /// <summary>Parses a reference key; throws <see cref="CredentialSourceException"/> (<see cref="CredentialError.InvalidReference"/>).</summary>
    public static KeePassReference Parse(string key)
    {
        try
        {
            if (JsonSerializer.Deserialize<KeePassReference>(key, Options) is { Database.Length: > 0, Entry.Length: > 0 } reference)
                return reference;
        }
        catch (JsonException ex)
        {
            throw new CredentialSourceException(CredentialError.InvalidReference, "KeePass", ex);
        }

        throw new CredentialSourceException(CredentialError.InvalidReference, "KeePass");
    }

    public static bool TryParse(string key, out KeePassReference? reference)
    {
        try
        {
            reference = Parse(key);
            return true;
        }
        catch (CredentialSourceException)
        {
            reference = null;
            return false;
        }
    }

    /// <summary>The entry this reference means: by UUID, then by path (with or without the root group), then by unique title.</summary>
    public KdbxEntry Resolve(IReadOnlyList<KdbxEntry> entries)
    {
        string wanted = Entry.Trim();
        string compactUuid = wanted.Replace("-", "", StringComparison.Ordinal).Trim('{', '}');
        if (compactUuid.Length == 32 && compactUuid.All(Uri.IsHexDigit)
            && entries.FirstOrDefault(e => e.Uuid.Equals(compactUuid, StringComparison.OrdinalIgnoreCase)) is { } byUuid)
            return byUuid;

        List<KdbxEntry> matches = wanted.Contains('/', StringComparison.Ordinal)
            ? [.. entries.Where(e => PathMatches(e, wanted.Trim('/')))]
            : [.. entries.Where(e => e.Title.Equals(wanted, StringComparison.OrdinalIgnoreCase))];
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new CredentialSourceException(CredentialError.EntryNotFound, wanted),
            _ => throw new CredentialSourceException(CredentialError.EntryAmbiguous, wanted),
        };
    }

    private static bool PathMatches(KdbxEntry entry, string path) =>
        entry.Path.Equals(path, StringComparison.OrdinalIgnoreCase)
        // A path copied from KeePass may start with the root group's name.
        || (path.IndexOf('/', StringComparison.Ordinal) is int slash and > 0 && entry.Path.Equals(path[(slash + 1)..], StringComparison.OrdinalIgnoreCase));

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
