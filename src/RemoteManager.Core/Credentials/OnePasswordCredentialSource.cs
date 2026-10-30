using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace RemoteManager.Core.Credentials;

/// <summary>
/// Read-only secrets from 1Password through its CLI: <c>op read --no-newline op://Tresor/Eintrag/Feld</c>.
/// The reference key is the secret reference itself (no secret). The secret only travels over the CLI's
/// stdout into memory at connect time; it is never on a command line, in a file, or in a log. Sign-in and
/// approval are 1Password's (desktop app integration / Windows Hello), so the first read may take a moment.
/// </summary>
public sealed partial class OnePasswordCredentialSource : ICredentialSource
{
    public const string SourceId = "1password";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    public string Id => SourceId;
    public bool IsWritable => false;

    /// <summary>Always registered so references keep working; a missing CLI is reported when reading.</summary>
    public bool IsAvailable => true;

    /// <summary>Path of op.exe; null finds it on PATH and in the usual install folders.</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>How long a read may take (includes the user approving in 1Password).</summary>
    public TimeSpan Timeout { get; set; } = DefaultTimeout;

    /// <summary>The op executable that would be used, or null when the CLI is not installed.</summary>
    public string? FindExecutable()
    {
        if (!string.IsNullOrEmpty(ExecutablePath))
            return File.Exists(ExecutablePath) ? ExecutablePath : null;

        IEnumerable<string> folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        folders = folders.Concat(
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "1Password CLI"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "1Password", "app", "8"),
        ]);
        foreach (string folder in folders)
        {
            try
            {
                string candidate = Path.Combine(folder, "op.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="reference"/> looks like "op://vault/item/field" (or with a section).</summary>
    public static bool IsValidReference(string reference) => ReferencePattern().IsMatch(reference.Trim());

    public async ValueTask<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        string reference = key.Trim();
        if (!IsValidReference(reference))
            throw new CredentialSourceException(CredentialError.InvalidReference, reference);
        string executable = FindExecutable() ?? throw new CredentialSourceException(CredentialError.ToolMissing, "op");

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // The reference is not secret; ArgumentList quotes it without a shell.
        start.ArgumentList.Add("read");
        start.ArgumentList.Add("--no-newline");
        start.ArgumentList.Add(reference);

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new CredentialSourceException(CredentialError.ToolMissing, executable, ex);
        }

        // No terminal to type into: a CLI asking for input gets EOF and fails instead of hanging.
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            throw new CredentialSourceException(cancellationToken.IsCancellationRequested ? CredentialError.Canceled : CredentialError.ToolFailed, "timeout");
        }

        string secret = await output.ConfigureAwait(false);
        string message = await error.ConfigureAwait(false);
        if (process.ExitCode == 0)
            return secret;

        throw Classify(message);
    }

    public async ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            return await ReadAsync(key, cancellationToken).ConfigureAwait(false) is not null;
        }
        catch (CredentialSourceException ex) when (ex.Error == CredentialError.EntryNotFound)
        {
            return false;
        }
    }

    public ValueTask WriteAsync(string key, string secret, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("1Password items are read-only here.");

    public ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

    /// <summary>Maps op's error text (stderr; never contains the secret) to an error kind.</summary>
    internal static CredentialSourceException Classify(string stderr)
    {
        // "[ERROR] 2026/10/30 12:00:00 could not read secret ...": keep the message, drop the prefix.
        string message = ErrorPrefix().Replace(stderr.Trim().Split('\n')[0], "").Trim();
        string lower = message.ToLowerInvariant();
        CredentialError error =
            lower.Contains("not currently signed in", StringComparison.Ordinal) || lower.Contains("no accounts configured", StringComparison.Ordinal)
                || lower.Contains("sign in", StringComparison.Ordinal) || lower.Contains("signin", StringComparison.Ordinal) ? CredentialError.NotSignedIn :
            lower.Contains("dismissed", StringComparison.Ordinal) || lower.Contains("cancel", StringComparison.Ordinal) ? CredentialError.Canceled :
            lower.Contains("more than one", StringComparison.Ordinal) ? CredentialError.EntryAmbiguous :
            lower.Contains("isn't an item", StringComparison.Ordinal) || lower.Contains("isn't a vault", StringComparison.Ordinal)
                || lower.Contains("could not find", StringComparison.Ordinal) || lower.Contains("not found", StringComparison.Ordinal)
                || lower.Contains("no item", StringComparison.Ordinal) || lower.Contains("does not have a field", StringComparison.Ordinal) ? CredentialError.EntryNotFound :
            CredentialError.ToolFailed;
        return new CredentialSourceException(error, message);
    }

    [GeneratedRegex(@"^op://[^/\s][^/]*/[^/]+(/[^/]+){1,2}(\?[^\s]*)?$")]
    private static partial Regex ReferencePattern();

    [GeneratedRegex(@"^\[ERROR\]\s*(\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2}\s*)?")]
    private static partial Regex ErrorPrefix();
}
