using RemoteManager.Core.Credentials;

namespace RemoteManager.Core.Tests;

/// <summary>A temporary folder, deleted on dispose.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Credential source in memory; the tests never touch the real Credential Manager.</summary>
internal sealed class MemoryCredentialSource(string id = "memory") : ICredentialSource
{
    public Dictionary<string, string> Secrets { get; } = [];

    public string Id { get; } = id;
    public bool IsWritable => true;
    public bool IsAvailable => true;

    public ValueTask<string?> ReadAsync(string key, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Secrets.GetValueOrDefault(key));

    public ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Secrets.ContainsKey(key));

    public ValueTask WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
    {
        Secrets[key] = secret;
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Secrets.Remove(key));
}
