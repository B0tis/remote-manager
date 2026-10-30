using System.ComponentModel;
using System.Globalization;
using RemoteManager.App.Resources;
using RemoteManager.Core.Credentials;

namespace RemoteManager.App.Credentials;

/// <summary>German messages for failures of the credential sources (Credential Manager, KeePass, 1Password).</summary>
public static class CredentialErrors
{
    /// <summary>Whether <paramref name="exception"/> is a credential source failure the UI should show (not a bug).</summary>
    public static bool IsCredentialFailure(Exception exception) =>
        exception is CredentialSourceException || (exception is Win32Exception && exception is not System.Net.Sockets.SocketException);

    /// <summary>The message for a failed read; never contains a secret.</summary>
    public static string Describe(Exception exception) => exception switch
    {
        CredentialSourceException source => Describe(source),
        _ => string.Format(CultureInfo.CurrentCulture, Strings.Password_ReadFailed, exception.Message),
    };

    public static string Describe(CredentialSourceException exception)
    {
        string format = exception.Error switch
        {
            CredentialError.Canceled => Strings.Vault_Err_Canceled,
            CredentialError.InvalidReference => Strings.Vault_Err_InvalidReference,
            CredentialError.FileNotFound => Strings.Vault_Err_FileNotFound,
            CredentialError.WrongMasterKey => Strings.Vault_Err_WrongMasterKey,
            CredentialError.UnsupportedFormat => Strings.Vault_Err_UnsupportedFormat,
            CredentialError.EntryNotFound => Strings.Vault_Err_EntryNotFound,
            CredentialError.EntryAmbiguous => Strings.Vault_Err_EntryAmbiguous,
            CredentialError.ToolMissing => Strings.Vault_Err_ToolMissing,
            CredentialError.NotSignedIn => Strings.Vault_Err_NotSignedIn,
            _ => Strings.Vault_Err_ToolFailed,
        };
        return string.Format(CultureInfo.CurrentCulture, format, exception.Detail);
    }

    /// <summary>The credential failure inside <paramref name="exception"/> (libraries may wrap it), or null.</summary>
    public static Exception? Find(Exception? exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (IsCredentialFailure(current))
                return current;
            if (current is AggregateException { InnerExceptions.Count: > 0 } aggregate && Find(aggregate.InnerExceptions[0]) is { } inner)
                return inner;
        }

        return null;
    }
}
