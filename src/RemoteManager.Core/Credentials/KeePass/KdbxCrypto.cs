using System.Buffers.Binary;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using Org.BouncyCastle.Crypto.Parameters;

namespace RemoteManager.Core.Credentials.KeePass;

/// <summary>Ciphers, key derivation, and inner streams of the KDBX format (KeePass 2.x, KeePassXC).</summary>
internal static class KdbxCrypto
{
    // UUIDs as they appear in the file (raw bytes).
    public static readonly byte[] CipherAes256 = Convert.FromHexString("31C1F2E6BF714350BE5805216AFC5AFF");
    public static readonly byte[] CipherChaCha20 = Convert.FromHexString("D6038A2B8B6F4CB5A524339A31DBB59A");
    public static readonly byte[] CipherTwofish = Convert.FromHexString("AD68F29F576F4BB9A36AD47AF965346C");
    public static readonly byte[] KdfAes = Convert.FromHexString("C9D9F39A628A4460BF740D08C18A4FEA");
    public static readonly byte[] KdfArgon2d = Convert.FromHexString("EF636DDF8C29444B91F7A9A403E30A0C");
    public static readonly byte[] KdfArgon2id = Convert.FromHexString("9E298B1956DB4773B23DFC3EC6F0A1E6");

    public const int InnerStreamSalsa20 = 2;
    public const int InnerStreamChaCha20 = 3;
    private static readonly byte[] Salsa20Iv = [0xE8, 0x30, 0x09, 0x4B, 0x97, 0x20, 0x5D, 0x2A];

