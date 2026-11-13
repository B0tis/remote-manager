using RemoteManager.Core.Model;
using RemoteManager.Core.Search;

namespace RemoteManager.Core.Tests;

/// <summary>What the quick launcher reads as a target to connect to directly, and the bar for its action search.</summary>
public sealed class AdHocTargetTests
{
    [Theory]
    [InlineData("10.0.0.5", "10.0.0.5", 0, AdHocHostKind.IPv4)]
    [InlineData("  192.168.1.10  ", "192.168.1.10", 0, AdHocHostKind.IPv4)]
    [InlineData("10.0.0.5:3390", "10.0.0.5", 3390, AdHocHostKind.IPv4)]
    [InlineData("server.example.com", "server.example.com", 0, AdHocHostKind.HostName)]
    [InlineData("server.example.com.", "server.example.com", 0, AdHocHostKind.HostName)]
    [InlineData("srv-01.corp.local:8022", "srv-01.corp.local", 8022, AdHocHostKind.HostName)]
    [InlineData("münchen-dc.firma.example", "münchen-dc.firma.example", 0, AdHocHostKind.HostName)]
    [InlineData("fe80::1", "fe80::1", 0, AdHocHostKind.IPv6)]
    [InlineData("::1", "::1", 0, AdHocHostKind.IPv6)]
    [InlineData("[2001:db8::10]", "2001:db8::10", 0, AdHocHostKind.IPv6)]
    [InlineData("[2001:db8::10]:5901", "2001:db8::10", 5901, AdHocHostKind.IPv6)]
    public void ParsesAddressesAndNames(string query, string host, int port, AdHocHostKind kind)
    {
        AdHocTarget? target = AdHocTarget.Parse(query);

        Assert.NotNull(target);
        Assert.Equal((host, port, kind), (target.Host, target.Port, target.Kind));
        Assert.True(target.IsExplicit);
        Assert.Null(target.Type);
        Assert.Equal("", target.UserName);
    }

    [Theory]
    [InlineData("root@10.0.0.5", "root", "", "10.0.0.5", 0)]
    [InlineData("root@box:2222", "root", "", "box", 2222)]
    [InlineData("admin@corp.example@dc01", "admin@corp.example", "", "dc01", 0)]
    [InlineData(@"CORP\admin@dc01.corp.example", "admin", "CORP", "dc01.corp.example", 0)]
    [InlineData("pi@[fe80::1]:22", "pi", "", "fe80::1", 22)]
    public void ParsesUserAndDomain(string query, string user, string domain, string host, int port)
    {
        AdHocTarget? target = AdHocTarget.Parse(query);

        Assert.NotNull(target);
        Assert.Equal((user, domain, host, port), (target.UserName, target.Domain, target.Host, target.Port));
        Assert.True(target.IsExplicit);
    }

