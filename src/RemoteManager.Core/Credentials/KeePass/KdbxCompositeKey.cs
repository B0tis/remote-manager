using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace RemoteManager.Core.Credentials.KeePass;

/// <summary>
/// The composite key of a KeePass database: SHA-256 over SHA-256(password) and the key file's key.
/// Only this hash is kept (in memory), never the master password itself. Zeroed on dispose.
/// </summary>
public sealed class KdbxCompositeKey : IDisposable
{
    private readonly byte[] hash;

    private KdbxCompositeKey(byte[] hash) => this.hash = hash;

    /// <param name="password">Master password; null when the database has none (key file only).</param>
    /// <param name="keyFilePath">Optional key file (KeePass XML 1.0/2.0, 32 raw bytes, 64 hex characters, or any file).</param>
    public static KdbxCompositeKey Create(string? password, string? keyFilePath)
    {
        if (password is null && string.IsNullOrEmpty(keyFilePath))
            throw new ArgumentException("A master password or a key file is required.", nameof(password));

        using var parts = new MemoryStream();
        if (password is not null)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(password);
            parts.Write(SHA256.HashData(utf8));
            CryptographicOperations.ZeroMemory(utf8);
        }

        if (!string.IsNullOrEmpty(keyFilePath))
        {
            byte[] key = ReadKeyFile(keyFilePath);
            parts.Write(key);
            CryptographicOperations.ZeroMemory(key);
        }

        byte[] buffer = parts.GetBuffer();
        byte[] composite = SHA256.HashData(buffer.AsSpan(0, (int)parts.Length));
        CryptographicOperations.ZeroMemory(buffer);
        return new KdbxCompositeKey(composite);
    }

    internal ReadOnlySpan<byte> Hash => hash;

    public KdbxCompositeKey Clone() => new((byte[])hash.Clone());

    public void Dispose() => CryptographicOperations.ZeroMemory(hash);

    /// <summary>The 32-byte key a key file contributes.</summary>
    internal static byte[] ReadKeyFile(string path)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new CredentialSourceException(CredentialError.FileNotFound, Path.GetFileName(path), ex);
        }

        try
        {
            if (TryXmlKey(data) is { } xmlKey)
                return xmlKey;
            if (data.Length == 32)
                return (byte[])data.Clone();
            if (data.Length == 64 && TryHex(Encoding.ASCII.GetString(data)) is { Length: 32 } hexKey)
                return hexKey;
            return SHA256.HashData(data);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    private static byte[]? TryXmlKey(byte[] data)
    {
        if (data.Length == 0 || data.Length > 64 * 1024)
            return null;
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(data), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            XElement root = XElement.Load(reader);
            if (root.Name.LocalName != "KeyFile")
                return null;
            string version = root.Element("Meta")?.Element("Version")?.Value.Trim() ?? "1.0";
            string text = root.Element("Key")?.Element("Data")?.Value ?? "";
            return version.StartsWith('2')
                ? TryHex(text) is { Length: 32 } key ? key : throw new CredentialSourceException(CredentialError.UnsupportedFormat, "key file")
                : Convert.FromBase64String(text.Trim());
        }
        catch (XmlException)
        {
            return null;
        }
        catch (FormatException ex)
        {
            throw new CredentialSourceException(CredentialError.UnsupportedFormat, "key file", ex);
        }
    }

    private static byte[]? TryHex(string text)
    {
        string compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        try
        {
            return Convert.FromHexString(compact);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
