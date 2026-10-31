namespace RemoteManager.Core.Model;

/// <summary>
/// Points at a secret in a credential source (<see cref="Credentials.ICredentialSource"/>); the
/// secret itself never enters the model or the database.
/// </summary>
/// <param name="Source">Id of the source, for example <see cref="Credentials.WindowsCredentialSource.SourceId"/>.</param>
/// <param name="Key">Key inside that source (Windows Credential Manager: the connection id).</param>
public sealed record CredentialRef(string Source, string Key);
