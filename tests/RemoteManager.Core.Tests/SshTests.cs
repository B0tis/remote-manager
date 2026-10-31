using System.Numerics;
using System.Security.Cryptography;
using RemoteManager.Core.Credentials;
using RemoteManager.Core.Library;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Ssh;
using RemoteManager.Core.Storage;
using Renci.SshNet;
using Renci.SshNet.Security;
using Renci.SshNet.Security.Cryptography;

namespace RemoteManager.Core.Tests;

public sealed class SshTests : IDisposable
{
    private readonly TempFolder folder = new();
    private readonly ConnectionLibrary library;
    private readonly MemoryCredentialSource source = new();
    private readonly CredentialService credentials;

    public SshTests()
    {
        library = new ConnectionLibrary(new SqliteConnectionStore(folder.File("ssh.db")));
        credentials = new CredentialService([source], library);
    }

    public void Dispose() => folder.Dispose();

    [Theory]
    [InlineData("L 8080:intranet:80", PortForwardKind.Local, "", 8080, "intranet", 80)]
    [InlineData("-L 127.0.0.1:8080:intranet:80", PortForwardKind.Local, "127.0.0.1", 8080, "intranet", 80)]
    [InlineData("r 9000:localhost:3000", PortForwardKind.Remote, "", 9000, "localhost", 3000)]
    [InlineData("D 1080", PortForwardKind.Dynamic, "", 1080, "", 0)]
    [InlineData("D 0.0.0.0:1080", PortForwardKind.Dynamic, "0.0.0.0", 1080, "", 0)]
    public void PortForwardParsesAndFormatsBack(string line, PortForwardKind kind, string bind, int bindPort, string host, int port)
    {
        PortForward? forward = PortForwardText.Parse(line);

        Assert.Equal(new PortForward(kind, bind, bindPort, host, port), forward);
        Assert.Equal(forward, PortForwardText.Parse(PortForwardText.Format(forward!)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("L 8080")]
    [InlineData("L 70000:host:80")]
    [InlineData("X 1:a:2")]
    [InlineData("L8080:a:80")]
    [InlineData("D host:")]
    public void PortForwardRejectsInvalidLines(string line) => Assert.Null(PortForwardText.Parse(line));

    [Fact]
    public void JumpHostOfCoversEveryTunneledType()
    {
        Assert.Equal("j", SshJumpChain.JumpHostOf(new Connection { Type = ConnectionType.Ssh, Settings = new SshSettings { JumpHostId = "j" } }));
        Assert.Equal("j", SshJumpChain.JumpHostOf(new Connection { Type = ConnectionType.Rdp, Settings = new RdpSettings { JumpHostId = "j" } }));
        Assert.Equal("j", SshJumpChain.JumpHostOf(new Connection { Type = ConnectionType.Vnc, Settings = new VncSettings { JumpHostId = "j" } }));
        Assert.Null(SshJumpChain.JumpHostOf(new Connection { Type = ConnectionType.Vnc, Settings = new VncSettings { JumpHostId = "" } }));
        Assert.Null(SshJumpChain.JumpHostOf(new Connection { Type = ConnectionType.Rdp }));
        Assert.Null(SshJumpChain.JumpHostOf(new Connection { Type = ConnectionType.TeamViewer }));
    }

    [Fact]
    public void PortForwardParseAllReportsTheFirstBadLine()
    {
        IReadOnlyList<PortForward> forwards = PortForwardText.ParseAll("L 1:a:2\n\nnonsense\nD 3\nalso bad", out int invalid);

        Assert.Equal(2, forwards.Count);
        Assert.Equal(3, invalid);
    }

    [Fact]
    public void HostKeyCheckDistinguishesTrustedUnknownAndChanged()
    {
        var key = new SshHostKey("ssh-ed25519", "SHA256:abcDEF123+/xyz", "aa:bb:cc", 256);

        Assert.Equal(HostKeyStatus.Unknown, key.CheckAgainst(""));
        Assert.Equal(HostKeyStatus.Unknown, key.CheckAgainst(null));
        Assert.Equal(HostKeyStatus.Trusted, key.CheckAgainst("SHA256:abcDEF123+/xyz"));
        Assert.Equal(HostKeyStatus.Trusted, key.CheckAgainst("abcDEF123+/xyz="));
        Assert.Equal(HostKeyStatus.Trusted, key.CheckAgainst("MD5:AA:BB:CC"));
        // Base64 is case-sensitive: a different case is a different key.
        Assert.Equal(HostKeyStatus.Changed, key.CheckAgainst("SHA256:ABCdef123+/xyz"));
        Assert.Equal(HostKeyStatus.Changed, key.CheckAgainst("SHA256:somethingelse"));
    }

    [Fact]
    public void JumpChainIsOutermostFirst()
    {
        Connection outer = AddSsh("outer");
        Connection inner = AddSsh("inner", jumpHostId: outer.Id);
        Connection target = AddSsh("target", jumpHostId: inner.Id);

        IReadOnlyList<Connection> chain = SshJumpChain.Resolve(target.SettingsAs<SshSettings>().JumpHostId, library.FindConnection, target.Id);

        Assert.Equal([outer, inner], chain);
        Assert.Empty(SshJumpChain.Resolve(null, library.FindConnection));
    }

    [Fact]
    public void JumpChainRejectsLoopsMissingAndNonSshHosts()
    {
        Connection a = AddSsh("a");
        Connection b = AddSsh("b", jumpHostId: a.Id);
        a.Settings = a.SettingsAs<SshSettings>() with { JumpHostId = b.Id };
        var rdp = new Connection { Name = "rdp", Type = ConnectionType.Rdp, Host = "x" };
        library.Add(rdp);

        Assert.Equal(SshLoginProblem.JumpHostLoop, Assert.Throws<SshLoginException>(() => SshJumpChain.Resolve(b.Id, library.FindConnection)).Problem);
        // A connection that is its own jump host.
        Assert.Equal(SshLoginProblem.JumpHostLoop, Assert.Throws<SshLoginException>(() => SshJumpChain.Resolve(a.Id, library.FindConnection, a.Id)).Problem);
        Assert.Equal(SshLoginProblem.JumpHostMissing, Assert.Throws<SshLoginException>(() => SshJumpChain.Resolve("gone", library.FindConnection)).Problem);
        Assert.Equal(SshLoginProblem.JumpHostNotSsh, Assert.Throws<SshLoginException>(() => SshJumpChain.Resolve(rdp.Id, library.FindConnection)).Problem);
    }

    [Fact]
    public async Task LoginUsesInheritedCredentialsAndTypedSecretWins()
    {
        ConnectionGroup group = library.EnsureGroupPath("Linux")!;
        group.UserName = "admin";
        await credentials.SaveGroupSecretAsync(group, "group-secret", TestContext.Current.CancellationToken);
        library.UpdateGroup(group);
        Connection connection = AddSsh("web", groupId: group.Id);
        connection.Settings = connection.SettingsAs<SshSettings>() with { HostKeyFingerprint = "SHA256:known", KeyPath = "%TEMP%\\id" };

        SshLogin stored = await SshLogin.FromConnectionAsync(connection, credentials, cancellationToken: TestContext.Current.CancellationToken);
        SshLogin typed = await SshLogin.FromConnectionAsync(connection, credentials, "typed", TestContext.Current.CancellationToken);

        Assert.Equal(("admin", "group-secret", 22, "SHA256:known"), (stored.UserName, stored.Secret, stored.Port, stored.KnownHostKey));
        Assert.Equal("typed", typed.Secret);
        Assert.Equal(Path.Combine(Path.GetTempPath().TrimEnd('\\'), "id"), stored.KeyPath);
        Assert.DoesNotContain("group-secret", stored.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LoginReportsMissingInputBeforeConnecting()
    {
        SshLogin Login(SshAuthMethod method, string user = "u", string? secret = null, string keyPath = "") => new()
        {
            ConnectionId = "c", Name = "n", Host = "h", Port = 22, UserName = user, AuthMethod = method, Secret = secret, KeyPath = keyPath,
            Agents = [new OpenSshAgent($"rm-test-no-agent-{Guid.NewGuid():N}")],
        };

        SshLoginProblem Problem(SshLogin login) => Assert.Throws<SshLoginException>(() => login.CreateConnectionInfo("h", 22)).Problem;

        Assert.Equal(SshLoginProblem.MissingUser, Problem(Login(SshAuthMethod.Password, user: "", secret: "x")));
        Assert.Equal(SshLoginProblem.MissingPassword, Problem(Login(SshAuthMethod.Password)));
        Assert.Equal(SshLoginProblem.KeyFileMissing, Problem(Login(SshAuthMethod.KeyFile, keyPath: folder.File("none"))));
        Assert.Equal(SshLoginProblem.NoAgentKeys, Problem(Login(SshAuthMethod.Agent)));

        File.WriteAllText(folder.File("garbage"), "not a key");
        Assert.Equal(SshLoginProblem.KeyFileUnreadable, Problem(Login(SshAuthMethod.KeyFile, keyPath: folder.File("garbage"))));

        ConnectionInfo info = Login(SshAuthMethod.Password, secret: "pw").CreateConnectionInfo("127.0.0.1", 2200);
        Assert.Equal(("127.0.0.1", 2200), (info.Host, info.Port));
        Assert.Equal(["password", "keyboard-interactive"], info.AuthenticationMethods.Select(m => m.Name));
    }

    [Fact]
    public void AgentListsKeysAndSigns()
    {
        var ed25519 = new PrivateKeyFile(new ED25519Key(RandomNumberGenerator.GetBytes(32)));
        using var agent = new SshTestAgent((ed25519, "test key äß"));

        SshAgentIdentity identity = Assert.Single(agent.Client.GetIdentities());
        Assert.Equal("ssh-ed25519", identity.KeyType);
        Assert.Equal("test key äß", identity.Comment);

        byte[] data = RandomNumberGenerator.GetBytes(64);
        HostAlgorithm viaAgent = Assert.Single(new SshAgentKeySource([identity]).HostKeyAlgorithms);
        HostAlgorithm local = ed25519.HostKeyAlgorithms.First();
        Assert.Equal(local.Data, viaAgent.Data);
        Assert.True(local.VerifySignature(data, viaAgent.Sign(data)));
        Assert.Equal(1, agent.SignRequests);
    }

    [Fact]
    public void AgentRsaKeysAreOfferedWithSha2Signatures()
    {
        using RSA rsa = RSA.Create(2048);
        RSAParameters p = rsa.ExportParameters(includePrivateParameters: true);
        static BigInteger Big(byte[] value) => new(value, isUnsigned: true, isBigEndian: true);
        var rsaKey = new RsaKey(Big(p.Modulus!), Big(p.Exponent!), Big(p.D!), Big(p.P!), Big(p.Q!), Big(p.InverseQ!));
        // PrivateKeyFile(Key) only offers ssh-rsa; a key file read from disk also offers the SHA-2 variants.
        var key = new KeySource(
            new KeyHostAlgorithm("ssh-rsa", rsaKey),
            new KeyHostAlgorithm("rsa-sha2-512", rsaKey, new RsaDigitalSignature(rsaKey, HashAlgorithmName.SHA512)),
            new KeyHostAlgorithm("rsa-sha2-256", rsaKey, new RsaDigitalSignature(rsaKey, HashAlgorithmName.SHA256)));
        using var agent = new SshTestAgent((key, "rsa"));

        var source = new SshAgentKeySource(agent.Client.GetIdentities());
        byte[] data = RandomNumberGenerator.GetBytes(32);

        Assert.Equal(["rsa-sha2-512", "rsa-sha2-256"], source.HostKeyAlgorithms.Select(a => a.Name));
        foreach (HostAlgorithm algorithm in source.HostKeyAlgorithms)
        {
            HostAlgorithm local = key.HostKeyAlgorithms.Single(a => a.Name == algorithm.Name);
            Assert.True(local.VerifySignature(data, algorithm.Sign(data)), algorithm.Name);
        }

        Assert.Equal([SshAgent.RsaSha2_512, SshAgent.RsaSha2_256], agent.SignFlags);
    }

    [Fact]
    public void AgentThatIsNotRunningIsReportedNotThrown()
    {
        var missing = new OpenSshAgent($"rm-test-missing-{Guid.NewGuid():N}");

        Assert.False(missing.IsRunning);
        Assert.Throws<SshAgentException>(() => missing.GetIdentities());
        Assert.Empty(SshAgentKeySource.FromRunningAgents([missing], out IReadOnlyList<string> problems).HostKeyAlgorithms);
        Assert.Empty(problems);
    }

    [Fact]
    public void SshSettingsRoundTripThroughJson()
    {
        var settings = new SshSettings
        {
            AuthMethod = SshAuthMethod.Agent, KeyPath = @"C:\keys\id", JumpHostId = "j", HostKeyFingerprint = "SHA256:x", RemoteDirectory = "/var/log",
            PortForwards = [new PortForward(PortForwardKind.Local, "", 8080, "web", 80), new PortForward(PortForwardKind.Dynamic, "", 1080, "", 0)],
        };

        var back = (SshSettings)ConnectionSettings.Deserialize(ConnectionType.Ssh, ConnectionSettings.Serialize(settings));

        Assert.Equal(settings with { PortForwards = [] }, back with { PortForwards = [] });
        Assert.Equal(settings.PortForwards, back.PortForwards);
    }

    private sealed class KeySource(params HostAlgorithm[] algorithms) : IPrivateKeySource
    {
        public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; } = algorithms;
    }

    private Connection AddSsh(string name, string? jumpHostId = null, string? groupId = null)
    {
        var connection = new Connection { Name = name, Type = ConnectionType.Ssh, Host = $"{name}.example", GroupId = groupId };
        connection.Settings = connection.SettingsAs<SshSettings>() with { JumpHostId = jumpHostId };
        library.Add(connection);
        return connection;
    }
}
