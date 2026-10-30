using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace RemoteManager.Core.Ssh;

/// <summary>A public key an agent holds; the private key never leaves the agent.</summary>
/// <param name="PublicKeyBlob">The key in SSH wire format (string type, then the key fields).</param>
public sealed record SshAgentIdentity(byte[] PublicKeyBlob, string Comment, SshAgent Agent)
{
    /// <summary>"ssh-ed25519", "ssh-rsa", "ecdsa-sha2-nistp256", ...</summary>
    public string KeyType => SshWire.ReadString(PublicKeyBlob, 0, out _);
}

/// <summary>
/// Client side of the SSH agent protocol (draft-miller-ssh-agent): list keys and let the agent sign.
/// <see cref="OpenSshAgent"/> talks to the Windows OpenSSH agent over its named pipe, <see cref="PageantAgent"/>
/// to PuTTY's Pageant over WM_COPYDATA. SSH.NET has no agent support, so this plugs in as an
/// <see cref="Renci.SshNet.IPrivateKeySource"/> (<see cref="SshAgentKeySource"/>).
/// </summary>
public abstract class SshAgent
{
    private const byte RequestIdentities = 11;
    private const byte IdentitiesAnswer = 12;
    private const byte SignRequest = 13;
    private const byte SignResponse = 14;

    /// <summary>Signature flags for RSA keys (the agent picks the hash).</summary>
    public const uint RsaSha2_256 = 2;
    public const uint RsaSha2_512 = 4;

    /// <summary>Name for messages ("OpenSSH-Agent", "Pageant").</summary>
    public abstract string Name { get; }

    /// <summary>Quick check whether the agent is running (no request is sent).</summary>
    public abstract bool IsRunning { get; }

    /// <summary>The keys the agent offers; empty when it holds none.</summary>
    public IReadOnlyList<SshAgentIdentity> GetIdentities()
    {
        byte[] reply = Exchange([RequestIdentities]);
        if (reply.Length == 0 || reply[0] != IdentitiesAnswer)
            throw new SshAgentException($"{Name}: unexpected answer {(reply.Length == 0 ? "(empty)" : reply[0])} to the key list request.");

        int offset = 1;
        uint count = SshWire.ReadUInt32(reply, ref offset);
        var identities = new List<SshAgentIdentity>((int)Math.Min(count, 64));
        for (uint i = 0; i < count; i++)
        {
            byte[] blob = SshWire.ReadBytes(reply, ref offset);
            string comment = Encoding.UTF8.GetString(SshWire.ReadBytes(reply, ref offset));
            identities.Add(new SshAgentIdentity(blob, comment, this));
        }

        return identities;
    }

    /// <summary>Signs <paramref name="data"/> with the key behind <paramref name="publicKeyBlob"/>; returns the SSH signature blob (string algorithm, string signature).</summary>
    public byte[] Sign(byte[] publicKeyBlob, byte[] data, uint flags)
    {
        using var request = new MemoryStream();
        request.WriteByte(SignRequest);
        SshWire.WriteBytes(request, publicKeyBlob);
        SshWire.WriteBytes(request, data);
        SshWire.WriteUInt32(request, flags);
        byte[] reply = Exchange(request.ToArray());
        if (reply.Length == 0 || reply[0] != SignResponse)
            throw new SshAgentException($"{Name}: the agent refused to sign.");

        int offset = 1;
        return SshWire.ReadBytes(reply, ref offset);
    }

    /// <summary>Sends one message (type byte plus payload, without the length) and returns the answer the same way.</summary>
    public byte[] Exchange(byte[] message)
    {
        byte[] framed = new byte[4 + message.Length];
        BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)message.Length);
        message.CopyTo(framed, 4);
        return Transact(framed);
    }

    /// <summary>Sends a framed request (uint32 length first) and returns the answer without its length.</summary>
    protected abstract byte[] Transact(byte[] framedRequest);

    /// <summary>Reads one framed answer from a stream.</summary>
    protected static byte[] ReadFramed(Stream stream)
    {
        Span<byte> header = stackalloc byte[4];
        stream.ReadExactly(header);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > 256 * 1024)
            throw new SshAgentException($"Agent answer too long ({length} bytes).");
        byte[] body = new byte[length];
        stream.ReadExactly(body);
        return body;
    }

    /// <summary>Every agent the app knows, running or not: Windows OpenSSH agent first, then Pageant.</summary>
    public static IReadOnlyList<SshAgent> All { get; } = [new OpenSshAgent(), new PageantAgent()];
}

public sealed class SshAgentException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The Windows OpenSSH agent (service "ssh-agent"), or whatever named pipe SSH_AUTH_SOCK names.</summary>
public sealed class OpenSshAgent : SshAgent
{
    public const string DefaultPipe = "openssh-ssh-agent";
    private readonly string pipeName;

    public OpenSshAgent(string? pipeName = null) => this.pipeName = pipeName ?? PipeFromEnvironment() ?? DefaultPipe;

