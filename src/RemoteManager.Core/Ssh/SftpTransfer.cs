using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace RemoteManager.Core.Ssh;

/// <summary>Progress of a transfer: file <see cref="FileIndex"/> of <see cref="FileCount"/>, bytes over all files.</summary>
public sealed record SftpProgress(string CurrentName, int FileIndex, int FileCount, long BytesDone, long BytesTotal)
{
    public double Fraction => BytesTotal > 0 ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1) : FileCount > 0 ? (double)FileIndex / FileCount : 0;
}

/// <summary>An entry of a remote folder listing.</summary>
public sealed record SftpEntry(string Name, string FullName, bool IsDirectory, bool IsSymbolicLink, long Length, DateTime LastWriteTime, string Permissions)
{
    public static SftpEntry From(ISftpFile file) => new(file.Name, file.FullName, file.IsDirectory, file.IsSymbolicLink, file.Length, file.LastWriteTime,
        PermissionText(file));

    private static string PermissionText(ISftpFile file)
    {
        static char Bit(bool set, char c) => set ? c : '-';
        return new string([
            file.IsDirectory ? 'd' : file.IsSymbolicLink ? 'l' : '-',
            Bit(file.OwnerCanRead, 'r'), Bit(file.OwnerCanWrite, 'w'), Bit(file.OwnerCanExecute, 'x'),
            Bit(file.GroupCanRead, 'r'), Bit(file.GroupCanWrite, 'w'), Bit(file.GroupCanExecute, 'x'),
            Bit(file.OthersCanRead, 'r'), Bit(file.OthersCanWrite, 'w'), Bit(file.OthersCanExecute, 'x'),
        ]);
    }
}

/// <summary>
/// Folder listing and recursive transfers for the SFTP browser. All methods block on the network;
/// call them off the UI thread.
/// </summary>
public static class SftpTransfer
{
    /// <summary>"/a/b" + "c" = "/a/b/c" (remote paths always use '/').</summary>
    public static string Combine(string directory, string name) =>
        directory.Length == 0 ? name : directory.EndsWith('/') ? directory + name : $"{directory}/{name}";

    /// <summary>The parent of a remote path ("/a/b" → "/a", "/a" → "/", "/" → "/").</summary>
    public static string Parent(string path)
    {
        string trimmed = path.TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        return slash <= 0 ? "/" : trimmed[..slash];
    }

    /// <summary>Folders first, then files, each by name; without "." and "..".</summary>
    public static IReadOnlyList<SftpEntry> List(SftpClient client, string path) =>
        [.. client.ListDirectory(path)
            .Where(f => f.Name is not ("." or ".."))
            .Select(SftpEntry.From)
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>Uploads files and folders (recursively) into <paramref name="remoteDirectory"/>, overwriting files with the same name.</summary>
    public static void Upload(SftpClient client, IEnumerable<string> localPaths, string remoteDirectory, IProgress<SftpProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        // Plan first, so progress knows the totals.
        var files = new List<(string Local, string Remote, long Length)>();
        var directories = new List<string>();
        foreach (string local in localPaths)
        {
            string name = Path.GetFileName(local.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string remote = Combine(remoteDirectory, name);
            if (Directory.Exists(local))
            {
                directories.Add(remote);
                foreach (string dir in Directory.EnumerateDirectories(local, "*", SearchOption.AllDirectories))
                    directories.Add(Combine(remote, Path.GetRelativePath(local, dir).Replace('\\', '/')));
                foreach (string file in Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories))
                    files.Add((file, Combine(remote, Path.GetRelativePath(local, file).Replace('\\', '/')), new FileInfo(file).Length));
            }
            else if (File.Exists(local))
            {
                files.Add((local, remote, new FileInfo(local).Length));
            }
        }

        foreach (string directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!client.Exists(directory))
                client.CreateDirectory(directory);
        }

        long total = files.Sum(f => f.Length), done = 0;
        for (int i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string local, string remote, long length) = files[i];
            string name = Path.GetFileName(local);
            long before = done;
            int index = i;
            progress?.Report(new SftpProgress(name, index, files.Count, before, total));
            using FileStream input = File.OpenRead(local);
            // SSH.NET may call back on another thread: report only, never throw there. Cancel applies between files.
            client.UploadFile(input, remote, canOverride: true, uploaded =>
                progress?.Report(new SftpProgress(name, index, files.Count, before + (long)uploaded, total)));
            done += length;
        }

