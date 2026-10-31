using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RemoteManager.Core.Credentials;
using RemoteManager.Core.ImportExport;
using RemoteManager.Core.Library;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Storage;

namespace RemoteManager.Core.Tests;

/// <summary>
/// The importers against the sample files in Samples/: name, host, port, user, and tags must arrive
/// unchanged ("lossless"), groups and inheritance must map, passwords only reach the credential source.
/// </summary>
public sealed class ImportTests : IDisposable
{
    private readonly TempFolder folder = new();
    private readonly ConnectionLibrary library;
    private readonly MemoryCredentialSource source = new();
    private readonly CredentialService credentials;

    public ImportTests()
    {
        library = new ConnectionLibrary(new SqliteConnectionStore(folder.File("import.db")));
        credentials = new CredentialService([source], library);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        folder.Dispose();
    }

    /// <summary>(name, host, effective port, DOMAIN\user, tags, group path) of every connection.</summary>
    private sealed record Row(string Name, string Host, int Port, string User, string Tags, string Group);

    [Fact]
    public async Task OneRemoteExportIsLossless()
    {
        ImportResult result = await new OneRemoteImporter().ImportAsync(Sample("1remote.prma"), TestContext.Current.CancellationToken);
        AssertOneRemote(result);
    }

    [Fact]
    public async Task OneRemoteDatabaseIsLossless()
    {
        // The 1Remote database: table Server with one JSON document per connection.
        string db = folder.File("1Remote.db");
        using (var connection = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE Server (Id VARCHAR(64) PRIMARY KEY NOT NULL, Protocol VARCHAR(32) NOT NULL, ClassVersion VARCHAR(32) NOT NULL, Json TEXT NOT NULL)";
            create.ExecuteNonQuery();
            foreach (JsonNode? item in JsonNode.Parse(File.ReadAllText(Sample("1remote.prma")))!.AsArray())
            {
                using SqliteCommand insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO Server VALUES ($id, $protocol, '1', $json)";
                insert.Parameters.AddWithValue("$id", (string)item!["Id"]!);
                insert.Parameters.AddWithValue("$protocol", (string)item["Protocol"]!);
                insert.Parameters.AddWithValue("$json", item.ToJsonString());
                insert.ExecuteNonQuery();
            }
        }

        var importer = new OneRemoteImporter();
        Assert.True(importer.CanImport(db));
        AssertOneRemote(await importer.ImportAsync(db, TestContext.Current.CancellationToken));
    }

