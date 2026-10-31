using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RemoteManager.App.Connections.Rdp;

/// <summary>
/// The Credential Manager entry mstsc reads for a host ("TERMSRV/&lt;host&gt;", a domain password
/// credential). mstsc takes no password on its command line or (unencrypted) in an .rdp file, so the
/// external start puts the password here, only for the logon session (CRED_PERSIST_SESSION), and
/// removes it when mstsc exits.
/// </summary>
internal static class TermsrvCredential
{
    private const int CredTypeDomainPassword = 2;
    private const int CredPersistSession = 1;
    private const int ErrorNotFound = 1168;

    public static string TargetFor(string host) => $"TERMSRV/{host}";

    public static bool Exists(string host)
    {
        if (!CredRead(TargetFor(host), CredTypeDomainPassword, 0, out nint pointer))
            return Marshal.GetLastWin32Error() == ErrorNotFound ? false : throw new Win32Exception(Marshal.GetLastWin32Error());
        CredFree(pointer);
        return true;
    }

    public static void Write(string host, string userName, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(userName);
        nint blob = Marshal.StringToCoTaskMemUni(password);
        try
        {
            var credential = new Credential
            {
                Type = CredTypeDomainPassword,
                TargetName = TargetFor(host),
                UserName = userName,
                CredentialBlobSize = password.Length * sizeof(char),
                CredentialBlob = blob,
                Persist = CredPersistSession,
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.Copy(new byte[password.Length * sizeof(char)], 0, blob, password.Length * sizeof(char));
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public static bool Delete(string host) => CredDelete(TargetFor(host), CredTypeDomainPassword, 0);

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
}
