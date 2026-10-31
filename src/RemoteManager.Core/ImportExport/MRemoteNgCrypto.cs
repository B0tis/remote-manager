using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RemoteManager.Core.ImportExport;

/// <summary>
/// mRemoteNG's password encryption, to take passwords over on import. Current files (EncryptionEngine
/// attribute) use AES-256-GCM: base64 of salt(16) | nonce(16) | ciphertext | tag(16), key =
/// PBKDF2-SHA1(master password, salt, KdfIterations), the salt is also the associated data. Older
/// files use AES-CBC with key MD5(master password) and the IV in front. Without a custom master
/// password the master password is "mR3m".
/// .NET's AesGcm only accepts 12-byte nonces, so GCM is done here on top of AES-ECB (decrypt only
/// what the file holds; nothing is kept).
/// </summary>
internal static class MRemoteNgCrypto
{
    public const string DefaultPassword = "mR3m";

    /// <summary>Decrypts one value; null when it is not valid for <paramref name="password"/>.</summary>
    public static string? Decrypt(string cipherText, string password, bool gcm, int iterations)
    {
        if (string.IsNullOrEmpty(cipherText))
            return null;
        try
        {
            byte[] data = Convert.FromBase64String(cipherText);
            return gcm ? DecryptGcm(data, password, iterations) : DecryptCbc(data, password);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Encrypts like mRemoteNG 1.76+ (used by the tests to build sample files).</summary>
    public static string EncryptGcm(string plainText, string password, int iterations)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16), nonce = RandomNumberGenerator.GetBytes(16);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA1, 32);
        byte[] plain = Encoding.UTF8.GetBytes(plainText);
        byte[] cipher = Gcm.Crypt(key, nonce, plain);
        byte[] tag = Gcm.Tag(key, nonce, salt, cipher);
        return Convert.ToBase64String([.. salt, .. nonce, .. cipher, .. tag]);
    }

    private static string? DecryptGcm(byte[] data, string password, int iterations)
    {
        if (data.Length < 48)
            return null;
        byte[] salt = data[..16], nonce = data[16..32], cipher = data[32..^16], tag = data[^16..];
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Math.Max(1, iterations), HashAlgorithmName.SHA1, 32);
        if (!CryptographicOperations.FixedTimeEquals(Gcm.Tag(key, nonce, salt, cipher), tag))
            return null;
        byte[] plain = Gcm.Crypt(key, nonce, cipher);
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    private static string? DecryptCbc(byte[] data, string password)
    {
        if (data.Length < 32)
            return null;
        using var aes = Aes.Create();
        aes.Key = MD5.HashData(Encoding.UTF8.GetBytes(password));
        byte[] plain = aes.DecryptCbc(data.AsSpan(16), data.AsSpan(0, 16), PaddingMode.PKCS7);
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    /// <summary>AES-GCM (NIST SP 800-38D) for any nonce length, built on AES-ECB.</summary>
    internal static class Gcm
    {
        /// <summary>Encrypts or decrypts (GCTR from inc32(J0)).</summary>
        public static byte[] Crypt(byte[] key, byte[] nonce, byte[] input)
        {
            using var aes = Aes.Create();
            aes.Key = key;
            byte[] counter = J0(aes, nonce);
            byte[] output = new byte[input.Length];
            byte[] stream = new byte[16];
            for (int offset = 0; offset < input.Length; offset += 16)
            {
                Increment(counter);
                aes.EncryptEcb(counter, stream, PaddingMode.None);
                int count = Math.Min(16, input.Length - offset);
                for (int i = 0; i < count; i++)
                    output[offset + i] = (byte)(input[offset + i] ^ stream[i]);
            }

            return output;
        }

        public static byte[] Tag(byte[] key, byte[] nonce, byte[] associated, byte[] cipher)
        {
            using var aes = Aes.Create();
            aes.Key = key;
            byte[] h = HashKey(aes);
            byte[] s = new byte[16];
            Absorb(s, h, associated);
            Absorb(s, h, cipher);
            byte[] lengths = new byte[16];
            BinaryPrimitives.WriteUInt64BigEndian(lengths, (ulong)associated.Length * 8);
            BinaryPrimitives.WriteUInt64BigEndian(lengths.AsSpan(8), (ulong)cipher.Length * 8);
            Absorb(s, h, lengths);

            byte[] tag = new byte[16];
            aes.EncryptEcb(J0(aes, nonce), tag, PaddingMode.None);
            for (int i = 0; i < 16; i++)
                tag[i] ^= s[i];
            return tag;
        }

        private static byte[] HashKey(Aes aes)
        {
            byte[] h = new byte[16];
            aes.EncryptEcb(new byte[16], h, PaddingMode.None);
            return h;
        }

        private static byte[] J0(Aes aes, byte[] nonce)
        {
            if (nonce.Length == 12)
                return [.. nonce, 0, 0, 0, 1];

            byte[] h = HashKey(aes);
            byte[] y = new byte[16];
            Absorb(y, h, nonce);
            byte[] lengths = new byte[16];
            BinaryPrimitives.WriteUInt64BigEndian(lengths.AsSpan(8), (ulong)nonce.Length * 8);
            Absorb(y, h, lengths);
            return y;
        }

        /// <summary>GHASH over <paramref name="data"/> zero-padded to whole blocks.</summary>
        private static void Absorb(byte[] y, byte[] h, byte[] data)
        {
            for (int offset = 0; offset < data.Length; offset += 16)
            {
                int count = Math.Min(16, data.Length - offset);
                for (int i = 0; i < count; i++)
                    y[i] ^= data[offset + i];
                Multiply(y, h);
            }
        }

        /// <summary>x = x · h in GF(2^128) (bit-reflected, as in the standard).</summary>
        private static void Multiply(byte[] x, byte[] h)
        {
            ulong zHigh = 0, zLow = 0;
            ulong vHigh = BinaryPrimitives.ReadUInt64BigEndian(h), vLow = BinaryPrimitives.ReadUInt64BigEndian(h.AsSpan(8));
            ulong xHigh = BinaryPrimitives.ReadUInt64BigEndian(x), xLow = BinaryPrimitives.ReadUInt64BigEndian(x.AsSpan(8));
            for (int i = 0; i < 128; i++)
            {
                ulong bit = i < 64 ? (xHigh >> (63 - i)) & 1 : (xLow >> (127 - i)) & 1;
                if (bit == 1)
                {
                    zHigh ^= vHigh;
                    zLow ^= vLow;
                }

                bool lsb = (vLow & 1) == 1;
                vLow = (vLow >> 1) | (vHigh << 63);
                vHigh >>= 1;
                if (lsb)
                    vHigh ^= 0xE100000000000000UL;
            }

            BinaryPrimitives.WriteUInt64BigEndian(x, zHigh);
            BinaryPrimitives.WriteUInt64BigEndian(x.AsSpan(8), zLow);
        }

        private static void Increment(byte[] counter)
        {
            uint value = BinaryPrimitives.ReadUInt32BigEndian(counter.AsSpan(12));
            BinaryPrimitives.WriteUInt32BigEndian(counter.AsSpan(12), value + 1);
        }
    }
}
