using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RemoteManager.Core.Diagnostics;
using RemoteManager.Core.ImportExport;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;

namespace RemoteManager.Core.Tests;

/// <summary>Builds 1Remote and PRemoteM databases with their real schemas.</summary>
internal static class OneRemoteFixture
{
    /// <summary>
    /// 1Remote >= 1.0 (DapperDataBase): Configs, Servers (Json), Credentials. PRemoteM: Server (JsonConfigString).
    /// A null row is written as the raw text "{not json" (a damaged entry).
    /// </summary>
    public static void CreateDatabase(string path, IEnumerable<string?> rows, bool premotem = false, SqliteConnection? keepOpen = null)
    {
        SqliteConnection connection = keepOpen ?? new SqliteConnection($"Data Source={path};Pooling=False");
        try
        {
            if (keepOpen is null)
                connection.Open();
            Execute(connection, premotem
                ? """
                  CREATE TABLE `Server` (`Id` INTEGER PRIMARY KEY AUTOINCREMENT, `Protocol` TEXT NOT NULL, `ClassVersion` TEXT NOT NULL, `JsonConfigString` TEXT NOT NULL);
                  CREATE TABLE `Config` (`Id` INTEGER PRIMARY KEY AUTOINCREMENT, `Key` TEXT NOT NULL, `Value` TEXT NOT NULL);
                  """
                : """
                  CREATE TABLE IF NOT EXISTS `Configs` (`Key` VARCHAR (64) PRIMARY KEY NOT NULL UNIQUE, `Value` TEXT NOT NULL);
                  CREATE TABLE IF NOT EXISTS `Servers` (`Id` VARCHAR (64) PRIMARY KEY NOT NULL UNIQUE, `Protocol` VARCHAR (32) NOT NULL, `ClassVersion` VARCHAR (32) NOT NULL, `Json` TEXT NOT NULL);
                  CREATE TABLE IF NOT EXISTS `Credentials` (`Id` VARCHAR (64) PRIMARY KEY NOT NULL UNIQUE, `Hash` VARCHAR (32) NOT NULL, `Json` TEXT NOT NULL);
                  INSERT INTO `Configs` VALUES ('EncryptionTest', 'x'), ('DatabaseId', 'fixture');
                  """);
            int index = 0;
            foreach (string? row in rows)
            {
                using SqliteCommand insert = connection.CreateCommand();
                insert.CommandText = premotem
                    ? "INSERT INTO `Server` (`Protocol`, `ClassVersion`, `JsonConfigString`) VALUES ($protocol, '1', $json)"
                    : "INSERT INTO `Servers` VALUES ($id, $protocol, '1', $json)";
                insert.Parameters.AddWithValue("$id", $"id-{index++}");
                insert.Parameters.AddWithValue("$protocol", row is not null && JsonNode.Parse(row) is JsonObject item ? (string?)item["Protocol"] ?? "" : "RDP");
                insert.Parameters.AddWithValue("$json", row ?? "{not json");
                insert.ExecuteNonQuery();
            }
        }
        finally
        {
            if (keepOpen is null)
                connection.Dispose();
        }
    }

    public static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static string Row(string protocol, string name, string address, string port = "", string user = "", string? privateKey = null)
    {
        var row = new JsonObject
        {
            ["Id"] = Guid.NewGuid().ToString("N"),
            ["Protocol"] = protocol,
            ["ClassVersion"] = "1",
            ["DisplayName"] = name,
            ["Tags"] = new JsonArray("t1"),
            ["Address"] = address,
            ["Port"] = port,
            ["UserName"] = user,
        };
        if (privateKey is not null)
            row["PrivateKey"] = privateKey;
        return row.ToJsonString();
    }
}

/// <summary>1Remote databases as they are on disk: both schemas, damaged rows, a database in use, private keys.</summary>
public sealed class OneRemoteImportTests : IDisposable
{
    private readonly TempFolder folder = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        folder.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly string[] MixedRows =
    [
        OneRemoteFixture.Row("RDP", "rdp01", "rdp01.example", "3390", @"CORP\admin"),
        OneRemoteFixture.Row("SSH", "ssh01", "ssh01.example", "22", "root"),
        OneRemoteFixture.Row("VNC", "vnc01", "vnc01.example", "5901"),
    ];

