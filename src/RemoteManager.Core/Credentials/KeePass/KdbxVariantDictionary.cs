using System.Buffers.Binary;
using System.Text;

namespace RemoteManager.Core.Credentials.KeePass;

/// <summary>The typed key/value list of KDBX 4 headers (KDF parameters).</summary>
internal sealed class KdbxVariantDictionary
{
    private const ushort Version = 0x0100;
    private const byte TypeUInt32 = 0x04, TypeUInt64 = 0x05, TypeBool = 0x08, TypeInt32 = 0x0C, TypeInt64 = 0x0D, TypeString = 0x18, TypeBytes = 0x42;

    private readonly Dictionary<string, (byte Type, byte[] Value)> items = [];

    public byte[]? Bytes(string key) => items.TryGetValue(key, out var item) ? item.Value : null;

    public uint? UInt32(string key) => items.TryGetValue(key, out var item) && item.Value.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(item.Value) : null;

    public ulong? UInt64(string key) => items.TryGetValue(key, out var item) && item.Value.Length == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(item.Value) : null;

    public KdbxVariantDictionary Set(string key, byte[] value)
    {
        items[key] = (TypeBytes, value);
        return this;
    }

    public KdbxVariantDictionary Set(string key, uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        items[key] = (TypeUInt32, bytes);
        return this;
    }

    public KdbxVariantDictionary Set(string key, ulong value)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        items[key] = (TypeUInt64, bytes);
        return this;
    }

    public static KdbxVariantDictionary Parse(ReadOnlySpan<byte> data)
    {
        var result = new KdbxVariantDictionary();
        if (data.Length < 2 || (BinaryPrimitives.ReadUInt16LittleEndian(data) & 0xFF00) != (Version & 0xFF00))
            throw new CredentialSourceException(CredentialError.UnsupportedFormat, "KDF parameters");

        int position = 2;
        while (position < data.Length)
        {
            byte type = data[position++];
            if (type == 0)
                return result;
            string key = Encoding.UTF8.GetString(ReadSized(data, ref position));
            byte[] value = ReadSized(data, ref position).ToArray();
            if (type is TypeUInt32 or TypeUInt64 or TypeBool or TypeInt32 or TypeInt64 or TypeString or TypeBytes)
                result.items[key] = (type, value);
        }

        throw new CredentialSourceException(CredentialError.UnsupportedFormat, "KDF parameters");
    }

    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(number, Version);
        stream.Write(number[..2]);
        foreach ((string key, (byte type, byte[] value)) in items)
        {
            stream.WriteByte(type);
            byte[] name = Encoding.UTF8.GetBytes(key);
            BinaryPrimitives.WriteInt32LittleEndian(number, name.Length);
            stream.Write(number);
            stream.Write(name);
            BinaryPrimitives.WriteInt32LittleEndian(number, value.Length);
            stream.Write(number);
            stream.Write(value);
        }

        stream.WriteByte(0);
        return stream.ToArray();
    }

    private static ReadOnlySpan<byte> ReadSized(ReadOnlySpan<byte> data, ref int position)
    {
        if (position + 4 > data.Length)
            throw new CredentialSourceException(CredentialError.UnsupportedFormat, "KDF parameters");
        int length = BinaryPrimitives.ReadInt32LittleEndian(data[position..]);
        position += 4;
        if (length < 0 || position + length > data.Length)
            throw new CredentialSourceException(CredentialError.UnsupportedFormat, "KDF parameters");
        ReadOnlySpan<byte> value = data.Slice(position, length);
        position += length;
        return value;
    }
}
