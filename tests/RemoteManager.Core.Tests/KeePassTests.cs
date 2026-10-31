using RemoteManager.Core.Credentials;
using RemoteManager.Core.Credentials.KeePass;
using RemoteManager.Core.Library;
using RemoteManager.Core.Model;
using RemoteManager.Core.Storage;

namespace RemoteManager.Core.Tests;

public sealed class KeePassTests : IDisposable
{
    private const string Master = "RemoteManager-Test";
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "KeePass");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempFolder folder = new();

    public void Dispose() => folder.Dispose();

    public static TheoryData<string, string?> Databases() => new()
    {
        { "kdbx3-aes-aeskdf.kdbx", null },
        { "kdbx4-aes-argon2d.kdbx", null },
        { "kdbx4-chacha20-argon2d.kdbx", null },
        { "kdbx4-twofish-argon2d.kdbx", null },
        { "kdbx4-aes-aeskdf.kdbx", null },
        { "kdbx4-aes-argon2id.kdbx", null },
        { "kdbx4-aes-uncompressed.kdbx", null },
        { "kdbx4-keyfile.kdbx", "fixture.keyx" },
    };

    [Theory]
    [MemberData(nameof(Databases))]
    public void ReadsEveryFormatVariant(string database, string? keyFile)
    {
        using KdbxCompositeKey key = KdbxCompositeKey.Create(Master, keyFile is null ? null : Path.Combine(Fixtures, keyFile));
        IReadOnlyList<KdbxEntry> entries = KdbxReader.ReadEntries(Path.Combine(Fixtures, database), key);

        KdbxEntry server = Assert.Single(entries, e => e.Title == "Server 01");
        Assert.Equal("admin", server.UserName);
        Assert.Equal("Pa55-äöü-€", server.Password);
        Assert.Equal(["Kunden", "Kunde A"], server.GroupPath);
        Assert.Equal("Kunden/Kunde A/Server 01", server.Path);
        // A protected custom field after protected history values: the inner stream is consumed in order.
        Assert.Equal("4711", server.Fields["PIN"]);
        Assert.Equal("router-secret", Assert.Single(entries, e => e.Title == "Router").Password);
        Assert.Equal(2, entries.Count(e => e.Title == "Doppelt"));
        Assert.DoesNotContain(entries, e => e.Title == "Gelöscht");
    }

    [Theory]
    [InlineData("kdbx3-aes-aeskdf.kdbx")]
    [InlineData("kdbx4-aes-argon2d.kdbx")]
    [InlineData("kdbx4-chacha20-argon2d.kdbx")]
    public void WrongMasterPasswordIsReportedAsSuch(string database)
    {
        using KdbxCompositeKey key = KdbxCompositeKey.Create("wrong", null);
        var ex = Assert.Throws<CredentialSourceException>(() => KdbxReader.ReadEntries(Path.Combine(Fixtures, database), key));
        Assert.Equal(CredentialError.WrongMasterKey, ex.Error);
    }

    [Fact]
    public void MissingKeyFileIsWrongKey()
    {
        using KdbxCompositeKey key = KdbxCompositeKey.Create(Master, null);
        var ex = Assert.Throws<CredentialSourceException>(() => KdbxReader.ReadEntries(Path.Combine(Fixtures, "kdbx4-keyfile.kdbx"), key));
        Assert.Equal(CredentialError.WrongMasterKey, ex.Error);
    }

    [Fact]
    public void NonKeePassFileIsUnsupported()
    {
        string path = folder.File("not.kdbx");
        File.WriteAllText(path, "hello");
        using KdbxCompositeKey key = KdbxCompositeKey.Create(Master, null);
        Assert.Equal(CredentialError.UnsupportedFormat, Assert.Throws<CredentialSourceException>(() => KdbxReader.ReadEntries(path, key)).Error);
        Assert.Equal(CredentialError.FileNotFound,
            Assert.Throws<CredentialSourceException>(() => KdbxReader.ReadEntries(folder.File("missing.kdbx"), key)).Error);
    }

    [Theory]
    [InlineData(KdbxWriteMode.AesWithAesKdf)]
    [InlineData(KdbxWriteMode.ChaCha20WithArgon2d)]
    public void WriterRoundTrips(KdbxWriteMode mode)
    {
        string path = folder.File("written.kdbx");
        using (KdbxCompositeKey key = KdbxCompositeKey.Create("pw", null))
            KdbxWriter.Write(path, key, [new("A/B", "Eintrag", "user", "geheim-ß"), new("", "Wurzel", "", "x", "00112233445566778899aabbccddeeff")], mode);

        using KdbxCompositeKey again = KdbxCompositeKey.Create("pw", null);
        IReadOnlyList<KdbxEntry> entries = KdbxReader.ReadEntries(path, again);
        Assert.Equal("geheim-ß", Assert.Single(entries, e => e.Path == "A/B/Eintrag").Password);
        Assert.Equal("00112233445566778899aabbccddeeff", Assert.Single(entries, e => e.Title == "Wurzel").Uuid);
    }

    [Fact]
    public void ReferenceResolvesByUuidPathAndUniqueTitle()
    {
        KdbxEntry a = new("aa000000000000000000000000000001", ["Kunden"], new Dictionary<string, string> { ["Title"] = "Doppelt" });
        KdbxEntry b = new("aa000000000000000000000000000002", ["Kunden", "Kunde A"], new Dictionary<string, string> { ["Title"] = "Doppelt" });
        KdbxEntry c = new("aa000000000000000000000000000003", [], new Dictionary<string, string> { ["Title"] = "Router" });
        KdbxEntry[] entries = [a, b, c];

        KdbxEntry Resolve(string entry) => new KeePassReference("x.kdbx", entry).Resolve(entries);

        Assert.Same(b, Resolve("AA000000-0000-0000-0000-000000000002"));
        Assert.Same(b, Resolve("Kunden/Kunde A/Doppelt"));
        Assert.Same(b, Resolve("Datenbank/Kunden/Kunde A/Doppelt")); // with the root group's name
        Assert.Same(c, Resolve("router"));
        Assert.Equal(CredentialError.EntryAmbiguous, Assert.Throws<CredentialSourceException>(() => Resolve("Doppelt")).Error);
        Assert.Equal(CredentialError.EntryNotFound, Assert.Throws<CredentialSourceException>(() => Resolve("Nope")).Error);
    }

    [Fact]
    public void ReferenceKeyRoundTripsAndRejectsGarbage()
    {
        var reference = new KeePassReference(@"C:\Daten\team.kdbx", "Kunden/Server", " ", "PIN");
        KeePassReference parsed = KeePassReference.Parse(reference.ToKey());
        Assert.Equal(@"C:\Daten\team.kdbx", parsed.Database);
        Assert.Null(parsed.KeyFile);
        Assert.Equal("PIN", parsed.FieldName);
        Assert.DoesNotContain("keyFile", reference.ToKey(), StringComparison.Ordinal);
        Assert.Equal(KeePassReference.PasswordField, new KeePassReference("a", "b").FieldName);

        Assert.False(KeePassReference.TryParse("not json", out _));
        Assert.False(KeePassReference.TryParse("""{"database":"a"}""", out _));
    }

    [Fact]
    public async Task SourceAsksOnceRetriesWrongPasswordsAndHoldsOnlyInMemory()
    {
        string database = Path.Combine(Fixtures, "kdbx4-aes-aeskdf.kdbx");
        var prompt = new ScriptedPrompt("wrong", Master);
        var source = new KeePassCredentialSource { Prompt = prompt };
        string key = new KeePassReference(database, "Kunden/Kunde A/Server 01").ToKey();

        Assert.Equal("Pa55-äöü-€", await source.ReadAsync(key, Ct));
        Assert.Equal([false, true], prompt.Requests.Select(r => r.PreviousAttemptFailed));
        Assert.True(source.IsUnlocked(database, null));

        // Second read: no prompt.
        Assert.Equal("root", await source.ReadAsync(new KeePassReference(database, "Router", Field: "UserName").ToKey(), Ct));
        Assert.Equal(2, prompt.Requests.Count);

        // Locked again and the prompt is canceled (the script has no answers left).
        source.LockAll();
        Assert.False(source.IsUnlocked(database, null));
        Assert.Equal(CredentialError.Canceled, (await Assert.ThrowsAsync<CredentialSourceException>(async () => await source.ReadAsync(key, Ct))).Error);
        Assert.False(await source.DeleteAsync(key, Ct));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await source.WriteAsync(key, "x", Ct));
    }

    [Fact]
    public async Task SourceWithKeyFileAcceptsAnEmptyPassword()
    {
        string database = folder.File("keyonly.kdbx");
        string keyFile = Path.Combine(Fixtures, "fixture.keyx");
        using (KdbxCompositeKey key = KdbxCompositeKey.Create(null, keyFile))
            KdbxWriter.Write(database, key, [new("", "Nur Schlüssel", "", "k")]);

        var source = new KeePassCredentialSource { Prompt = new ScriptedPrompt("") };
        Assert.Equal("k", await source.ReadAsync(new KeePassReference(database, "Nur Schlüssel", keyFile).ToKey(), Ct));
        Assert.False(await source.ExistsAsync(new KeePassReference(database, "Fehlt", keyFile).ToKey(), Ct));
    }

    [Fact]
    public async Task UseReferenceDropsTheOwnStoredPassword()
    {
        var library = new ConnectionLibrary(new SqliteConnectionStore(folder.File("a.db")));
        var windows = new MemoryCredentialSource(WindowsCredentialSource.SourceId);
        var credentials = new CredentialService([windows, new KeePassCredentialSource()], library);
        var connection = new Connection { Name = "x", Host = "h" };
        await credentials.SaveOwnSecretAsync(connection, "old", Ct);

        var reference = new CredentialRef(KeePassCredentialSource.SourceId, new KeePassReference("a.kdbx", "b").ToKey());
        await credentials.UseReferenceAsync(connection, reference, Ct);

        Assert.Equal(reference, connection.Credential);
        Assert.Empty(windows.Secrets);
        await Assert.ThrowsAsync<ArgumentException>(async () => await credentials.UseReferenceAsync(connection, new CredentialRef(windows.Id, "k"), Ct));
    }

    private sealed class ScriptedPrompt(params string[] answers) : IMasterPasswordPrompt
    {
        private readonly Queue<string> answers = new(answers);

        public List<MasterPasswordRequest> Requests { get; } = [];

        public ValueTask<string?> AskAsync(MasterPasswordRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(answers.TryDequeue(out string? answer) ? answer : null);
        }
    }
}
