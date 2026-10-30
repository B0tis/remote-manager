using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace RemoteManager.Core.Credentials.KeePass;

/// <summary>Cipher and key derivation of a database written by <see cref="KdbxWriter"/>.</summary>
public enum KdbxWriteMode
{
    /// <summary>AES-256 with AES-KDF (fast to open; for tests).</summary>
    AesWithAesKdf,

    /// <summary>ChaCha20 with Argon2d, the KeePass default.</summary>
    ChaCha20WithArgon2d,
}

/// <summary>An entry to write; <see cref="Group"/> is "A/B" (empty: the root group).</summary>
public sealed record KdbxNewEntry(string Group, string Title, string UserName, string Password, string? Uuid = null);

/// <summary>
/// Writes a small KDBX 4.0 database. The app never writes the user's databases; this exists so tests and
/// the self-test can create throwaway ones (KeePass and KeePassXC open the result).
/// </summary>
public static class KdbxWriter
{
    public static void Write(string path, KdbxCompositeKey key, IEnumerable<KdbxNewEntry> entries, KdbxWriteMode mode = KdbxWriteMode.AesWithAesKdf, ulong aesRounds = 1000)
    {
        bool aes = mode == KdbxWriteMode.AesWithAesKdf;
        byte[] cipher = aes ? KdbxCrypto.CipherAes256 : KdbxCrypto.CipherChaCha20;
        byte[] masterSeed = RandomNumberGenerator.GetBytes(32);
        byte[] iv = RandomNumberGenerator.GetBytes(aes ? 16 : 12);
        KdbxVariantDictionary kdf = aes
            ? new KdbxVariantDictionary().Set("$UUID", KdbxCrypto.KdfAes).Set("R", aesRounds).Set("S", RandomNumberGenerator.GetBytes(32))
            : new KdbxVariantDictionary().Set("$UUID", KdbxCrypto.KdfArgon2d).Set("S", RandomNumberGenerator.GetBytes(32))
                .Set("P", 2u).Set("M", 8ul * 1024 * 1024).Set("I", 2ul).Set("V", 0x13u);

        using var header = new MemoryStream();
        var writer = new BinaryWriter(header);
        writer.Write(KdbxReader.Signature1);
        writer.Write(KdbxReader.Signature2);
        writer.Write((ushort)0);
        writer.Write((ushort)4);
        WriteField(writer, 2, cipher);
        WriteField(writer, 3, [1, 0, 0, 0]); // gzip
        WriteField(writer, 4, masterSeed);
        WriteField(writer, 7, iv);
        WriteField(writer, 11, kdf.ToBytes());
        WriteField(writer, 0, [0x0D, 0x0A, 0x0D, 0x0A]);
        writer.Flush();
        byte[] headerBytes = header.ToArray();

        byte[] transformed = aes
            ? KdbxCrypto.AesKdf(key.Hash, kdf.Bytes("S")!, aesRounds)
            : KdbxCrypto.Argon2(key.Hash, false, kdf);
        byte[] seedAndKey = [.. masterSeed, .. transformed];
        byte[] cipherKey = SHA256.HashData(seedAndKey);
        byte[] hmacBase = SHA512.HashData([.. seedAndKey, 0x01]);

        // Inner header + XML, gzip, encrypt.
        byte[] innerKey = RandomNumberGenerator.GetBytes(64);
        using var inner = new MemoryStream();
        var innerWriter = new BinaryWriter(inner);
        WriteInnerField(innerWriter, 1, BitConverter.GetBytes((uint)KdbxCrypto.InnerStreamChaCha20));
        WriteInnerField(innerWriter, 2, innerKey);
        WriteInnerField(innerWriter, 0, []);
        innerWriter.Write(Xml(entries, KdbxCrypto.InnerStream(KdbxCrypto.InnerStreamChaCha20, innerKey)));
        innerWriter.Flush();
        byte[] encrypted = KdbxCrypto.Encrypt(cipher, cipherKey, iv, Gzip(inner.ToArray()));

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        file.Write(headerBytes);
        file.Write(SHA256.HashData(headerBytes));
        file.Write(HMACSHA256.HashData(KdbxCrypto.BlockHmacKey(hmacBase, ulong.MaxValue), headerBytes));
        WriteBlock(file, hmacBase, 0, encrypted);
        WriteBlock(file, hmacBase, 1, []);

        CryptographicOperations.ZeroMemory(transformed);
        CryptographicOperations.ZeroMemory(seedAndKey);
        CryptographicOperations.ZeroMemory(cipherKey);
        CryptographicOperations.ZeroMemory(hmacBase);
    }

    private static void WriteField(BinaryWriter writer, byte id, byte[] value)
    {
        writer.Write(id);
        writer.Write(value.Length);
        writer.Write(value);
    }

    private static void WriteInnerField(BinaryWriter writer, byte id, byte[] value) => WriteField(writer, id, value);

    private static void WriteBlock(Stream file, byte[] hmacBase, ulong index, byte[] data)
    {
        file.Write(KdbxCrypto.BlockHmac(hmacBase, index, data));
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, data.Length);
        file.Write(size);
        file.Write(data);
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(data);
        return output.ToArray();
    }

    private static byte[] Xml(IEnumerable<KdbxNewEntry> entries, Org.BouncyCastle.Crypto.IStreamCipher stream)
    {
        static string NewUuid() => Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var root = new XElement("Group", new XElement("UUID", NewUuid()), new XElement("Name", "Root"));
        var groups = new Dictionary<string, XElement>(StringComparer.Ordinal) { [""] = root };

        XElement GroupFor(string path)
        {
            if (groups.TryGetValue(path, out XElement? existing))
                return existing;
            int slash = path.LastIndexOf('/');
            XElement parent = GroupFor(slash < 0 ? "" : path[..slash]);
            var group = new XElement("Group", new XElement("UUID", NewUuid()), new XElement("Name", slash < 0 ? path : path[(slash + 1)..]));
            parent.Add(group);
            groups[path] = group;
            return group;
        }

        XElement Protected(string text)
        {
            byte[] plain = Encoding.UTF8.GetBytes(text);
            byte[] data = new byte[plain.Length];
            stream.ProcessBytes(plain, 0, plain.Length, data, 0);
            CryptographicOperations.ZeroMemory(plain);
            return new XElement("Value", new XAttribute("Protected", "True"), Convert.ToBase64String(data));
        }

        foreach (KdbxNewEntry entry in entries)
        {
            string uuid = entry.Uuid is { } hex ? Convert.ToBase64String(Convert.FromHexString(hex)) : NewUuid();
            GroupFor(entry.Group).Add(new XElement("Entry",
                new XElement("UUID", uuid),
                new XElement("String", new XElement("Key", "Title"), new XElement("Value", entry.Title)),
                new XElement("String", new XElement("Key", "UserName"), new XElement("Value", entry.UserName)),
                new XElement("String", new XElement("Key", "Password"), Protected(entry.Password))));
        }

        var document = new XDocument(new XElement("KeePassFile",
            new XElement("Meta", new XElement("Generator", "RemoteManager"), new XElement("RecycleBinEnabled", "False")),
            new XElement("Root", root)));
        using var output = new MemoryStream();
        document.Save(output, SaveOptions.DisableFormatting);
        return output.ToArray();
    }
}
