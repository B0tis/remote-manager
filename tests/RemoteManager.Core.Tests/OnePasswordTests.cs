using RemoteManager.Core.Credentials;

namespace RemoteManager.Core.Tests;

/// <summary>Runs a fake op (a batch file) so the process plumbing is tested without a 1Password account.</summary>
public sealed class OnePasswordTests : IDisposable
{
    private const string FakeOp = """
        @echo off
        if not "%~1"=="read" exit /b 9
        if not "%~2"=="--no-newline" exit /b 9
        if "%~3"=="op://Tresor/Fehlt/password" goto missing
        if "%~3"=="op://Tresor/Abgemeldet/password" goto signedout
        <nul set /p "=geheim:%~3"
        exit /b 0
        :missing
        >&2 echo [ERROR] 2026/10/30 12:00:00 could not read secret 'op://Tresor/Fehlt/password': "Fehlt" isn't an item in the "Tresor" vault.
        exit /b 1
        :signedout
        >&2 echo [ERROR] 2026/10/30 12:00:00 You are not currently signed in. Please run op signin --help for instructions
        exit /b 1
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempFolder folder = new();
    private readonly OnePasswordCredentialSource source;

    public OnePasswordTests()
    {
        string op = folder.File("op.cmd");
        File.WriteAllText(op, FakeOp.ReplaceLineEndings("\r\n"));
        source = new OnePasswordCredentialSource { ExecutablePath = op };
    }

    public void Dispose() => folder.Dispose();

    [Theory]
    [InlineData("op://Tresor/Server 01/password", true)]
    [InlineData("op://Tresor/Server/Abschnitt/feld", true)]
    [InlineData("op://Tresor/Server/password?attribute=otp", true)]
    [InlineData("op://Tresor/Server", false)]
    [InlineData("https://Tresor/Server/password", false)]
    [InlineData("op:///Server/password", false)]
    public void ValidatesSecretReferences(string reference, bool valid) =>
        Assert.Equal(valid, OnePasswordCredentialSource.IsValidReference(reference));

    [Fact]
    public async Task ReadsTheSecretFromStdoutWithoutNewline()
    {
        Assert.Equal("geheim:op://Tresor/Server 01/password", await source.ReadAsync(" op://Tresor/Server 01/password ", Ct));
        Assert.True(await source.ExistsAsync("op://Tresor/Server 01/password", Ct));
    }

    [Fact]
    public async Task MapsCliErrors()
    {
        var missing = await Assert.ThrowsAsync<CredentialSourceException>(async () => await source.ReadAsync("op://Tresor/Fehlt/password", Ct));
        Assert.Equal(CredentialError.EntryNotFound, missing.Error);
        Assert.StartsWith("could not read secret", missing.Detail, StringComparison.Ordinal);
        Assert.False(await source.ExistsAsync("op://Tresor/Fehlt/password", Ct));

        var signedOut = await Assert.ThrowsAsync<CredentialSourceException>(async () => await source.ReadAsync("op://Tresor/Abgemeldet/password", Ct));
        Assert.Equal(CredentialError.NotSignedIn, signedOut.Error);

        var invalid = await Assert.ThrowsAsync<CredentialSourceException>(async () => await source.ReadAsync("Tresor/Server", Ct));
        Assert.Equal(CredentialError.InvalidReference, invalid.Error);
    }

    [Fact]
    public async Task MissingCliIsReported()
    {
        var none = new OnePasswordCredentialSource { ExecutablePath = folder.File("nope.exe") };
        Assert.Null(none.FindExecutable());
        var ex = await Assert.ThrowsAsync<CredentialSourceException>(async () => await none.ReadAsync("op://a/b/c", Ct));
        Assert.Equal(CredentialError.ToolMissing, ex.Error);
    }

    [Theory]
    [InlineData("[ERROR] 2026/10/30 12:00:00 authorization prompt dismissed, please try again", CredentialError.Canceled)]
    [InlineData("[ERROR] 2026/10/30 12:00:00 More than one item matches \"Server\".", CredentialError.EntryAmbiguous)]
    [InlineData("[ERROR] 2026/10/30 12:00:00 something odd", CredentialError.ToolFailed)]
    public void ClassifiesErrorText(string stderr, CredentialError expected)
    {
        CredentialSourceException ex = OnePasswordCredentialSource.Classify(stderr);
        Assert.Equal(expected, ex.Error);
        Assert.DoesNotContain("[ERROR]", ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsReadOnly()
    {
        Assert.False(source.IsWritable);
        Assert.False(await source.DeleteAsync("op://a/b/c", Ct));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await source.WriteAsync("op://a/b/c", "x", Ct));
    }
}
