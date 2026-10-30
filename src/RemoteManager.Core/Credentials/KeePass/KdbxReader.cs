using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Org.BouncyCastle.Crypto;

namespace RemoteManager.Core.Credentials.KeePass;

/// <summary>One entry of a KeePass database (history and recycle bin left out).</summary>
/// <param name="Uuid">The entry's UUID as 32 hex characters (the 16 bytes as stored in the file).</param>
/// <param name="GroupPath">Group names below the root group.</param>
public sealed record KdbxEntry(string Uuid, IReadOnlyList<string> GroupPath, IReadOnlyDictionary<string, string> Fields)
{
    public string Title => Fields.GetValueOrDefault("Title", "");
    public string UserName => Fields.GetValueOrDefault("UserName", "");
    public string Password => Fields.GetValueOrDefault("Password", "");

    /// <summary>"Group/Subgroup/Title", the form the reference editor shows.</summary>
    public string Path => string.Join('/', GroupPath.Append(Title));
}

/// <summary>
/// Reads KeePass databases (KDBX 3.1 and 4.x: AES-256, ChaCha20, or Twofish; AES-KDF, Argon2d, or Argon2id;
/// key files). Read-only and in memory: the file is never written, and nothing decrypted is cached.
/// </summary>
public static class KdbxReader
{
    internal const uint Signature1 = 0x9AA2D903, Signature2 = 0xB54BFB67;

    private const byte HeaderEnd = 0, HeaderCipher = 2, HeaderCompression = 3, HeaderMasterSeed = 4, HeaderTransformSeed = 5,
        HeaderTransformRounds = 6, HeaderIv = 7, HeaderStreamKey = 8, HeaderStreamStart = 9, HeaderInnerStreamId = 10, HeaderKdf = 11;

    private const byte InnerHeaderEnd = 0, InnerHeaderStreamId = 1, InnerHeaderStreamKey = 2;

    /// <summary>Reads the entries of <paramref name="path"/>. Throws <see cref="CredentialSourceException"/>.</summary>
    public static IReadOnlyList<KdbxEntry> ReadEntries(string path, KdbxCompositeKey key)
    {
        byte[] file;
        try
        {
            file = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new CredentialSourceException(CredentialError.FileNotFound, System.IO.Path.GetFileName(path), ex);
        }

        return ReadEntries(file, key);
    }

    public static IReadOnlyList<KdbxEntry> ReadEntries(byte[] file, KdbxCompositeKey key)
    {
        try
        {
            return Parse(file, key);
        }
        catch (Exception ex) when (ex is EndOfStreamException or ArgumentOutOfRangeException or InvalidDataException or XmlException or FormatException)
        {
            throw new CredentialSourceException(CredentialError.UnsupportedFormat, ex.GetType().Name, ex);
        }
    }

