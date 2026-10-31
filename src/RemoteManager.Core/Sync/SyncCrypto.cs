using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace RemoteManager.Core.Sync;

/// <summary>Key derivation parameters of a sync file; the salt is made once, when the file is first written.</summary>
public sealed record SyncKdf(int MemoryKiB, int Iterations, int Parallelism, byte[] Salt)
{
    /// <summary>
    /// Argon2id, 64 MiB, 3 passes, 4 lanes (RFC 9106, second recommended option). Memory-hard, so guessing
    /// the passphrase of a stolen sync file on GPUs costs far more than PBKDF2 at any practical iteration count.
    /// The parameters are stored in the file, so a later build can raise them.
    /// </summary>
    public static SyncKdf CreateDefault() => new(64 * 1024, 3, 4, RandomNumberGenerator.GetBytes(16));
}

/// <summary>
/// The sync file format: an unencrypted header (magic, version, KDF parameters, salt, key check, nonce)
/// followed by the AES-256-GCM encrypted snapshot JSON; the header is authenticated as associated data.
/// No names, hosts, or ids outside the ciphertext. The key check (HMAC of a fixed label) tells a wrong
/// passphrase apart from a damaged file.
/// </summary>
public static class SyncCrypto
{
    private static readonly byte[] Magic = "RMSYNC\r\n"u8.ToArray();
    private static readonly byte[] KeyCheckLabel = "Remote-Manager sync key check"u8.ToArray();
    private const byte Version = 1;
    private const byte KdfArgon2id = 1;
    private const int SaltLength = 16, CheckLength = 16, NonceLength = 12, TagLength = 16;
    private const int HeaderLength = 8 + 1 + 1 + 4 * 3 + SaltLength + CheckLength + NonceLength;

    private static readonly ConcurrentDictionary<string, byte[]> KeyCache = new();

    public static byte[] Encrypt(byte[] plaintext, string passphrase, SyncKdf kdf)
    {
        byte[] key = DeriveKey(passphrase, kdf);
        byte[] file = new byte[HeaderLength + TagLength + plaintext.Length];
        Span<byte> header = file.AsSpan(0, HeaderLength);
        Magic.CopyTo(header);
        header[8] = Version;
        header[9] = KdfArgon2id;
        BinaryPrimitives.WriteInt32LittleEndian(header[10..], kdf.MemoryKiB);
        BinaryPrimitives.WriteInt32LittleEndian(header[14..], kdf.Iterations);
        BinaryPrimitives.WriteInt32LittleEndian(header[18..], kdf.Parallelism);
        kdf.Salt.CopyTo(header[22..]);
        KeyCheck(key).CopyTo(header[(22 + SaltLength)..]);
        RandomNumberGenerator.Fill(header[(22 + SaltLength + CheckLength)..]);

        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(header[^NonceLength..], plaintext, file.AsSpan(HeaderLength + TagLength), file.AsSpan(HeaderLength, TagLength), header);
        return file;
    }

    /// <summary>Opens a sync file. Throws <see cref="SyncException"/> for a wrong passphrase, a damaged file, or a newer format.</summary>
    public static byte[] Decrypt(byte[] file, string passphrase, out SyncKdf kdf)
    {
        if (file.Length < HeaderLength + TagLength || !file.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new SyncException(SyncError.Damaged, "Not a Remote-Manager sync file or incomplete.");
        ReadOnlySpan<byte> header = file.AsSpan(0, HeaderLength);
        if (header[8] > Version || header[9] != KdfArgon2id)
            throw new SyncException(SyncError.NewerFormat, $"Sync file version {header[8]}, KDF {header[9]}.");

        kdf = new SyncKdf(
            BinaryPrimitives.ReadInt32LittleEndian(header[10..]),
            BinaryPrimitives.ReadInt32LittleEndian(header[14..]),
            BinaryPrimitives.ReadInt32LittleEndian(header[18..]),
            header.Slice(22, SaltLength).ToArray());
        if (kdf.MemoryKiB is < 8 * 1024 or > 4 * 1024 * 1024 || kdf.Iterations is < 1 or > 100 || kdf.Parallelism is < 1 or > 64)
            throw new SyncException(SyncError.Damaged, "Key derivation parameters out of range.");

        byte[] key = DeriveKey(passphrase, kdf);
        if (!CryptographicOperations.FixedTimeEquals(KeyCheck(key), header.Slice(22 + SaltLength, CheckLength)))
            throw new SyncException(SyncError.WrongPassphrase, "The passphrase does not open this sync file.");

        byte[] plaintext = new byte[file.Length - HeaderLength - TagLength];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(header[^NonceLength..], file.AsSpan(HeaderLength + TagLength), file.AsSpan(HeaderLength, TagLength), plaintext, header);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new SyncException(SyncError.Damaged, "The sync file is damaged or incomplete.", ex);
        }

        return plaintext;
    }

    /// <summary>The derived key, cached per passphrase and parameters for this process (Argon2id takes a moment).</summary>
    private static byte[] DeriveKey(string passphrase, SyncKdf kdf)
    {
        byte[] secret = Encoding.UTF8.GetBytes(passphrase);
        try
        {
            string cacheKey = Convert.ToHexString(SHA256.HashData([.. secret, .. kdf.Salt])) + $":{kdf.MemoryKiB}:{kdf.Iterations}:{kdf.Parallelism}";
            return KeyCache.GetOrAdd(cacheKey, _ =>
            {
                var generator = new Argon2BytesGenerator();
                generator.Init(new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
                    .WithVersion(Argon2Parameters.Version13)
                    .WithSalt(kdf.Salt)
                    .WithMemoryAsKB(kdf.MemoryKiB)
                    .WithIterations(kdf.Iterations)
                    .WithParallelism(kdf.Parallelism)
                    .Build());
                byte[] key = new byte[32];
                generator.GenerateBytes(secret, key);
                return key;
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static byte[] KeyCheck(byte[] key) => HMACSHA256.HashData(key, KeyCheckLabel)[..CheckLength];
}
