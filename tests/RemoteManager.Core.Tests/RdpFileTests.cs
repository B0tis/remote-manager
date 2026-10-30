using System.Text;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Rdp;

namespace RemoteManager.Core.Tests;

/// <summary>The .rdp file written for the external start (mstsc) and its parser.</summary>
public sealed class RdpFileTests
{
    private static readonly RdpSettings AllOptions = new()
    {
        Display = RdpDisplayMode.FixedResolution,
        DesktopWidth = 1600,
        DesktopHeight = 900,
        UseMultimon = true,
        Keyboard = RdpKeyboardMode.FullScreenOnly,
        EnableNla = false,
        AuthenticationLevel = 1,
        AdminSession = true,
        GatewayHost = "gw.example",
        GatewayUsage = RdpGatewayUsage.Detect,
        GatewayUsesConnectionCredentials = false,
        Redirections = new RdpRedirections { Clipboard = false, Drives = true, Printers = true, SmartCards = true, Ports = true, Microphone = true, Audio = RdpAudioMode.Remote },
        StartProgram = @"C:\Tools\start.cmd",
        WorkingDirectory = @"C:\Tools",
    };

    [Fact]
    public void WrittenFileCarriesTheOptionsAndReadsBackTheSame()
    {
        var connection = new Connection { Name = "ts", Host = "ts.example", Port = 3390, Settings = AllOptions };

        RdpFile file = RdpFileMapping.ToRdpFile(connection, "jdoe", "EXAMPLE");
        Connection back = RdpFileMapping.ToConnection(RdpFile.Parse(file.ToString()), "ts");

        Assert.Equal("ts.example:3390", file.GetString("full address"));
        Assert.Equal(@"EXAMPLE\jdoe", file.GetString("username"));
        Assert.Equal(("ts.example", 3390, "jdoe", "EXAMPLE"), (back.Host, back.Port, back.UserName, back.Domain));
        Assert.Equal(AllOptions, back.SettingsAs<RdpSettings>());
    }

    [Fact]
    public void WrittenFileNeverContainsAPassword()
    {
        RdpFile file = RdpFileMapping.ToRdpFile(new Connection { Host = "h", UserName = "u" }, "u", "");
        string text = file.ToString();

        Assert.DoesNotContain(file.Keys, k => k.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("prompt for credentials:i:0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteAppAndSwitches()
    {
        var settings = new RdpSettings { RemoteAppProgram = "||excel", RestrictedAdmin = true, RemoteCredentialGuard = true, AdminSession = true };
        RdpFile file = RdpFileMapping.ToRdpFile(new Connection { Name = "Excel", Host = "rds", Settings = settings }, "", "");

        Assert.Equal((1, "||excel", "Excel"), (file.GetInt("remoteapplicationmode"), file.GetString("remoteapplicationprogram"), file.GetString("remoteapplicationname")));
        Assert.Null(file.GetString("username"));
        // Restricted Admin wins over Remote Credential Guard (mstsc accepts only one).
        Assert.Equal(["/admin", "/restrictedAdmin"], RdpFileMapping.MstscSwitches(settings));
        Assert.Equal(["/remoteGuard", "/multimon"], RdpFileMapping.MstscSwitches(new RdpSettings { RemoteCredentialGuard = true, UseMultimon = true }));
    }

    [Theory]
    [InlineData("host", "host", 0)]
    [InlineData("host:3390", "host", 3390)]
    [InlineData("10.0.0.1:3391", "10.0.0.1", 3391)]
    [InlineData("[fe80::1]:3392", "fe80::1", 3392)]
    [InlineData("[fe80::1]", "fe80::1", 0)]
    [InlineData("fe80::1", "fe80::1", 0)]
    [InlineData("host:abc", "host:abc", 0)]
    public void AddressParsing(string address, string host, int port) =>
        Assert.Equal((host, port), RdpFileMapping.ParseAddress(address));

    [Fact]
    public void IPv6IsBracketedAndTheDefaultPortLeftOut()
    {
        Assert.Equal("[fe80::1]:3390", RdpFileMapping.FormatAddress("fe80::1", 3390));
        Assert.Equal("host", RdpFileMapping.FormatAddress("host", 3389));
    }

    [Fact]
    public void ParserKeepsColonsInValuesAndIgnoresJunk()
    {
        RdpFile file = RdpFile.Parse("\uFEFFfull address:s:host:3390\r\nnot a line\r\nalternate shell:s:C:\\a b\\c.exe\r\nbroken:x:1\r\n");
        Assert.Equal("host:3390", file.GetString("FULL ADDRESS"));
        Assert.Equal(@"C:\a b\c.exe", file.GetString("alternate shell"));
        Assert.False(file.Contains("broken"));
    }

    [Fact]
    public void SaveWritesUtf16LikeMstsc()
    {
        string path = Path.Combine(Path.GetTempPath(), $"rm-{Guid.NewGuid():N}.rdp");
        try
        {
            new RdpFile().Set("full address", "h").Save(path);
            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(Encoding.Unicode.GetPreamble(), bytes[..2]);
            Assert.Equal("h", RdpFile.Load(path).GetString("full address"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