    [Fact]
    public async Task OneRemoteSchemaSkipsDamagedRowsAndImportsTheRest()
    {
        string db = folder.File("1Remote.db");
        OneRemoteFixture.CreateDatabase(db, [MixedRows[0], null, MixedRows[1], "[1, 2]", MixedRows[2]]);

        var importer = new OneRemoteImporter();
        Assert.True(importer.CanImport(db));
        ImportResult result = await importer.ImportAsync(db, Token);

        Assert.Equal(
            [("rdp01", ConnectionType.Rdp, "rdp01.example", 3390), ("ssh01", ConnectionType.Ssh, "ssh01.example", 22), ("vnc01", ConnectionType.Vnc, "vnc01.example", 5901)],
            result.Connections.Select(c => (c.Name, c.Type, c.Host, c.EffectivePort)));
        Assert.Equal(("admin", "CORP"), (result.Connections[0].UserName, result.Connections[0].Domain));
        Assert.Contains(result.Warnings, w => w.StartsWith("Eintrag 2:", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.StartsWith("Eintrag 4:", StringComparison.Ordinal));
        Assert.Empty(result.Secrets);
    }

    [Fact]
    public async Task PRemoteMSchemaIsRead()
    {
        string db = folder.File("PRemoteM.db");
        OneRemoteFixture.CreateDatabase(db, MixedRows, premotem: true);

        var importer = new OneRemoteImporter();
        Assert.True(importer.CanImport(db));
        ImportResult result = await importer.ImportAsync(db, Token);
        Assert.Equal(["rdp01", "ssh01", "vnc01"], result.Connections.Select(c => c.Name));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task DatabaseWithoutServerTableIsReportedNotCrashed()
    {
        string db = folder.File("other.db");
        using (var connection = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            connection.Open();
            OneRemoteFixture.Execute(connection, "CREATE TABLE Something (Id INTEGER)");
        }

        var importer = new OneRemoteImporter();
        Assert.True(importer.CanImport(db));
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportAsync(db, Token));
        Assert.Contains("keine 1Remote-Datenbank", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 1Remote keeps its database open for writing while it runs (WAL, an open transaction, the file shared for read
    /// and write). Checking and importing must work then, see the committed rows in the -wal file, and leave the file alone.
    /// </summary>
    [Fact]
    public async Task DatabaseHeldOpenForWritingIsImported()
    {
        string db = folder.File("1Remote.db");
        using var writer = new SqliteConnection($"Data Source={db};Mode=ReadWriteCreate;Pooling=False");
        writer.Open();
        OneRemoteFixture.Execute(writer, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
        OneRemoteFixture.CreateDatabase(db, MixedRows, keepOpen: writer);
        Assert.True(File.Exists(db + "-wal"));
        // A write in progress, plus a handle like 1Remote's own (read/write, shared).
        using SqliteTransaction pending = writer.BeginTransaction();
        OneRemoteFixture.Execute(writer, "INSERT INTO `Configs` VALUES ('pending', 'x')");
        await using var held = new FileStream(db, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        var importer = new OneRemoteImporter();
        Assert.True(importer.CanImport(db));
        ImportResult result = await importer.ImportAsync(db, Token);

        Assert.Equal(["rdp01", "ssh01", "vnc01"], result.Connections.Select(c => c.Name));
        pending.Commit();
        // The writer was not disturbed: its two config rows plus the pending one.
        using var count = new SqliteCommand("SELECT COUNT(*) FROM Configs", writer);
        Assert.Equal(3L, (long)count.ExecuteScalar()!);
    }

    [Fact]
    public void CanImportNeverThrows()
    {
        var importer = new OneRemoteImporter();
        Assert.False(importer.CanImport(folder.File("missing.db")));
        Assert.False(importer.CanImport(folder.Path));
        Assert.False(importer.CanImport(""));
        Assert.False(importer.CanImport("bad\0path.db"));
        string empty = folder.File("empty.db");
        File.WriteAllBytes(empty, []);
        Assert.False(importer.CanImport(empty));
    }

    [Fact]
    public async Task PrivateKeyIsTakenOnlyWhenItIsAFilePath()
    {
        string db = folder.File("1Remote.db");
        OneRemoteFixture.CreateDatabase(db,
        [
            OneRemoteFixture.Row("SSH", "with-path", "a.example", privateKey: @"C:\Users\me\.ssh\id_ed25519"),
            // What 1Remote's database holds for a stored key: an encrypted value, not a path.
            OneRemoteFixture.Row("SSH", "encrypted", "b.example", privateKey: "/8AbCdEfGhIjKlMnOpQrStUvWxYz0123456789+/AbCdEf=="),
            OneRemoteFixture.Row("SFTP", "inline", "c.example", privateKey: "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----"),
        ]);

        ImportResult result = await new OneRemoteImporter().ImportAsync(db, Token);

        Connection withPath = result.Connections.Single(c => c.Name == "with-path");
        Assert.Equal((SshAuthMethod.KeyFile, @"C:\Users\me\.ssh\id_ed25519"), (withPath.SettingsAs<SshSettings>().AuthMethod, withPath.SettingsAs<SshSettings>().KeyPath));
        foreach (string name in new[] { "encrypted", "inline" })
        {
            SshSettings ssh = result.Connections.Single(c => c.Name == name).SettingsAs<SshSettings>();
            Assert.True(string.IsNullOrEmpty(ssh.KeyPath), name);
            Assert.NotEqual(SshAuthMethod.KeyFile, ssh.AuthMethod);
            Assert.Contains(result.Warnings, w => w.StartsWith($"{name}: Der private Schlüssel", StringComparison.Ordinal));
        }

        // Nothing of the key itself ends up in a warning.
        Assert.DoesNotContain(result.Warnings, w => w.Contains("AbCdEf", StringComparison.Ordinal) || w.Contains("BEGIN", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"C:\Keys\web01.ppk", true)]
    [InlineData("D:/keys/id_rsa", true)]
    [InlineData(@"\\server\share\id_rsa", true)]
    [InlineData(@"%USERPROFILE%\.ssh\id_rsa", true)]
    [InlineData(@"~\.ssh\id_rsa", true)]
    [InlineData("/8AbCdEfGh+IjKl==", false)]
    [InlineData("QUJDREVGR0hJSktMTU5PUA==", false)]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----", false)]
    [InlineData(@"C:\a|b", false)]
    [InlineData("", false)]
    public void KeyFilePathDetection(string value, bool expected) =>
        Assert.Equal(expected, OneRemoteImporter.LooksLikeKeyFilePath(value));

    [Fact]
    public void CrashLogHoldsTheExceptionAndKeepsOnlyTheNewest()
    {
        string logs = Path.Combine(folder.Path, "logs");
        Exception error;
        try
        {
            throw new InvalidOperationException("outer", new IOException("inner"));
        }
        catch (InvalidOperationException ex)
        {
            error = ex;
        }

        string? path = CrashLogFile.Write(logs, error, "test", "1.2.3", new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));
        Assert.NotNull(path);
        string text = File.ReadAllText(path);
        Assert.Contains("System.InvalidOperationException: outer", text, StringComparison.Ordinal);
        Assert.Contains("System.IO.IOException: inner", text, StringComparison.Ordinal);
        Assert.Contains("Version:   1.2.3", text, StringComparison.Ordinal);
        Assert.Contains(nameof(CrashLogHoldsTheExceptionAndKeepsOnlyTheNewest), text, StringComparison.Ordinal);

        for (int i = 1; i <= CrashLogFile.Keep + 3; i++)
            CrashLogFile.Write(logs, error, "test", "1.2.3", new DateTimeOffset(2026, 11, 3, 10, 0, i, TimeSpan.Zero));
        Assert.Equal(CrashLogFile.Keep, Directory.GetFiles(logs, "crash-*.log").Length);
        Assert.False(File.Exists(path));

        // A folder that cannot be created (a file is in the way) gives null, not a second crash.
        string blocker = folder.File("blocker");
        File.WriteAllText(blocker, "");
        Assert.Null(CrashLogFile.Write(Path.Combine(blocker, "logs"), error, "test", "1.2.3"));
    }
}
