namespace RemoteManager.Core.Credentials;

/// <summary>Why a credential source could not deliver a secret; the app turns it into a German message.</summary>
public enum CredentialError
{
    /// <summary>The user closed the unlock prompt (or the password manager's approval).</summary>
    Canceled,

    /// <summary>The reference is malformed (bad JSON, not an <c>op://</c> reference, ...).</summary>
    InvalidReference,

    /// <summary>The database file or key file does not exist.</summary>
    FileNotFound,

    /// <summary>Master password or key file is wrong (or the file is damaged).</summary>
    WrongMasterKey,

    /// <summary>The file is no KeePass database or uses a format this reader does not support.</summary>
    UnsupportedFormat,

    /// <summary>No entry or item matches the reference.</summary>
    EntryNotFound,

    /// <summary>More than one entry matches the reference (a title that is not unique).</summary>
    EntryAmbiguous,

    /// <summary>The external tool (1Password <c>op</c>) is not installed.</summary>
    ToolMissing,

    /// <summary>The external tool is installed but not signed in.</summary>
    NotSignedIn,

    /// <summary>The external tool failed; <see cref="CredentialSourceException.Detail"/> has its message.</summary>
    ToolFailed,
}

/// <summary>
/// A credential source failed. Never contains a secret: <see cref="Detail"/> is a file name, a reference,
/// or the error text of an external tool.
/// </summary>
public sealed class CredentialSourceException : Exception
{
    public CredentialSourceException(CredentialError error, string? detail = null, Exception? inner = null)
        : base($"{error}{(string.IsNullOrEmpty(detail) ? "" : $": {detail}")}", inner)
    {
        Error = error;
        Detail = detail ?? "";
    }

    public CredentialSourceException()
        : this(CredentialError.ToolFailed)
    {
    }

    public CredentialSourceException(string message)
        : this(CredentialError.ToolFailed, message)
    {
    }

    public CredentialSourceException(string message, Exception innerException)
        : this(CredentialError.ToolFailed, message, innerException)
    {
    }

    public CredentialError Error { get; }

    public string Detail { get; }
}
