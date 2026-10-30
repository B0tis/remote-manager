using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.RemoteTools;

namespace RemoteManager.Core.Tests;

public sealed class RemoteToolCommandLineTests
{
    private const string Secret = "S3cret-Pw";

    [Fact]
    public void TeamViewerWithoutOptInKeepsThePasswordOffTheCommandLine()
    {
        RemoteToolCommand command = RemoteToolCommandLine.Build(ConnectionType.TeamViewer, "tv.exe", "123 456 789", new RemoteToolSettings(), hasPassword: true);

        Assert.Equal(["-i", "123456789"], command.Arguments);
        Assert.False(command.ExposesSecret);
        Assert.False(command.SecretViaStdin);
        Assert.DoesNotContain(Secret, command.ArgumentsWith(Secret));
    }

    [Fact]
    public void TeamViewerPasswordOptInUsesAMaskedSlot()
    {
        var settings = new RemoteToolSettings { CredentialOnCommandLine = true, Mode = RemoteToolMode.FileTransfer, Quality = 3, AccessControl = 1 };
        RemoteToolCommand command = RemoteToolCommandLine.Build(ConnectionType.TeamViewer, @"C:\Program Files\TeamViewer\TeamViewer.exe", "123456789", settings, hasPassword: true);

        Assert.True(command.ExposesSecret);
        Assert.Equal(["-i", "123456789", "-p", Secret, "-m", "fileTransfer", "-q", "3", "-a", "1"], command.ArgumentsWith(Secret));
        Assert.Equal("\"C:\\Program Files\\TeamViewer\\TeamViewer.exe\" -i 123456789 -p •••••• -m fileTransfer -q 3 -a 1", command.Preview);
        Assert.DoesNotContain(Secret, command.Preview);
    }

    [Fact]
    public void TeamViewerOptInWithoutStoredPasswordAddsNoSwitch()
    {
        RemoteToolCommand command = RemoteToolCommandLine.Build(ConnectionType.TeamViewer, "tv.exe", "1", new RemoteToolSettings { CredentialOnCommandLine = true }, hasPassword: false);
        Assert.Equal(["-i", "1"], command.Arguments);
        Assert.False(command.ExposesSecret);
    }

    [Fact]
    public void TeamViewerVpnAndOutOfRangeOptions()
    {
        var settings = new RemoteToolSettings { Mode = RemoteToolMode.Vpn, Quality = 9, AccessControl = 5 };
        Assert.Equal(["-i", "1", "-m", "vpn"], RemoteToolCommandLine.Build(ConnectionType.TeamViewer, "tv.exe", "1", settings, false).Arguments);
    }

    [Fact]
    public void AnyDeskSendsThePasswordThroughStdin()
    {
        var settings = new RemoteToolSettings { Mode = RemoteToolMode.FileTransfer, FullScreen = true, Plain = true, CredentialOnCommandLine = true };
        RemoteToolCommand command = RemoteToolCommandLine.Build(ConnectionType.AnyDesk, "AnyDesk.exe", "987 654 321", settings, hasPassword: true);

        Assert.True(command.SecretViaStdin);
        Assert.False(command.ExposesSecret);
        Assert.Equal(["987654321", "--file-transfer", "--full-screen", "--plain", "--with-password"], command.ArgumentsWith(Secret));
    }

    [Fact]
    public void AnyDeskWithoutPasswordHasNoWithPassword()
    {
        RemoteToolCommand command = RemoteToolCommandLine.Build(ConnectionType.AnyDesk, "AnyDesk.exe", "kasse2@ad", new RemoteToolSettings(), hasPassword: false);
        Assert.Equal(["kasse2@ad"], command.Arguments);
        Assert.False(command.SecretViaStdin);
    }

    [Fact]
    public void AnyDeskFallsBackToControlForUnsupportedModes()
    {
        RemoteToolCommand command = RemoteToolCommandLine.Build(ConnectionType.AnyDesk, "a.exe", "1", new RemoteToolSettings { Mode = RemoteToolMode.Vpn }, false);
        Assert.Equal(["1"], command.Arguments);
    }

    [Theory]
    [InlineData(RemoteToolMode.Control, "--connect")]
    [InlineData(RemoteToolMode.FileTransfer, "--file-transfer")]
    [InlineData(RemoteToolMode.PortForward, "--port-forward")]
    public void RustDeskModes(RemoteToolMode mode, string verb)
    {
        RemoteToolCommand command = RemoteToolCommandLine.Build(ConnectionType.RustDesk, "rustdesk.exe", "123456789", new RemoteToolSettings { Mode = mode }, true);
        Assert.Equal([verb, "123456789"], command.Arguments);
        Assert.False(command.ExposesSecret);
    }

    [Fact]
    public void RustDeskPasswordOptIn()
    {
        RemoteToolCommand command = RemoteToolCommandLine.Build(ConnectionType.RustDesk, "rustdesk.exe", "1", new RemoteToolSettings { CredentialOnCommandLine = true }, true);
        Assert.Equal(["--connect", "1", "--password", Secret], command.ArgumentsWith(Secret));
        Assert.EndsWith("--password ••••••", command.Preview);
    }

    [Fact]
    public void ModeOverrideStartsFileTransfer()
    {
        RemoteToolCommand command = RemoteToolCommandLine.Build(ConnectionType.TeamViewer, "tv.exe", "1", new RemoteToolSettings(), false, RemoteToolMode.FileTransfer);
        Assert.Equal(["-i", "1", "-m", "fileTransfer"], command.Arguments);
    }