    private static IReadOnlyList<KdbxEntry> Parse(byte[] file, KdbxCompositeKey key)
    {
        var reader = new BinaryReader(new MemoryStream(file));
        if (file.Length < 12 || reader.ReadUInt32() != Signature1 || reader.ReadUInt32() != Signature2)
            throw new CredentialSourceException(CredentialError.UnsupportedFormat, "no KeePass 2 database");
        reader.ReadUInt16(); // minor version
        ushort major = reader.ReadUInt16();
        if (major is not (3 or 4))
            throw new CredentialSourceException(CredentialError.UnsupportedFormat, $"KDBX {major}");
        bool v4 = major == 4;

        var header = new Dictionary<byte, byte[]>();
        while (true)
        {
            byte id = reader.ReadByte();
            int size = v4 ? reader.ReadInt32() : reader.ReadUInt16();
            header[id] = reader.ReadBytes(size);
            if (id == HeaderEnd)
                break;
        }

        int headerLength = (int)reader.BaseStream.Position;
        byte[] cipher = Required(header, HeaderCipher);
        byte[] masterSeed = Required(header, HeaderMasterSeed);
        byte[] iv = Required(header, HeaderIv);
        bool compressed = header.TryGetValue(HeaderCompression, out byte[]? flags) && flags.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(flags) == 1;

        byte[] transformed = v4
            ? DeriveV4(key, KdbxVariantDictionary.Parse(Required(header, HeaderKdf)))
            : KdbxCrypto.AesKdf(key.Hash, Required(header, HeaderTransformSeed), BinaryPrimitives.ReadUInt64LittleEndian(Required(header, HeaderTransformRounds)));
        byte[] seedAndKey = [.. masterSeed, .. transformed];
        byte[] cipherKey = SHA256.HashData(seedAndKey);
        CryptographicOperations.ZeroMemory(transformed);

        try
        {
            byte[] payload;
            int innerStreamId;
            byte[] innerStreamKey;
            if (v4)
            {
                byte[] hmacBase = SHA512.HashData([.. seedAndKey, 0x01]);
                try
                {
                    ReadOnlySpan<byte> headerBytes = file.AsSpan(0, headerLength);
                    if (!SHA256.HashData(headerBytes).AsSpan().SequenceEqual(reader.ReadBytes(32)))
                        throw new CredentialSourceException(CredentialError.UnsupportedFormat, "header hash");
                    byte[] headerHmac = HMACSHA256.HashData(KdbxCrypto.BlockHmacKey(hmacBase, ulong.MaxValue), headerBytes);
                    if (!CryptographicOperations.FixedTimeEquals(headerHmac, reader.ReadBytes(32)))
                        throw new CredentialSourceException(CredentialError.WrongMasterKey);

                    byte[] encrypted = ReadHmacBlocks(reader, hmacBase);
                    payload = KdbxCrypto.Decrypt(cipher, cipherKey, iv, encrypted);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(hmacBase);
                }

                if (compressed)
                    payload = Gunzip(payload);
                (innerStreamId, innerStreamKey, int xmlStart) = ReadInnerHeader(payload);
                payload = payload[xmlStart..];
            }
            else
            {
                byte[] decrypted = KdbxCrypto.Decrypt(cipher, cipherKey, iv, file[headerLength..]);
                byte[] start = Required(header, HeaderStreamStart);
                if (decrypted.Length < start.Length || !decrypted.AsSpan(0, start.Length).SequenceEqual(start))
                    throw new CredentialSourceException(CredentialError.WrongMasterKey);
                payload = ReadHashedBlocks(decrypted.AsSpan(start.Length));
                if (compressed)
                    payload = Gunzip(payload);
                innerStreamId = (int)BinaryPrimitives.ReadUInt32LittleEndian(Required(header, HeaderInnerStreamId));
                innerStreamKey = Required(header, HeaderStreamKey);
            }

            return ParseXml(payload, KdbxCrypto.InnerStream(innerStreamId, innerStreamKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cipherKey);
            CryptographicOperations.ZeroMemory(seedAndKey);
        }
    }

    private static byte[] DeriveV4(KdbxCompositeKey key, KdbxVariantDictionary kdf)
    {
        byte[] uuid = kdf.Bytes("$UUID") ?? [];
        if (uuid.AsSpan().SequenceEqual(KdbxCrypto.KdfAes))
            return KdbxCrypto.AesKdf(key.Hash, kdf.Bytes("S") ?? [], kdf.UInt64("R") ?? 0);
        if (uuid.AsSpan().SequenceEqual(KdbxCrypto.KdfArgon2d) || uuid.AsSpan().SequenceEqual(KdbxCrypto.KdfArgon2id))
            return KdbxCrypto.Argon2(key.Hash, uuid.AsSpan().SequenceEqual(KdbxCrypto.KdfArgon2id), kdf);
        throw new CredentialSourceException(CredentialError.UnsupportedFormat, $"KDF {Convert.ToHexString(uuid)}");
    }

    private static byte[] Required(Dictionary<byte, byte[]> header, byte id) =>
        header.TryGetValue(id, out byte[]? value) ? value : throw new CredentialSourceException(CredentialError.UnsupportedFormat, $"header field {id} missing");

    private static byte[] ReadHmacBlocks(BinaryReader reader, byte[] hmacBase)
    {
        using var output = new MemoryStream();
        for (ulong index = 0; ; index++)
        {
            byte[] expected = reader.ReadBytes(32);
            int size = reader.ReadInt32();
            if (size < 0)
                throw new InvalidDataException("block size");
            byte[] data = reader.ReadBytes(size);
            if (data.Length != size || !CryptographicOperations.FixedTimeEquals(expected, KdbxCrypto.BlockHmac(hmacBase, index, data)))
                throw new CredentialSourceException(CredentialError.UnsupportedFormat, $"block {index} damaged");
            if (size == 0)
                return output.ToArray();
            output.Write(data);
        }
    }

    private static byte[] ReadHashedBlocks(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        int position = 0;
        while (true)
        {
            if (position + 40 > data.Length)
                throw new InvalidDataException("hashed block");
            ReadOnlySpan<byte> hash = data.Slice(position + 4, 32);
            int size = BinaryPrimitives.ReadInt32LittleEndian(data[(position + 36)..]);
            position += 40;
            if (size == 0)
                return output.ToArray();
            if (size < 0 || position + size > data.Length)
                throw new InvalidDataException("hashed block");
            ReadOnlySpan<byte> block = data.Slice(position, size);
            if (!SHA256.HashData(block).AsSpan().SequenceEqual(hash))
                throw new CredentialSourceException(CredentialError.UnsupportedFormat, "block damaged");
            output.Write(block);
            position += size;
        }
    }

    private static (int Id, byte[] Key, int End) ReadInnerHeader(byte[] payload)
    {
        int id = 0;
        byte[] key = [];
        int position = 0;
        while (true)
        {
            byte field = payload[position];
            int size = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(position + 1));
            if (size < 0 || position + 5 + size > payload.Length)
                throw new InvalidDataException("inner header");
            byte[] value = payload.AsSpan(position + 5, size).ToArray();
            position += 5 + size;
            switch (field)
            {
                case InnerHeaderEnd:
                    return (id, key, position);
                case InnerHeaderStreamId:
                    id = (int)BinaryPrimitives.ReadUInt32LittleEndian(value);
                    break;
                case InnerHeaderStreamKey:
                    key = value;
                    break;
            }
        }
    }

