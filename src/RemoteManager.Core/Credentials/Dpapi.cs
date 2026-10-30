using System.Runtime.InteropServices;
using System.Text;

namespace RemoteManager.Core.Credentials;

/// <summary>
/// Windows DPAPI for the current user (CryptUnprotectData), used to read passwords that other tools
/// stored with it (RDCMan). The result goes straight to a credential source, never to a file or log.
/// </summary>
public static class Dpapi
{
    /// <summary>The UTF-16 text in a base64 DPAPI blob, or null when it cannot be decrypted (other user/machine, not Windows).</summary>
    public static string? TryUnprotectBase64(string base64)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(base64))
            return null;

        byte[] data;
        try
        {
            data = Convert.FromBase64String(base64.Trim());
        }
        catch (FormatException)
        {
            return null;
        }

        byte[]? plain = Unprotect(data);
        if (plain is null)
            return null;
        try
        {
            return Encoding.Unicode.GetString(plain).TrimEnd('\0');
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    /// <summary>Encrypts UTF-16 text for the current user, as RDCMan stores it (used to build test files).</summary>
    public static string ProtectToBase64(string text)
    {
        byte[] data = Encoding.Unicode.GetBytes(text);
        GCHandle pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new DataBlob { Size = data.Length, Data = pinned.AddrOfPinnedObject() };
            if (!CryptProtectData(ref input, 0, 0, 0, 0, CryptProtectUiForbidden, out DataBlob output))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                byte[] result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return Convert.ToBase64String(result);
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            pinned.Free();
            Array.Clear(data);
        }
    }

    private static byte[]? Unprotect(byte[] data)
    {
        GCHandle pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new DataBlob { Size = data.Length, Data = pinned.AddrOfPinnedObject() };
            if (!CryptUnprotectData(ref input, 0, 0, 0, 0, CryptProtectUiForbidden, out DataBlob output))
                return null;
            try
            {
                byte[] result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                Marshal.Copy(new byte[output.Size], 0, output.Data, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, nint description, nint entropy, nint reserved, nint prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, nint description, nint entropy, nint reserved, nint prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint LocalFree(nint memory);
}