    public override string Name => "OpenSSH-Agent";

    public string PipeName => pipeName;

    // Listing \\.\pipe\ does not connect to the pipe (File.Exists would use up a pipe instance).
    public override bool IsRunning =>
        OperatingSystem.IsWindows() && Directory.EnumerateFiles(@"\\.\pipe\").Any(p => string.Equals(Path.GetFileName(p), pipeName, StringComparison.OrdinalIgnoreCase));

    protected override byte[] Transact(byte[] framedRequest)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
        try
        {
            pipe.Connect(2000);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            throw new SshAgentException($"{Name}: pipe {pipeName} is not available ({ex.Message}).", ex);
        }

        pipe.Write(framedRequest);
        pipe.Flush();
        return ReadFramed(pipe);
    }

    private static string? PipeFromEnvironment()
    {
        const string prefix = @"\\.\pipe\";
        string? socket = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
        return socket is not null && socket.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? socket[prefix.Length..] : null;
    }
}

/// <summary>
/// PuTTY's Pageant through its classic interface: the request goes into a named shared memory
/// block whose name is sent with WM_COPYDATA; Pageant writes the answer into the same block.
/// </summary>
public sealed class PageantAgent : SshAgent
{
    private const int MaxMessage = 8192;
    private static readonly nint AgentCopyDataId = unchecked((nint)0x804e50ba);
    private const uint WmCopyData = 0x004A;

    public override string Name => "Pageant";

    public override bool IsRunning => OperatingSystem.IsWindows() && FindWindow("Pageant", "Pageant") != 0;

    protected override byte[] Transact(byte[] framedRequest)
    {
        if (!OperatingSystem.IsWindows())
            throw new SshAgentException("Pageant is only available on Windows.");
        nint window = FindWindow("Pageant", "Pageant");
        if (window == 0)
            throw new SshAgentException("Pageant is not running.");
        if (framedRequest.Length > MaxMessage)
            throw new SshAgentException("Request too long for Pageant.");

        string mapName = $"PageantRequest{Environment.ProcessId:x8}{Random.Shared.Next():x8}";
        using MemoryMappedFile map = MemoryMappedFile.CreateNew(mapName, MaxMessage);
        using MemoryMappedViewAccessor view = map.CreateViewAccessor(0, MaxMessage);
        view.WriteArray(0, framedRequest, 0, framedRequest.Length);

        byte[] name = Encoding.ASCII.GetBytes(mapName + "\0");
        GCHandle pinned = GCHandle.Alloc(name, GCHandleType.Pinned);
        try
        {
            var data = new CopyDataStruct { Id = AgentCopyDataId, Length = name.Length, Data = pinned.AddrOfPinnedObject() };
            // Never block for good: a busy or hung Pageant (dialog open) must not freeze the connect.
            // 30 s leaves time for a confirmation prompt (pageant --confirm).
            if (SendMessageTimeout(window, WmCopyData, 0, ref data, SmtoAbortIfHung, 30_000, out nint result) == 0 || result == 0)
                throw new SshAgentException("Pageant did not answer (it may be busy, waiting for a confirmation, or refusing the request).");
        }
        finally
        {
            pinned.Free();
        }

        uint length = BinaryPrimitives.ReverseEndianness(view.ReadUInt32(0));
        if (length > MaxMessage - 4)
            throw new SshAgentException("Pageant answer too long.");
        byte[] body = new byte[length];
        view.ReadArray(4, body, 0, body.Length);
        return body;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public nint Id;
        public int Length;
        public nint Data;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
    private static extern nint FindWindow(string className, string windowName);

    private const uint SmtoAbortIfHung = 0x0002;

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, ref CopyDataStruct lParam, uint flags, uint timeout, out nint result);
}

/// <summary>Big-endian uint32 and length-prefixed strings of the SSH wire format.</summary>
public static class SshWire
{
    public static uint ReadUInt32(byte[] buffer, ref int offset)
    {
        if (offset + 4 > buffer.Length)
            throw new SshAgentException("Truncated agent message.");
        uint value = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset));
        offset += 4;
        return value;
    }

    public static byte[] ReadBytes(byte[] buffer, ref int offset)
    {
        uint length = ReadUInt32(buffer, ref offset);
        if (length > buffer.Length - offset)
            throw new SshAgentException("Truncated agent message.");
        byte[] value = buffer.AsSpan(offset, (int)length).ToArray();
        offset += (int)length;
        return value;
    }

    public static string ReadString(byte[] buffer, int offset, out int next)
    {
        byte[] bytes = ReadBytes(buffer, ref offset);
        next = offset;
        return Encoding.ASCII.GetString(bytes);
    }

    public static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    public static void WriteBytes(Stream stream, ReadOnlySpan<byte> value)
    {
        WriteUInt32(stream, (uint)value.Length);
        stream.Write(value);
    }

    public static void WriteString(Stream stream, string value) => WriteBytes(stream, Encoding.UTF8.GetBytes(value));
}