        progress?.Report(new SftpProgress("", files.Count, files.Count, total, total));
    }

    /// <summary>
    /// Downloads entries (folders recursively) into <paramref name="localDirectory"/>. An existing local
    /// file is never overwritten: the copy gets " (2)", " (3)", ... Returns the top-level local paths.
    /// </summary>
    public static IReadOnlyList<string> Download(SftpClient client, IEnumerable<SftpEntry> entries, string localDirectory, IProgress<SftpProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var files = new List<(string Remote, string Local, long Length)>();
        var directories = new List<string>();
        var roots = new List<string>();
        foreach (SftpEntry entry in entries)
        {
            string target = FreeLocalName(Path.Combine(localDirectory, SafeLocalName(entry.Name)), entry.IsDirectory);
            roots.Add(target);
            if (entry.IsDirectory)
                Plan(client, entry.FullName, target, files, directories, cancellationToken);
            else
                files.Add((entry.FullName, target, entry.Length));
        }

        foreach (string directory in directories)
            Directory.CreateDirectory(directory);

        long total = files.Sum(f => f.Length), done = 0;
        for (int i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string remote, string local, long length) = files[i];
            string name = Path.GetFileName(local);
            long before = done;
            int index = i;
            progress?.Report(new SftpProgress(name, index, files.Count, before, total));
            using (FileStream output = File.Create(local))
            {
                client.DownloadFile(remote, output, downloaded =>
                    progress?.Report(new SftpProgress(name, index, files.Count, before + (long)downloaded, total)));
            }

            done += length;
        }

        progress?.Report(new SftpProgress("", files.Count, files.Count, total, total));
        return roots;
    }

    /// <summary>Deletes a file, or a folder with everything in it.</summary>
    public static void Delete(SftpClient client, SftpEntry entry, CancellationToken cancellationToken = default)
    {
        if (!entry.IsDirectory || entry.IsSymbolicLink)
        {
            client.DeleteFile(entry.FullName);
            return;
        }

        foreach (SftpEntry child in List(client, entry.FullName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delete(client, child, cancellationToken);
        }

        client.DeleteDirectory(entry.FullName);
    }

    private static void Plan(SftpClient client, string remoteDirectory, string localDirectory, List<(string, string, long)> files, List<string> directories, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        directories.Add(localDirectory);
        foreach (SftpEntry child in List(client, remoteDirectory))
        {
            string local = Path.Combine(localDirectory, SafeLocalName(child.Name));
            if (child.IsDirectory && !child.IsSymbolicLink)
                Plan(client, child.FullName, local, files, directories, cancellationToken);
            else if (!child.IsDirectory)
                files.Add((child.FullName, local, child.Length));
        }
    }

    /// <summary>A remote name made valid for Windows (characters like ':' or '?' become '_').</summary>
    public static string SafeLocalName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new([.. name.Select(c => invalid.Contains(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c)]);
        safe = safe.TrimEnd('.', ' ');
        return safe.Length == 0 ? "_" : safe;
    }

    private static string FreeLocalName(string path, bool directory)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;
        string folder = Path.GetDirectoryName(path)!;
        string stem = directory ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        string extension = directory ? "" : Path.GetExtension(path);
        for (int n = 2; ; n++)
        {
            string candidate = Path.Combine(folder, $"{stem} ({n}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }
}
