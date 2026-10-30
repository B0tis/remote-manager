using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RemoteManager.App;

/// <summary>
/// Connection passwords in the Windows Credential Manager (generic credential, persisted per
/// machine for this user, target "RemoteManager:&lt;connection id&gt;"). The secret is DPAPI-protected
/// by Windows; it never goes into connections.json, logs, or snapshots.
/// Deliberately one small class: whether a password manager becomes a second source is still open.
/// </summary>
internal static class CredentialStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public static string TargetFor(string connectionId) => $"RemoteManager:{connectionId}";

    /// <summary>Creates or replaces the password of a connection.</summary>
    public static void Write(string connectionId, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        nint blob = Marshal.StringToCoTaskMemUni(password);
        try
        {
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = TargetFor(connectionId),
                CredentialBlobSize = password.Length * sizeof(char),
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                // The user name lives in connections.json; keeping it out of here avoids a stale copy.
                UserName = "",
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            ZeroMemory(blob, password.Length * sizeof(char));
            Marshal.FreeCoTaskMem(blob);
        }
    }

    /// <summary>The stored password, or null when the connection has none.</summary>
    public static string? Read(string connectionId)
    {
        if (!CredRead(TargetFor(connectionId), CredTypeGeneric, 0, out nint pointer))
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

    public static bool Exists(string connectionId)
    {
        if (!CredRead(TargetFor(connectionId), CredTypeGeneric, 0, out nint pointer))
            return false;
        CredFree(pointer);
        return true;
    }

    /// <summary>Removes the password. Returns false when there was none.</summary>
    public static bool Delete(string connectionId)
    {
        if (CredDelete(TargetFor(connectionId), CredTypeGeneric, 0))
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
