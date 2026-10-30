using RemoteManager.Core.Credentials;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace RemoteManager.Core.Ssh;

/// <summary>
/// Everything needed to log on to one SSH server, resolved from a <see cref="Connection"/>. The secret
/// (password, or the key file's passphrase) only lives in memory; <see cref="ToString"/> never shows it.
/// </summary>
public sealed class SshLogin
{
    public required string ConnectionId { get; init; }
    public required string Name { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string UserName { get; init; }
    public SshAuthMethod AuthMethod { get; init; } = SshAuthMethod.Password;
    public string KeyPath { get; init; } = "";

    /// <summary>Password (<see cref="SshAuthMethod.Password"/>) or passphrase (<see cref="SshAuthMethod.KeyFile"/>); null when none is known.</summary>
    public string? Secret { get; init; }

    /// <summary>Agents to ask for <see cref="SshAuthMethod.Agent"/>; null means every known agent (<see cref="SshAgent.All"/>).</summary>
    public IReadOnlyList<SshAgent>? Agents { get; init; }

    /// <summary>The accepted host key ("SHA256:..."), empty when the host was never confirmed.</summary>
    public string KnownHostKey { get; init; } = "";

    public override string ToString() => $"{UserName}@{Host}:{Port} ({AuthMethod})";

    /// <summary>
    /// The logon of <paramref name="connection"/>: its own or inherited user and secret. A non-empty
    /// <paramref name="typedSecret"/> wins over the stored one.
    /// </summary>
    public static async Task<SshLogin> FromConnectionAsync(Connection connection, CredentialService credentials, string? typedSecret = null, CancellationToken cancellationToken = default)
    {
        EffectiveCredential credential = credentials.Resolve(connection);
        string? secret = typedSecret is { Length: > 0 } ? typedSecret : await credentials.ReadAsync(credential.Secret, cancellationToken).ConfigureAwait(false);
        SshSettings settings = connection.Settings as SshSettings ?? new SshSettings();
        return new SshLogin
        {
            ConnectionId = connection.Id,
            Name = connection.Name,
            Host = connection.Host.Trim(),
            Port = connection.EffectivePort,
            UserName = credential.UserName,
            AuthMethod = settings.AuthMethod,
            KeyPath = Environment.ExpandEnvironmentVariables(settings.KeyPath.Trim()),
            Secret = secret is { Length: > 0 } ? secret : null,
            KnownHostKey = settings.HostKeyFingerprint,
        };
    }

    /// <summary>SSH.NET connection data for this logon, aimed at <paramref name="host"/>:<paramref name="port"/> (the server itself or a local tunnel end).</summary>
    public ConnectionInfo CreateConnectionInfo(string host, int port)
    {
        if (UserName.Length == 0)
            throw new SshLoginException(SshLoginProblem.MissingUser, this);

        var methods = new List<AuthenticationMethod>();
        switch (AuthMethod)
        {
            case SshAuthMethod.KeyFile:
                methods.Add(new PrivateKeyAuthenticationMethod(UserName, LoadKeyFile()));
                break;
            case SshAuthMethod.Agent:
                SshAgentKeySource agentKeys = SshAgentKeySource.FromRunningAgents(Agents, out IReadOnlyList<string> problems);
                if (agentKeys.HostKeyAlgorithms.Count == 0)
                    throw new SshLoginException(SshLoginProblem.NoAgentKeys, this, string.Join(" ", problems));
                methods.Add(new PrivateKeyAuthenticationMethod(UserName, agentKeys));
                break;
            default:
                if (Secret is null)
                    throw new SshLoginException(SshLoginProblem.MissingPassword, this);
                methods.Add(new PasswordAuthenticationMethod(UserName, Secret));
                // Many servers only allow keyboard-interactive; answer its password prompt with the same secret.
                var interactive = new KeyboardInteractiveAuthenticationMethod(UserName);
                interactive.AuthenticationPrompt += (_, e) =>
                {
                    foreach (AuthenticationPrompt prompt in e.Prompts)
                        prompt.Response = Secret;
                };
                methods.Add(interactive);
                break;
        }

        return new ConnectionInfo(host, port, UserName, [.. methods]) { Timeout = TimeSpan.FromSeconds(20) };
    }

    private PrivateKeyFile LoadKeyFile()
    {
        if (KeyPath.Length == 0 || !File.Exists(KeyPath))
            throw new SshLoginException(SshLoginProblem.KeyFileMissing, this, KeyPath);
        try
        {
            return Secret is null ? new PrivateKeyFile(KeyPath) : new PrivateKeyFile(KeyPath, Secret);
        }
        catch (SshPassPhraseNullOrEmptyException ex)
        {
            throw new SshLoginException(SshLoginProblem.PassphraseRequired, this, inner: ex);
        }
        catch (SshException ex) when (Secret is not null)
        {
            // SSH.NET reports a wrong passphrase as a decryption or format error.
            throw new SshLoginException(SshLoginProblem.WrongPassphrase, this, ex.Message, ex);
        }
        catch (SshException ex)
        {
            throw new SshLoginException(SshLoginProblem.KeyFileUnreadable, this, ex.Message, ex);
        }
    }
}

public enum SshLoginProblem
{
    MissingUser,
    MissingPassword,
    KeyFileMissing,
    KeyFileUnreadable,
    PassphraseRequired,
    WrongPassphrase,
    NoAgentKeys,
    JumpHostMissing,
    JumpHostNotSsh,
    JumpHostLoop,
}

/// <summary>A logon cannot even be tried (missing input); <see cref="Login"/> names the hop.</summary>
public sealed class SshLoginException(SshLoginProblem problem, SshLogin? login, string? detail = null, Exception? inner = null)
    : Exception($"{problem}{(login is null ? "" : $" for {login}")}{(detail is { Length: > 0 } ? $": {detail}" : "")}", inner)
{
    public SshLoginProblem Problem { get; } = problem;
    public SshLogin? Login { get; } = login;
    public string Detail { get; } = detail ?? "";
}
