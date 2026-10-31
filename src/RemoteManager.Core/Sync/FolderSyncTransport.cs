using System.Security.Cryptography;

namespace RemoteManager.Core.Sync;

/// <summary>One encrypted sync file as read from a transport; <see cref="Version"/> identifies its content.</summary>
public sealed record SyncBlob(string Name, byte[] Data)
{
    public string Version { get; } = Convert.ToHexString(SHA256.HashData(Data));
}

/// <summary>Where the encrypted sync file lives. Today a folder (<see cref="FolderSyncTransport"/>); a server can follow.</summary>
public interface ISyncTransport
{
    /// <summary>For the UI and the log.</summary>
    string Location { get; }

    /// <summary>The sync file, or null when there is none yet.</summary>
    Task<SyncBlob?> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Other versions the sync service set aside (Syncthing/OneDrive conflict copies); merged, then deleted.</summary>
    Task<IReadOnlyList<SyncBlob>> ReadConflictCopiesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the sync file. Throws <see cref="SyncException"/> with <see cref="SyncError.ChangedDuringWrite"/>
    /// when the file is no longer <paramref name="expectedVersion"/> (null: there was none).
    /// </summary>
    Task WriteAsync(byte[] data, string? expectedVersion, CancellationToken cancellationToken = default);

    Task DeleteConflictCopyAsync(SyncBlob copy, CancellationToken cancellationToken = default);
}

/// <summary>
/// The sync file in a folder the user picks (OneDrive, Syncthing, a network share). Writes go to a temporary
/// file in the same folder and are renamed over the old one, so a reader never sees half a file; locked files
/// (sync clients, virus scanners) are retried. Conflict copies are recognized by name:
/// "remote-manager.sync-conflict-…sync" (Syncthing), "remote-manager-PC.sync" (OneDrive), "remote-manager (… conflicted copy …).sync".
/// </summary>
public sealed class FolderSyncTransport(string folder) : ISyncTransport
{
    public const string FileName = "remote-manager.sync";
    private const string BaseName = "remote-manager";
    private const string Extension = ".sync";
    private const string TempExtension = ".tmp";
    private static readonly TimeSpan[] RetryDelays = [.. new[] { 100, 250, 500, 1000, 2000 }.Select(ms => TimeSpan.FromMilliseconds(ms))];

    public string Folder { get; } = folder;
    public string FilePath => Path.Combine(Folder, FileName);
    public string Location => FilePath;

    public async Task<SyncBlob?> ReadAsync(CancellationToken cancellationToken = default)
    {
        EnsureFolder();
        byte[]? data = await ReadFileAsync(FilePath, cancellationToken).ConfigureAwait(false);
        return data is null ? null : new SyncBlob(FileName, data);
    }

    public async Task<IReadOnlyList<SyncBlob>> ReadConflictCopiesAsync(CancellationToken cancellationToken = default)
    {
        EnsureFolder();
        var copies = new List<SyncBlob>();
        foreach (string path in Directory.EnumerateFiles(Folder, BaseName + "*" + Extension).Order(StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(path);
            if (string.Equals(name, FileName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (await ReadFileAsync(path, cancellationToken).ConfigureAwait(false) is { } data)
                copies.Add(new SyncBlob(name, data));
        }

        return copies;
    }

    public async Task WriteAsync(byte[] data, string? expectedVersion, CancellationToken cancellationToken = default)
    {
        EnsureFolder();
        RemoveStaleTempFiles();
        string temp = Path.Combine(Folder, $"{FileName}.{Guid.NewGuid():N}{TempExtension}");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            await RetryAsync(() =>
            {
                // Last check right before the rename: did another device's write land in the meantime?
                byte[]? current = File.Exists(FilePath) ? File.ReadAllBytes(FilePath) : null;
                string? currentVersion = current is null ? null : new SyncBlob(FileName, current).Version;
                if (currentVersion != expectedVersion)
                    throw new SyncException(SyncError.ChangedDuringWrite, "The sync file changed while merging.");
                File.Move(temp, FilePath, overwrite: true);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public Task DeleteConflictCopyAsync(SyncBlob copy, CancellationToken cancellationToken = default) =>
        RetryAsync(() => File.Delete(Path.Combine(Folder, copy.Name)), cancellationToken);

    private void EnsureFolder()
    {
        if (!Directory.Exists(Folder))
            throw new SyncException(SyncError.FolderMissing, $"Sync folder {Folder} does not exist.");
    }

    private static async Task<byte[]?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        byte[]? data = null;
        await RetryAsync(() =>
        {
            if (!File.Exists(path))
            {
                data = null;
                return;
            }

            // Share everything: a sync client may hold the file open while we read.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            data = memory.ToArray();
        }, cancellationToken).ConfigureAwait(false);
        return data;
    }

    /// <summary>Runs <paramref name="action"/>, retrying while the file is locked (IOException other than "not found").</summary>
    private static async Task RetryAsync(Action action, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
            {
                if (attempt >= RetryDelays.Length)
                    throw new SyncException(SyncError.Locked, $"The sync file stays locked: {ex.Message}", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                // Also what Windows reports for a file that is being replaced or deleted.
                if (attempt >= RetryDelays.Length)
                    throw new SyncException(SyncError.Locked, $"No access to the sync file: {ex.Message}", ex);
            }

            await Task.Delay(RetryDelays[attempt], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Temporary files left by a crash or a killed process (older than ten minutes).</summary>
    private void RemoveStaleTempFiles()
    {
        foreach (string path in Directory.EnumerateFiles(Folder, FileName + ".*" + TempExtension))
        {
            if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddMinutes(-10))
                TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
