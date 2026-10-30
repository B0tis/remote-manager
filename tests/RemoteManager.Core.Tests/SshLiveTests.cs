using System.Net.Sockets;
using System.Text;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace RemoteManager.Core.Tests;

/// <summary>
/// Against the Docker testbed (tools/ssh-testbed/start.ps1): a jump host on RM_SSH_TEST_HOST:RM_SSH_TEST_PORT
/// and RM_SSH_TEST_TARGET, which is only reachable through it. Skipped when the variables are not set.
/// </summary>
public sealed class SshLiveTests
{
    private static readonly string? Host = Environment.GetEnvironmentVariable("RM_SSH_TEST_HOST");
    private static readonly int Port = int.TryParse(Environment.GetEnvironmentVariable("RM_SSH_TEST_PORT"), out int p) ? p : 22;
    private static readonly string User = Environment.GetEnvironmentVariable("RM_SSH_TEST_USER") ?? "";
    private static readonly string? Password = Environment.GetEnvironmentVariable("RM_SSH_TEST_PASSWORD");
    private static readonly string? KeyPath = Environment.GetEnvironmentVariable("RM_SSH_TEST_KEY");
    private static readonly string? Target = Environment.GetEnvironmentVariable("RM_SSH_TEST_TARGET");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static SshLogin Jump(SshAuthMethod method = SshAuthMethod.Password, string? secret = null, string known = "", IReadOnlyList<SshAgent>? agents = null)
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Host), "SSH testbed not running (tools/ssh-testbed/start.ps1).");
        return new SshLogin
        {
            ConnectionId = "jump", Name = "jump", Host = Host!, Port = Port, UserName = User, AuthMethod = method,
            Secret = method == SshAuthMethod.Password ? secret ?? Password : secret, KeyPath = KeyPath ?? "", KnownHostKey = known, Agents = agents,
        };
    }

    /// <summary>Connects once without a known key to learn it (the "unknown host key" path).</summary>
    private static async Task<string> LearnHostKeyAsync(SshRoute route, SshLogin login)
    {
        SshHostKeyException ex = await Assert.ThrowsAsync<SshHostKeyException>(() => route.ConnectAsync(login, i => new SshClient(i), Token));
        Assert.Equal(HostKeyStatus.Unknown, ex.Status);
        Assert.StartsWith("SHA256:", ex.Presented.Sha256Fingerprint, StringComparison.Ordinal);
        return ex.Presented.Sha256Fingerprint;
    }

    private static string Run(SshClient client, string command) => client.RunCommand(command).Result.Trim();

    [Fact]
    public async Task UnknownAndChangedHostKeysAreRefusedUntilAccepted()
    {
        SshLogin login = Jump();
        using var route = await SshRoute.OpenAsync([], Token);
        string fingerprint = await LearnHostKeyAsync(route, login);

        SshLogin changed = Jump(known: "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        SshHostKeyException ex = await Assert.ThrowsAsync<SshHostKeyException>(() => route.ConnectAsync(changed, i => new SshClient(i), Token));
        Assert.Equal(HostKeyStatus.Changed, ex.Status);

        SshClient client = await route.ConnectAsync(Jump(known: fingerprint), i => new SshClient(i), Token);
        Assert.Equal("jump", Run(client, "hostname"));
    }

    [Fact]
    public async Task PasswordKeyFileAndAgentLogonsWork()
    {
        string known = await KnownJumpKeyAsync();
        using var route = await SshRoute.OpenAsync([], Token);

        SshClient byPassword = await route.ConnectAsync(Jump(known: known), i => new SshClient(i), Token);
        Assert.Equal(User, Run(byPassword, "whoami"));

        SshClient byKey = await route.ConnectAsync(Jump(SshAuthMethod.KeyFile, known: known), i => new SshClient(i), Token);
        Assert.Equal(User, Run(byKey, "whoami"));

        using var agent = new SshTestAgent((new PrivateKeyFile(KeyPath!), "testbed"));
        SshClient byAgent = await route.ConnectAsync(Jump(SshAuthMethod.Agent, known: known, agents: [agent.Client]), i => new SshClient(i), Token);
        Assert.Equal(User, Run(byAgent, "whoami"));
        Assert.True(agent.SignRequests >= 1);

        await Assert.ThrowsAsync<SshAuthenticationException>(() =>
            route.ConnectAsync(Jump(secret: "wrong-" + Guid.NewGuid(), known: known), i => new SshClient(i), Token));
    }

    [Fact]
    public async Task JumpHostReachesTheInnerTargetForSshSftpAndTcp()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Target), "RM_SSH_TEST_TARGET not set.");
        string known = await KnownJumpKeyAsync();
        using var route = await SshRoute.OpenAsync([Jump(SshAuthMethod.KeyFile, known: known)], Token);
        Assert.False(route.IsDirect);

        var target = new SshLogin { ConnectionId = "t", Name = "t", Host = Target!, Port = 22, UserName = User, Secret = Password };
        string targetKey = await LearnHostKeyAsync(route, target);
        target = new SshLogin { ConnectionId = "t", Name = "t", Host = Target!, Port = 22, UserName = User, Secret = Password, KnownHostKey = targetKey };

        SshClient ssh = await route.ConnectAsync(target, i => new SshClient(i), Token);
        Assert.Equal("target", Run(ssh, "hostname"));

        SftpClient sftp = await route.ConnectAsync(target, i => new SftpClient(i), Token);
        string name = $"rm-test-{Guid.NewGuid():N}.txt";
        byte[] content = Encoding.UTF8.GetBytes("über den Jump-Host");
        sftp.UploadFile(new MemoryStream(content), name);
        Assert.Contains(sftp.ListDirectory("."), f => f.Name == name);
        using var back = new MemoryStream();
        sftp.DownloadFile(name, back);
        sftp.DeleteFile(name);
        Assert.Equal(content, back.ToArray());

        // Plain TCP through the route (what RDP over SSH uses): the target's SSH banner arrives.
        (string host, int port) = route.Reach(Target!, 22);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, Token);
        byte[] banner = new byte[64];
        int read = await tcp.GetStream().ReadAsync(banner, Token);
        Assert.StartsWith("SSH-2.0-", Encoding.ASCII.GetString(banner, 0, read), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfiguredLocalForwardIsStarted()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Target), "RM_SSH_TEST_TARGET not set.");
        string known = await KnownJumpKeyAsync();
        using var route = await SshRoute.OpenAsync([], Token);
        SshClient client = await route.ConnectAsync(Jump(known: known), i => new SshClient(i), Token);
        int localPort = FreePort();

        IReadOnlyList<string> problems = SshRoute.StartForwards(client, [new PortForward(PortForwardKind.Local, "", localPort, Target!, 22)]);

        Assert.Empty(problems);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", localPort, Token);
        byte[] banner = new byte[64];
        int read = await tcp.GetStream().ReadAsync(banner, Token);
        Assert.StartsWith("SSH-2.0-", Encoding.ASCII.GetString(banner, 0, read), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShellStreamResizesThePty()
    {
        string known = await KnownJumpKeyAsync();
        using var route = await SshRoute.OpenAsync([], Token);
        SshClient client = await route.ConnectAsync(Jump(known: known), i => new SshClient(i), Token);
        using ShellStream shell = client.CreateShellStream("xterm-256color", 80, 24, 0, 0, 4096);

        shell.ChangeWindowSize(132, 40, 0, 0);
        await Task.Delay(300, Token);
        shell.WriteLine("stty size");
        string? line = shell.Expect(new System.Text.RegularExpressions.Regex(@"\b40 132\b"), TimeSpan.FromSeconds(10));
        Assert.NotNull(line);
    }

    private static async Task<string> KnownJumpKeyAsync()
    {
        using var route = await SshRoute.OpenAsync([], Token);
        return await LearnHostKeyAsync(route, Jump());
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