    private static void AssertOneRemote(ImportResult result)
    {
        Assert.Equal(
        [
            new Row("dc01", "10.0.0.10", 3389, "CORP\\admin", "prod,ad", ""),
            new Row("Terminalserver Büro", "ts.example.org", 3390, "jdoe@example.org", "büro,#rds", ""),
            new Row("web01", "web01.example.org", 2222, "root", "linux", ""),
        ], Rows(result.Connections, null));

        Connection dc = result.Connections[0];
        Assert.Equal("Domain Controller, Wartung Di", dc.Notes);
        RdpSettings rdp = dc.SettingsAs<RdpSettings>();
        Assert.True(rdp.AdminSession);
        Assert.True(rdp.UseMultimon);
        Assert.Equal(("gw.example", RdpGatewayUsage.Always), (rdp.GatewayHost, rdp.GatewayUsage));
        Assert.Equal(new RdpRedirections { Clipboard = true, Drives = true, Printers = true, Audio = RdpAudioMode.Remote }, rdp.Redirections);

        RdpSettings ts = result.Connections[1].SettingsAs<RdpSettings>();
        Assert.Equal((RdpDisplayMode.FixedResolution, 1600, 900), (ts.Display, ts.DesktopWidth, ts.DesktopHeight));
        Assert.Equal(RdpKeyboardMode.Local, ts.Keyboard);
        Assert.Equal(RdpGatewayUsage.None, ts.GatewayUsage);

        Assert.Equal(@"C:\Keys\web01.ppk", result.Connections[2].SettingsAs<SshSettings>().KeyPath);
        // The APP entry and the encrypted password are reported, not silently dropped.
        Assert.Contains(result.Warnings, w => w.Contains("APP", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("1 passwords", StringComparison.Ordinal));
        Assert.Empty(result.Secrets);
    }

    [Fact]
    public async Task RdcManFileKeepsTreeLogonsAndSettings()
    {
        string path = Prepare("sample.rdg");
        ImportSummary summary = await Import(new RdcManImporter(), path);

        Assert.Equal(
        [
            new Row("dc01", "10.0.0.10", 3389, "", "", "Rechenzentrum / Kunde A"),
            new Row("fileserver.kunde-a.example", "fileserver.kunde-a.example", 3390, "KUNDEA\\fs-admin", "", "Rechenzentrum / Kunde A"),
            new Row("print01", "print01.kunde-a.example", 3389, "", "", "Rechenzentrum / Kunde A / Standort 1"),
            new Row("app01 (Kunde B)", "app01.kunde-b.example", 3389, "", "", "Rechenzentrum / Kunde B"),
        ], Rows(library.Connections, library));
        Assert.Contains(summary.Warnings, w => w.Contains("Alle Drucker", StringComparison.Ordinal));

        // Inheritance: dc01 takes the file's logon, app01 the profile of "Kunde B".
        EffectiveCredential dc = credentials.Resolve(Find("dc01"));
        Assert.Equal(("admin", "CORP", "Rechenzentrum"), (dc.UserName, dc.Domain, dc.InheritedFrom?.Name));
        Assert.Equal("Datei-Geheim-1", await credentials.ReadAsync(dc.Secret, TestContext.Current.CancellationToken));
        EffectiveCredential app = credentials.Resolve(Find("app01 (Kunde B)"));
        Assert.Equal(("kb-admin", "KUNDEB", "Kunde B"), (app.UserName, app.Domain, app.InheritedFrom?.Name));
        Assert.Equal("Profil-Geheim-1", await credentials.ReadAsync(app.Secret, TestContext.Current.CancellationToken));
        Assert.Equal(2, summary.StoredPasswords);

        RdpSettings dcSettings = Find("dc01").SettingsAs<RdpSettings>();
        Assert.True(dcSettings.AdminSession);
        Assert.Equal(RdpKeyboardMode.FullScreenOnly, dcSettings.Keyboard);
        Assert.Equal(("gw.kunde-a.example", RdpGatewayUsage.Always), (dcSettings.GatewayHost, dcSettings.GatewayUsage));
        Assert.Equal(new RdpRedirections { Clipboard = true, Drives = true, SmartCards = true, Audio = RdpAudioMode.Remote }, dcSettings.Redirections);
        RdpSettings fs = Find("fileserver.kunde-a.example").SettingsAs<RdpSettings>();
        Assert.Equal((RdpDisplayMode.FixedResolution, 1280, 720, 0), (fs.Display, fs.DesktopWidth, fs.DesktopHeight, fs.AuthenticationLevel));
        Assert.True(library.Groups.Single(g => g.Name == "Kunde A").IsCollapsed);
    }

    [Fact]
    public async Task RdcManSchemaOneIsRead()
    {
        ImportSummary summary = await Import(new RdcManImporter(), Sample("sample-v1.rdg"));

        Assert.Equal([new Row("Labor RDP", "lab-rdp01", 3391, "LAB\\tester", "", "Alt / Labor")], Rows(library.Connections, library));
        Assert.Equal("Testserver", library.Connections[0].Description);
        Assert.Equal("Klartext-1", await credentials.ReadAsync(library.Connections[0].Credential, TestContext.Current.CancellationToken));
        Assert.Equal(1, summary.StoredPasswords);
    }

    [Fact]
    public async Task MRemoteNgFileKeepsTreeLogonsAndSettings()
    {
        ImportSummary summary = await Import(new MRemoteNgImporter(), Prepare("confCons.xml"));

        Assert.Equal(
        [
            new Row("vnc-kiosk", "192.168.1.50", 5901, "", "", ""),
            new Row("dc01", "10.0.0.10", 3389, "", "", "Kunde A"),
            new Row("sql01", "sql01.kunde-a.example", 3390, "KUNDEA\\sqladmin", "", "Kunde A"),
            new Row("web01", "web01.kunde-a.example", 2222, "root", "", "Kunde A / Linux"),
        ], Rows(library.Connections, library));
        Assert.Contains(summary.Warnings, w => w.Contains("Telnet", StringComparison.Ordinal));

        // The folder logon (and its password) is inherited by dc01; sql01 has its own.
        EffectiveCredential dc = credentials.Resolve(Find("dc01"));
        Assert.Equal(("admin", "CORP", "Kunde A"), (dc.UserName, dc.Domain, dc.InheritedFrom?.Name));
        Assert.Equal("Gruppe-Geheim-1", await credentials.ReadAsync(dc.Secret, TestContext.Current.CancellationToken));
        Assert.Equal("Sql-Geheim-1", await credentials.ReadAsync(Find("sql01").Credential, TestContext.Current.CancellationToken));

        RdpSettings dcSettings = Find("dc01").SettingsAs<RdpSettings>();
        Assert.True(dcSettings.AdminSession && dcSettings.RestrictedAdmin && dcSettings.Redirections.Drives);
        Assert.Equal(RdpAudioMode.None, dcSettings.Redirections.Audio);
        Assert.Equal(("gw.kunde-a.example", RdpGatewayUsage.Always), (dcSettings.GatewayHost, dcSettings.GatewayUsage));
        RdpSettings sql = Find("sql01").SettingsAs<RdpSettings>();
        Assert.Equal((RdpDisplayMode.FixedResolution, 1280, 720, 0), (sql.Display, sql.DesktopWidth, sql.DesktopHeight, sql.AuthenticationLevel));
        Assert.Equal(ConnectionType.Vnc, Find("vnc-kiosk").Type);
    }

    [Fact]
    public async Task MRemoteNgCustomMasterPasswordKeepsConnectionsAndReportsPasswords()
    {
        string path = Prepare("confCons.xml", password: "eigenes-Master-Passwort");
        ImportSummary summary = await Import(new MRemoteNgImporter(), path);

        Assert.Equal(4, summary.AddedConnections);
        Assert.Equal(0, summary.StoredPasswords);
        Assert.Empty(source.Secrets);
        Assert.Contains(summary.Warnings, w => w.Contains("custom mRemoteNG master password", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MRemoteNgFullyEncryptedFileIsRead()
    {
        // "Full file encryption": the root element holds the encrypted nodes as its text.
        string plain = Prepare("confCons.xml");
        var document = System.Xml.Linq.XDocument.Load(plain);
        System.Xml.Linq.XElement root = document.Root!;
        string inner = string.Concat(root.Elements().Select(e => e.ToString(System.Xml.Linq.SaveOptions.DisableFormatting)));
        root.RemoveNodes();
        root.SetAttributeValue("FullFileEncryption", "true");
        root.Value = MRemoteNgCrypto.EncryptGcm(inner, MRemoteNgCrypto.DefaultPassword, 1000);
        string path = folder.File("encrypted-confCons.xml");
        document.Save(path);

        ImportSummary summary = await Import(new MRemoteNgImporter(), path);

        Assert.Equal(4, summary.AddedConnections);
        Assert.Equal("Sql-Geheim-1", await credentials.ReadAsync(Find("sql01").Credential, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void GcmMatchesTheFrameworkForStandardNonces()
    {
        // The hand-written GCM (needed for mRemoteNG's 16-byte nonces) against AesGcm with a 12-byte nonce.
        byte[] key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(), nonce = new byte[12], aad = Encoding.UTF8.GetBytes("salt-as-aad");
        byte[] plain = Encoding.UTF8.GetBytes("Ein Passwort mit Ümlauten und mehr als einem Block!");
        byte[] cipher = new byte[plain.Length], tag = new byte[16];
        using (var gcm = new System.Security.Cryptography.AesGcm(key, 16))
            gcm.Encrypt(nonce, plain, cipher, tag, aad);

        Assert.Equal(cipher, MRemoteNgCrypto.Gcm.Crypt(key, nonce, plain));
        Assert.Equal(tag, MRemoteNgCrypto.Gcm.Tag(key, nonce, aad, cipher));
        string encrypted = MRemoteNgCrypto.EncryptGcm("geheim", "mR3m", 1000);
        Assert.Equal("geheim", MRemoteNgCrypto.Decrypt(encrypted, "mR3m", gcm: true, 1000));
        Assert.Null(MRemoteNgCrypto.Decrypt(encrypted, "falsch", gcm: true, 1000));
    }

    [Fact]
    public async Task RdpFileIsLosslessAndIgnoresTheEncryptedPassword()
    {
        // mstsc writes UTF-16; read the sample in that encoding.
        string path = folder.File("Terminalserver 1.rdp");
        File.WriteAllText(path, File.ReadAllText(Sample("sample.rdp")), Encoding.Unicode);
        ImportSummary summary = await Import(new RdpFileImporter(), path);

        Assert.Equal([new Row("Terminalserver 1", "ts01.example.org", 3392, "EXAMPLE\\jdoe", "", "")], Rows(library.Connections, library));
        RdpSettings s = library.Connections[0].SettingsAs<RdpSettings>();
        Assert.Equal((RdpDisplayMode.FixedResolution, 1920, 1080, true), (s.Display, s.DesktopWidth, s.DesktopHeight, s.UseMultimon));
        Assert.Equal((false, 0, true, RdpKeyboardMode.Local), (s.EnableNla, s.AuthenticationLevel, s.AdminSession, s.Keyboard));
        Assert.Equal(("rdgw.example.org", RdpGatewayUsage.Detect, false), (s.GatewayHost, s.GatewayUsage, s.GatewayUsesConnectionCredentials));
        Assert.Equal(new RdpRedirections { Clipboard = false, Drives = true, Printers = true, SmartCards = true, Audio = RdpAudioMode.None }, s.Redirections);
        Assert.Equal((@"C:\Tools\start.cmd", @"C:\Tools"), (s.StartProgram, s.WorkingDirectory));
        Assert.Empty(source.Secrets);
        Assert.Contains(summary.Warnings, w => w.Contains("encrypted password", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportingTheSameFileTwiceAddsNothing()
    {
        string path = Prepare("sample.rdg");
        await Import(new RdcManImporter(), path);
        int groups = library.Groups.Count;

        ImportSummary again = await Import(new RdcManImporter(), path);

        Assert.Equal((0, 4, 0), (again.AddedConnections, again.SkippedDuplicates, again.AddedGroups));
        Assert.Equal(4, library.Connections.Count);
        Assert.Equal(groups, library.Groups.Count);
    }

    [Fact]
    public async Task ImportGoesUnderTheTargetGroup()
    {
        await new ImportService(library, credentials).ApplyAsync(await new RdcManImporter().ImportAsync(Sample("sample-v1.rdg"), TestContext.Current.CancellationToken), "Import / RDCMan", TestContext.Current.CancellationToken);
        Assert.Equal("Import / RDCMan / Alt / Labor", library.PathOf(library.Connections[0].GroupId));
    }

    [Fact]
    public async Task JsonBackupRestoresEverythingWithoutSecrets()
    {
        await Import(new RdcManImporter(), Prepare("sample.rdg"));
        await Import(new OneRemoteImporter(), Sample("1remote.prma"));
        Connection favorite = library.Connections.First(c => c.Name == "dc01");
        favorite.IsFavorite = true;
        favorite.LastConnectedAt = new DateTime(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc);
        library.Update(favorite);

        string backup = folder.File("backup.json");
        await using (FileStream stream = File.Create(backup))
            await new JsonBackupExporter().ExportAsync(new ExportData(library.Groups, library.Connections), stream, TestContext.Current.CancellationToken);
        string text = await File.ReadAllTextAsync(backup, TestContext.Current.CancellationToken);
        Assert.All(source.Secrets.Values, secret => Assert.DoesNotContain(secret, text, StringComparison.Ordinal));

        // Restore into an empty library.
        var restored = new ConnectionLibrary(new SqliteConnectionStore(folder.File("restored.db")));
        var importer = new JsonBackupImporter();
        Assert.True(importer.CanImport(backup));
        Assert.False(new OneRemoteImporter().CanImport(backup));
        ImportSummary summary = await new ImportService(restored, new CredentialService([source], restored)).ApplyAsync(await importer.ImportAsync(backup, TestContext.Current.CancellationToken), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(library.Connections.Count, summary.AddedConnections);
        Assert.Equal(Rows(library.Connections, library), Rows(restored.Connections, restored));
        foreach (Connection original in library.Connections)
        {
            Connection copy = restored.FindConnection(original.Id)!;
            Assert.Equal((original.Description, original.Notes, original.IsFavorite, original.LastConnectedAt?.ToUniversalTime(), original.Credential, ConnectionSettings.Serialize(original.Settings)),
                (copy.Description, copy.Notes, copy.IsFavorite, copy.LastConnectedAt?.ToUniversalTime(), copy.Credential, ConnectionSettings.Serialize(copy.Settings)));
        }

        Assert.Equal(library.Groups.Select(g => (g.Id, g.Name, g.ParentId, g.UserName, g.Credential)).OrderBy(g => g.Id),
            restored.Groups.Select(g => (g.Id, g.Name, g.ParentId, g.UserName, g.Credential)).OrderBy(g => g.Id));

        // Restoring again replaces by id instead of duplicating, and keeps the objects the UI holds.
        Connection held = restored.Connections[0];
        ConnectionGroup heldGroup = restored.Groups[0];
        ImportSummary second = await new ImportService(restored, new CredentialService([source], restored)).ApplyAsync(await importer.ImportAsync(backup, TestContext.Current.CancellationToken), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal((0, library.Connections.Count), (second.AddedConnections, second.ReplacedConnections));
        Assert.Equal(library.Connections.Count, restored.Connections.Count);
        Assert.Same(held, restored.FindConnection(held.Id));
        Assert.Same(heldGroup, restored.FindGroup(heldGroup.Id));
    }

    private async Task<ImportSummary> Import(IImporter importer, string path)
    {
        Assert.True(importer.CanImport(path), $"{importer.Id} should accept {Path.GetFileName(path)}");
        ImportResult result = await importer.ImportAsync(path, TestContext.Current.CancellationToken);
        return await new ImportService(library, credentials).ApplyAsync(result, cancellationToken: TestContext.Current.CancellationToken);
    }

    private Connection Find(string name) => library.Connections.Single(c => c.Name == name);

    private static List<Row> Rows(IEnumerable<Connection> connections, ConnectionLibrary? library) =>
        [.. connections.OrderBy(c => library?.PathOf(c.GroupId), StringComparer.Ordinal).ThenBy(c => c.SortOrder).ThenBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => new Row(c.Name, c.Host, c.EffectivePort,
                c.Domain.Length > 0 ? $"{c.Domain}\\{c.UserName}" : c.UserName, string.Join(',', c.Tags), library?.PathOf(c.GroupId) ?? ""))];

    private static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "Samples", name);

    /// <summary>A copy of the sample with its placeholders replaced by encrypted values (DPAPI, mRemoteNG).</summary>
    private string Prepare(string name, string password = MRemoteNgCrypto.DefaultPassword)
    {
        string text = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(Sample(name)), @"\{\{(DPAPI|MRNG):([^}]+)\}\}", m =>
            m.Groups[1].Value == "DPAPI" ? Dpapi.ProtectToBase64(m.Groups[2].Value) : MRemoteNgCrypto.EncryptGcm(m.Groups[2].Value, password, 1000));
        string path = folder.File(name);
        File.WriteAllText(path, text);
        return path;
    }
}
