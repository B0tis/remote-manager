using Renci.SshNet;
using Renci.SshNet.Security;

namespace RemoteManager.Core.Ssh;

/// <summary>
/// The keys of one or more agents as an SSH.NET key source. SSH.NET first asks the server whether it
/// accepts a key and only then signs, so offering every agent key costs one round trip per key.
/// RSA keys are offered with SHA-2 signatures only (OpenSSH refuses ssh-rsa/SHA-1 by default).
/// </summary>
public sealed class SshAgentKeySource : IPrivateKeySource
{
    public SshAgentKeySource(IEnumerable<SshAgentIdentity> identities)
    {
        var algorithms = new List<HostAlgorithm>();
        foreach (SshAgentIdentity identity in identities)
        {
            if (identity.KeyType == "ssh-rsa")
            {
                algorithms.Add(new AgentHostAlgorithm("rsa-sha2-512", identity, SshAgent.RsaSha2_512));
                algorithms.Add(new AgentHostAlgorithm("rsa-sha2-256", identity, SshAgent.RsaSha2_256));
            }
            else
            {
                algorithms.Add(new AgentHostAlgorithm(identity.KeyType, identity, 0));
            }
        }

        HostKeyAlgorithms = algorithms;
    }

    public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; }

    /// <summary>The keys of every running agent (default <see cref="SshAgent.All"/>); agents that fail are skipped and reported in <paramref name="problems"/>.</summary>
    public static SshAgentKeySource FromRunningAgents(IEnumerable<SshAgent>? agents, out IReadOnlyList<string> problems)
    {
        var identities = new List<SshAgentIdentity>();
        var errors = new List<string>();
        foreach (SshAgent agent in (agents ?? SshAgent.All).Where(a => a.IsRunning))
        {
            try
            {
                identities.AddRange(agent.GetIdentities());
            }
            catch (SshAgentException ex)
            {
                errors.Add(ex.Message);
            }
        }

        problems = errors;
        return new SshAgentKeySource(identities);
    }

    private sealed class AgentHostAlgorithm(string name, SshAgentIdentity identity, uint flags) : HostAlgorithm(name)
    {
        public override byte[] Data => identity.PublicKeyBlob;

        public override byte[] Sign(byte[] data) => identity.Agent.Sign(identity.PublicKeyBlob, data, flags);

        public override bool VerifySignature(byte[] data, byte[] signature) =>
            throw new NotSupportedException("Agent keys only sign.");
    }
}