    [Theory]
    [InlineData("rdp dc01", ConnectionType.Rdp, "dc01", "", 0)]
    [InlineData("SSH root@10.0.0.5:2222", ConnectionType.Ssh, "10.0.0.5", "root", 2222)]
    [InlineData("vnc   kiosk.example", ConnectionType.Vnc, "kiosk.example", "", 0)]
    [InlineData("sftp backup@nas", ConnectionType.Sftp, "nas", "backup", 0)]
    [InlineData("ssh://git@code.example:22/", ConnectionType.Ssh, "code.example", "git", 22)]
    [InlineData("rdp://10.0.0.5", ConnectionType.Rdp, "10.0.0.5", "", 0)]
    public void ParsesTypePrefix(string query, ConnectionType type, string host, string user, int port)
    {
        AdHocTarget? target = AdHocTarget.Parse(query);

        Assert.NotNull(target);
        Assert.Equal((type, host, user, port), (target.Type!.Value, target.Host, target.UserName, target.Port));
        Assert.True(target.IsExplicit);
        Assert.Equal([type], target.SuggestedTypes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("dc01 kunde")] // two words are a search
    [InlineData("selftest kasse")]
    [InlineData("123")] // digits only: no host
    [InlineData("123 456 789")] // a TeamViewer ID
    [InlineData("10.0.0")] // an address that is still being typed
    [InlineData("10.0.0.256")]
    [InlineData("1.2.3.4.5")]
    [InlineData("server.12")]
    [InlineData("-server")]
    [InlineData("server-.example")]
    [InlineData("a..b")]
    [InlineData("host:0")]
    [InlineData("host:65536")]
    [InlineData("host:ssh")]
    [InlineData("12:30")] // a time, not host 12 on port 30
    [InlineData("@host")]
    [InlineData("user@")]
    [InlineData("user:geheim@host")] // never a password in the search field
    [InlineData(@"\user@host")]
    [InlineData("[10.0.0.5]:22")]
    [InlineData("[::1")]
    [InlineData("gggg::1")]
    [InlineData("ssh")]
    [InlineData("ssh ")]
    [InlineData("rdp a b")]
    [InlineData("#prod")]
    [InlineData(">liste")]
    [InlineData("https://example.com")]
    public void RejectsWhatIsNotATarget(string query)
    {
        AdHocTarget? target = AdHocTarget.Parse(query);

        // "ssh" alone is a valid host name, but only as a guess (see SingleWordIsOnlyAGuess).
        Assert.True(target is null || (!target.IsExplicit && target.Host == query.Trim()), $"\"{query}\" parsed as {target}");
        if (query.Trim() is not "ssh")
            Assert.Null(target);
    }

    [Theory]
    [InlineData("dc01")]
    [InlineData("kasse")]
    [InlineData("Büro-PC")]
    [InlineData("web_01")]
    public void SingleWordIsOnlyAGuess(string query)
    {
        AdHocTarget? target = AdHocTarget.Parse(query);

        Assert.NotNull(target);
        Assert.Equal(query, target.Host);
        Assert.Equal(AdHocHostKind.HostName, target.Kind);
        Assert.False(target.IsExplicit);
    }

    [Theory]
    [InlineData("dc01:")] // the port is still being typed
    [InlineData("rdp dc01")]
    [InlineData("admin@dc01")]
    [InlineData("dc01.corp")]
    public void QualifiedSingleNameIsExplicit(string query) => Assert.True(AdHocTarget.Parse(query)!.IsExplicit);

    [Theory]
    [InlineData("10.0.0.5", new[] { ConnectionType.Rdp, ConnectionType.Ssh, ConnectionType.Vnc })]
    [InlineData("10.0.0.5:3389", new[] { ConnectionType.Rdp, ConnectionType.Ssh, ConnectionType.Vnc })]
    [InlineData("10.0.0.5:22", new[] { ConnectionType.Ssh, ConnectionType.Rdp, ConnectionType.Vnc })]
    [InlineData("10.0.0.5:2222", new[] { ConnectionType.Ssh, ConnectionType.Rdp, ConnectionType.Vnc })]
    [InlineData("10.0.0.5:5901", new[] { ConnectionType.Vnc, ConnectionType.Rdp, ConnectionType.Ssh })]
    [InlineData("root@10.0.0.5", new[] { ConnectionType.Ssh, ConnectionType.Rdp, ConnectionType.Vnc })]
    [InlineData(@"CORP\admin@dc01", new[] { ConnectionType.Rdp, ConnectionType.Ssh, ConnectionType.Vnc })]
    [InlineData("admin@corp.example@dc01", new[] { ConnectionType.Rdp, ConnectionType.Ssh, ConnectionType.Vnc })]
    [InlineData("root@10.0.0.5:3389", new[] { ConnectionType.Rdp, ConnectionType.Ssh, ConnectionType.Vnc })]
    [InlineData("sftp nas", new[] { ConnectionType.Sftp })]
    public void SuggestsTheLikelyTypeFirst(string query, ConnectionType[] expected) =>
        Assert.Equal(expected, AdHocTarget.Parse(query)!.SuggestedTypes);

    [Theory]
    [InlineData("10.0.0.5", "10.0.0.5", "10.0.0.5")]
    [InlineData("root@box:2222", "box:2222", "root@box:2222")]
    [InlineData(@"CORP\admin@dc01", "dc01", @"CORP\admin@dc01")]
    [InlineData("pi@[fe80::1]:22", "[fe80::1]:22", "pi@[fe80::1]:22")]
    [InlineData("[fe80::1]", "fe80::1", "fe80::1")]
    public void FormatsAddressAndLabel(string query, string address, string label)
    {
        AdHocTarget target = AdHocTarget.Parse(query)!;

        Assert.Equal((address, label), (target.Address, target.Label));
    }

    [Fact]
    public void ToConnectionCarriesTheTargetAndKeepsDefaultPortsAtZero()
    {
        Connection ssh = AdHocTarget.Parse(@"root@box:2222")!.ToConnection(ConnectionType.Ssh);
        Connection rdp = AdHocTarget.Parse(@"CORP\admin@10.0.0.5:3389")!.ToConnection(ConnectionType.Rdp);
        Connection again = AdHocTarget.Parse("10.0.0.5")!.ToConnection(ConnectionType.Rdp);

        Assert.Equal((ConnectionType.Ssh, "box", 2222, "root", "box:2222"), (ssh.Type, ssh.Host, ssh.Port, ssh.UserName, ssh.Name));
        Assert.Equal((ConnectionType.Rdp, "10.0.0.5", 0, "admin", "CORP"), (rdp.Type, rdp.Host, rdp.Port, rdp.UserName, rdp.Domain));
        Assert.Equal(3389, rdp.EffectivePort);
        Assert.Null(ssh.Credential);
        Assert.Null(ssh.GroupId);
        Assert.NotEqual(rdp.Id, again.Id);
    }

    [Fact]
    public void RankWithAMinimumScoreDropsScatteredLetters()
    {
        string[] actions = ["Neue Verbindung", "Synchronisierung …", "Design wie Windows", "Online-Status aktualisieren"];
        static IEnumerable<SearchField> Fields(string label) => [new(label, 4)];

        // Without a bar, "nrg" is found as n, r, g somewhere in two labels; with it, only real hits stay.
        Assert.Equal(2, FuzzySearch.Rank("nrg", actions, Fields).Count);
        Assert.Empty(FuzzySearch.Rank("nrg", actions, Fields, minimumScorePerLetter: 25));
        Assert.Equal(["Neue Verbindung"], FuzzySearch.Rank("nv", actions, Fields, minimumScorePerLetter: 25));
        Assert.Equal(["Synchronisierung …"], FuzzySearch.Rank("sync", actions, Fields, minimumScorePerLetter: 25));
        Assert.Equal(["Design wie Windows"], FuzzySearch.Rank("windows design", actions, Fields, minimumScorePerLetter: 25));
    }

    [Fact]
    public void ALowMinimumScoreKeepsFuzzyNamesAndDropsLettersSpreadOverADescription()
    {
        (string Name, string Description)[] connections = [("dc01", "Domain Controller"), ("Selftest Gamma", ""), ("Kasse 2", "Laden, unbeaufsichtigter Zugriff")];
        static IEnumerable<SearchField> Fields((string Name, string Description) c) => [new(c.Name, 4), new(c.Description, 1)];

        // "neue" is n, e, u, e somewhere in the description of "Kasse 2": a hit without a bar, none with the launcher's.
        Assert.Equal(["Kasse 2"], FuzzySearch.Rank("neue", connections, Fields).Select(c => c.Name));
        Assert.Empty(FuzzySearch.Rank("neue", connections, Fields, minimumScorePerLetter: 10));
        Assert.Equal(["dc01"], FuzzySearch.Rank("dc1", connections, Fields, minimumScorePerLetter: 10).Select(c => c.Name));
        Assert.Equal(["Selftest Gamma"], FuzzySearch.Rank("stgam", connections, Fields, minimumScorePerLetter: 10).Select(c => c.Name));
        Assert.Equal(["Kasse 2"], FuzzySearch.Rank("zugriff", connections, Fields, minimumScorePerLetter: 10).Select(c => c.Name));
    }
}
