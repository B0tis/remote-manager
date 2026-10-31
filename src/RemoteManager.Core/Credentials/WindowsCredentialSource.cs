using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RemoteManager.Core.Credentials;

/// <summary>
/// Secrets in the Windows Credential Manager (generic credential, persisted per machine for this
/// user, target "RemoteManager:&lt;key&gt;"; the key of a connection's own password is its id).
/// Windows protects the blob with DPAPI; it never goes into the database, logs, or snapshots.
/// </summary>
public sealed class WindowsCredentialSource : ICredentialSource
{
    public const string SourceId = "wincred";

    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public string Id => SourceId;
    public bool IsWritable => true;
    public bool IsAvailable => OperatingSystem.IsWindows();

    public static string TargetFor(string key) => $"RemoteManager:{key}";

    public ValueTask<string?> ReadAsync(string key, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(key));

    public ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => ValueTask.FromResult(Exists(key));

    public ValueTask WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
    {
        Write(key, secret);
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => ValueTask.FromResult(Delete(key));

    /// <summary>Creates or replaces a secret.</summary>
    public static void Write(string key, string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        nint blob = Marshal.StringToCoTaskMemUni(secret);
        try
        {
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = TargetFor(key),
                CredentialBlobSize = secret.Length * sizeof(char),
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                // The user name lives in the database; keeping it out of here avoids a stale copy.
                UserName = "",
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            ZeroMemory(blob, secret.Length * sizeof(char));
            Marshal.FreeCoTaskMem(blob);
        }
    }

    /// <summary>The stored secret, or null when there is none.</summary>
    public static string? Read(string key)
    {
        if (!CredRead(TargetFor(key), CredTypeGeneric, 0, out nint pointer))
            return Marshal.GetLastWin32Error() == ErrorNotFound ? null : throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return credential.CredentialBlobSize == 0
                ? ""
                : Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / sizeof(char));
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public static bool Exists(string key)
    {
        if (!CredRead(TargetFor(key), CredTypeGeneric, 0, out nint pointer))
            return false;
        CredFree(pointer);
        return true;
    }

    /// <summary>Removes a secret. Returns false when there was none.</summary>
    public static bool Delete(string key)
    {
        if (CredDelete(TargetFor(key), CredTypeGeneric, 0))
            return true;
        return Marshal.GetLastWin32Error() == ErrorNotFound ? false : throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public nint CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int flags, out nint credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint buffer);

    [DllImport("kernel32.dll", EntryPoint = "RtlZeroMemory")]
    private static extern void ZeroMemory(nint destination, nint length);
}