    private static byte[] Gunzip(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static List<KdbxEntry> ParseXml(byte[] xml, IStreamCipher innerStream)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = false };
        XDocument document;
        using (var reader = XmlReader.Create(new MemoryStream(xml), settings))
            document = XDocument.Load(reader);
        CryptographicOperations.ZeroMemory(xml);

        // Protected values share one key stream, consumed in document order (history entries included).
        foreach (XElement value in document.Descendants().Where(e => string.Equals((string?)e.Attribute("Protected"), "True", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            byte[] data = Convert.FromBase64String(value.Value);
            byte[] plain = new byte[data.Length];
            innerStream.ProcessBytes(data, 0, data.Length, plain, 0);
            value.Value = Encoding.UTF8.GetString(plain);
            CryptographicOperations.ZeroMemory(plain);
        }

        XElement? meta = document.Root?.Element("Meta");
        string? recycleBin = meta?.Element("RecycleBinEnabled")?.Value.Equals("True", StringComparison.OrdinalIgnoreCase) == true
            ? UuidHex(meta.Element("RecycleBinUUID")?.Value)
            : null;

        var entries = new List<KdbxEntry>();
        if (document.Root?.Element("Root")?.Element("Group") is { } rootGroup)
            CollectEntries(rootGroup, [], recycleBin, entries);
        return entries;
    }

    private static void CollectEntries(XElement group, List<string> path, string? recycleBin, List<KdbxEntry> entries)
    {
        foreach (XElement entry in group.Elements("Entry"))
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (XElement field in entry.Elements("String"))
            {
                if (field.Element("Key")?.Value is { } name)
                    fields[name] = field.Element("Value")?.Value ?? "";
            }

            entries.Add(new KdbxEntry(UuidHex(entry.Element("UUID")?.Value) ?? "", [.. path], fields));
        }

        foreach (XElement child in group.Elements("Group"))
        {
            if (recycleBin is not null && UuidHex(child.Element("UUID")?.Value) == recycleBin)
                continue;
            path.Add(child.Element("Name")?.Value ?? "");
            CollectEntries(child, path, recycleBin, entries);
            path.RemoveAt(path.Count - 1);
        }
    }

    private static string? UuidHex(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            return null;
        try
        {
            return Convert.ToHexString(Convert.FromBase64String(base64.Trim())).ToLowerInvariant();
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