    [Fact]
    public void ExtraArgumentsAreSplitAndGetTheId()
    {
        var settings = new RemoteToolSettings { ExtraArguments = "--relay \"--note=a b\" x{id}y" };
        RemoteToolCommand command = RemoteToolCommandLine.Build(ConnectionType.RustDesk, "r.exe", "42", settings, false);

        Assert.Equal(["--connect", "42", "--relay", "--note=a b", "x42y"], command.Arguments);
        Assert.Equal("r.exe --connect 42 --relay \"--note=a b\" x42y", command.Preview);
    }

    [Theory]
    [InlineData("", new string[0])]
    [InlineData("  a   b ", new[] { "a", "b" })]
    [InlineData("\"\" x", new[] { "", "x" })]
    [InlineData("say \\\"hi\\\"", new[] { "say", "\"hi\"" })]
    public void SplitArguments(string text, string[] expected) =>
        Assert.Equal(expected, RemoteToolCommandLine.SplitArguments(text));

    [Theory]
    [InlineData("-p", "-p")]
    [InlineData("--quiet --Password=x", "--Password=x")]
    [InlineData("-b QUJD", "-b")]
    [InlineData("--passwordb64 QUJD", "--passwordb64")]
    [InlineData("--with-password", "--with-password")]
    [InlineData("--plain -q 2", null)]
    public void FindSecretArgument(string text, string? expected) =>
        Assert.Equal(expected, RemoteToolCommandLine.FindSecretArgument(RemoteToolCommandLine.SplitArguments(text)));

    [Theory]
    [InlineData(ConnectionType.TeamViewer, "123456789", true)]
    [InlineData(ConnectionType.TeamViewer, "12345abc", false)]
    [InlineData(ConnectionType.TeamViewer, "-p", false)]
    [InlineData(ConnectionType.AnyDesk, "kasse-2@firma", true)]
    [InlineData(ConnectionType.AnyDesk, "123&calc", false)]
    [InlineData(ConnectionType.AnyDesk, "--plain", false)]
    [InlineData(ConnectionType.RustDesk, "abc:21116", true)]
    [InlineData(ConnectionType.RustDesk, "a b", false)]
    [InlineData(ConnectionType.Rdp, "1", false)]
    public void IsValidId(ConnectionType type, string id, bool valid) =>
        Assert.Equal(valid, RemoteToolCommandLine.IsValidId(type, id));

    [Fact]
    public void ModesPerTool()
    {
        Assert.Contains(RemoteToolMode.Vpn, RemoteToolCommandLine.ModesOf(ConnectionType.TeamViewer));
        Assert.DoesNotContain(RemoteToolMode.Vpn, RemoteToolCommandLine.ModesOf(ConnectionType.AnyDesk));
        Assert.Contains(RemoteToolMode.PortForward, RemoteToolCommandLine.ModesOf(ConnectionType.RustDesk));
        Assert.All([ConnectionType.TeamViewer, ConnectionType.AnyDesk, ConnectionType.RustDesk],
            t => Assert.Contains(RemoteToolMode.FileTransfer, RemoteToolCommandLine.ModesOf(t)));
    }

    [Fact]
    public void SettingsRoundTripThroughJson()
    {
        var settings = new RemoteToolSettings
        {
            Alias = "Kasse", Mode = RemoteToolMode.PortForward, CredentialOnCommandLine = true, Quality = 2, AccessControl = 0,
            FullScreen = true, Plain = true, Source = RemoteToolSource.TeamViewerApi, ExternalId = "d1",
            LastSyncedAt = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc), RemovedAtSource = true, ExtraArguments = "--relay",
        };
        string json = ConnectionSettings.Serialize(settings);
        Assert.Equal(settings, ConnectionSettings.Deserialize(ConnectionType.RustDesk, json));
        Assert.Equal(-1, ((RemoteToolSettings)ConnectionSettings.Deserialize(ConnectionType.AnyDesk, "{\"alias\":\"x\"}")).AccessControl);
    }
}

public sealed class RemoteToolLocatorTests
{
    private readonly Dictionary<string, string> settings = [];

    [Fact]
    public void ConfiguredPathWins()
    {
        settings["tools.path.TeamViewer"] = @"D:\Tools\TeamViewer.exe";
        RemoteToolLocator locator = Create(@"D:\Tools\TeamViewer.exe", @"C:\PF\TeamViewer\TeamViewer.exe");

        ToolLocation location = locator.Locate(ConnectionType.TeamViewer);
        Assert.Equal(@"D:\Tools\TeamViewer.exe", location.Path);
        Assert.True(location.FromSetting);
    }

    [Fact]
    public void MissingConfiguredPathFallsBackToDetection()
    {
        settings["tools.path.AnyDesk"] = @"D:\gone\AnyDesk.exe";
        ToolLocation location = Create(@"C:\PF\AnyDesk\AnyDesk.exe").Locate(ConnectionType.AnyDesk);

        Assert.Equal(@"C:\PF\AnyDesk\AnyDesk.exe", location.Path);
        Assert.False(location.FromSetting);
        Assert.True(location.ConfiguredPathMissing);
    }

    [Fact]
    public void NothingFound()
    {
        ToolLocation location = Create().Locate(ConnectionType.RustDesk);
        Assert.False(location.Found);
        Assert.False(location.ConfiguredPathMissing);
    }

    [Fact]
    public void InfoHasDownloadPages()
    {
        Assert.All(RemoteToolLocator.Tools, t => Assert.StartsWith("https://", t.DownloadUrl, StringComparison.Ordinal));
        Assert.Throws<ArgumentOutOfRangeException>(() => RemoteToolLocator.InfoFor(ConnectionType.Rdp));
    }

    private RemoteToolLocator Create(params string[] existing) =>
        new(key => settings.GetValueOrDefault(key), existing.Contains,
            info => [$@"C:\PF\{info.Folders[0]}\{info.ExeName}"]);
}