    /// <summary>AES-KDF: <paramref name="rounds"/> AES-256-ECB encryptions of the composite key, then SHA-256.</summary>
    public static byte[] AesKdf(ReadOnlySpan<byte> compositeKey, byte[] seed, ulong rounds)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = seed;
        byte[] block = compositeKey.ToArray();
        try
        {
            // Both 16-byte halves in one call; ECB keeps them independent.
            using ICryptoTransform encryptor = aes.CreateEncryptor();
            for (ulong i = 0; i < rounds; i++)
                encryptor.TransformBlock(block, 0, 32, block, 0);

            return SHA256.HashData(block);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(block);
        }
    }

    public static byte[] Argon2(ReadOnlySpan<byte> compositeKey, bool argon2id, KdbxVariantDictionary parameters)
    {
        byte[] salt = parameters.Bytes("S") ?? throw new CredentialSourceException(CredentialError.UnsupportedFormat, "Argon2 without salt");
        ulong memory = parameters.UInt64("M") ?? 0;
        ulong iterations = parameters.UInt64("I") ?? 0;
        uint parallelism = parameters.UInt32("P") ?? 0;
        uint version = parameters.UInt32("V") ?? 0x13;
        if (memory < 8 * 1024 || memory / 1024 > int.MaxValue || iterations is 0 or > int.MaxValue || parallelism is 0 or > 64)
            throw new CredentialSourceException(CredentialError.UnsupportedFormat, "Argon2 parameters out of range");

        var builder = new Argon2Parameters.Builder(argon2id ? Argon2Parameters.Argon2id : Argon2Parameters.Argon2d)
            .WithVersion((int)version)
            .WithSalt(salt)
            .WithIterations((int)iterations)
            .WithMemoryAsKB((int)(memory / 1024))
            .WithParallelism((int)parallelism);
        if (parameters.Bytes("K") is { Length: > 0 } secret)
            builder.WithSecret(secret);
        if (parameters.Bytes("A") is { Length: > 0 } associated)
            builder.WithAdditional(associated);

        var generator = new Argon2BytesGenerator();
        generator.Init(builder.Build());
        byte[] password = compositeKey.ToArray();
        try
        {
            byte[] result = new byte[32];
            generator.GenerateBytes(password, result);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
        }
    }

    /// <summary>Decrypts the payload with the header's cipher (AES-256-CBC, ChaCha20, or Twofish-CBC).</summary>
    public static byte[] Decrypt(byte[] cipherId, byte[] key, byte[] iv, byte[] data)
    {
        try
        {
            if (cipherId.AsSpan().SequenceEqual(CipherAes256))
            {
                using var aes = Aes.Create();
                aes.Key = key;
                return aes.DecryptCbc(data, iv, PaddingMode.PKCS7);
            }

            if (cipherId.AsSpan().SequenceEqual(CipherChaCha20))
                return ChaCha20(key, iv, data);

            if (cipherId.AsSpan().SequenceEqual(CipherTwofish))
            {
                var cipher = new PaddedBufferedBlockCipher(new CbcBlockCipher(new TwofishEngine()), new Pkcs7Padding());
                cipher.Init(false, new ParametersWithIV(new KeyParameter(key), iv));
                return cipher.DoFinal(data);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidCipherTextException or DataLengthException)
        {
            // A wrong key shows up as bad padding (KDBX 3; KDBX 4 already failed the header HMAC).
            throw new CredentialSourceException(CredentialError.WrongMasterKey, null, ex);
        }

        throw new CredentialSourceException(CredentialError.UnsupportedFormat, $"cipher {Convert.ToHexString(cipherId)}");
    }

    public static byte[] Encrypt(byte[] cipherId, byte[] key, byte[] iv, byte[] data)
    {
        if (cipherId.AsSpan().SequenceEqual(CipherAes256))
        {
            using var aes = Aes.Create();
            aes.Key = key;
            return aes.EncryptCbc(data, iv, PaddingMode.PKCS7);
        }

        if (cipherId.AsSpan().SequenceEqual(CipherChaCha20))
            return ChaCha20(key, iv, data);

        throw new NotSupportedException("Only AES-256 and ChaCha20 are written.");
    }

    /// <summary>ChaCha20 as in RFC 7539 (12-byte nonce, counter from 0). Encrypt and decrypt are the same.</summary>
    public static byte[] ChaCha20(byte[] key, byte[] nonce, byte[] data)
    {
        var engine = new ChaCha7539Engine();
        engine.Init(true, new ParametersWithIV(new KeyParameter(key), nonce));
        byte[] output = new byte[data.Length];
        engine.ProcessBytes(data, 0, data.Length, output, 0);
        return output;
    }

    /// <summary>The stream that XORs the protected values in the XML (in document order).</summary>
    public static IStreamCipher InnerStream(int id, byte[] key)
    {
        switch (id)
        {
            case InnerStreamChaCha20:
            {
                byte[] hash = SHA512.HashData(key);
                var engine = new ChaCha7539Engine();
                engine.Init(true, new ParametersWithIV(new KeyParameter(hash, 0, 32), hash, 32, 12));
                return engine;
            }
            case InnerStreamSalsa20:
            {
                var engine = new Salsa20Engine();
                engine.Init(true, new ParametersWithIV(new KeyParameter(SHA256.HashData(key)), Salsa20Iv));
                return engine;
            }
            default:
                throw new CredentialSourceException(CredentialError.UnsupportedFormat, $"inner stream {id}");
        }
    }

    /// <summary>Key of HMAC block <paramref name="index"/> (KDBX 4); the header uses <see cref="ulong.MaxValue"/>.</summary>
    public static byte[] BlockHmacKey(byte[] hmacBaseKey, ulong index)
    {
        byte[] input = new byte[8 + hmacBaseKey.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(input, index);
        hmacBaseKey.CopyTo(input, 8);
        byte[] key = SHA512.HashData(input);
        CryptographicOperations.ZeroMemory(input);
        return key;
    }

    /// <summary>HMAC of one block: index, size, and data.</summary>
    public static byte[] BlockHmac(byte[] hmacBaseKey, ulong index, ReadOnlySpan<byte> data)
    {
        byte[] key = BlockHmacKey(hmacBaseKey, index);
        byte[] prefix = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(prefix, index);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(8), data.Length);
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        hmac.AppendData(prefix);
        hmac.AppendData(data);
        CryptographicOperations.ZeroMemory(key);
        return hmac.GetHashAndReset();
    }
}
