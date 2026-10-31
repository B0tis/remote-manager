using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace RemoteManager.App.Diagnostics;

/// <summary>
/// A minimal RFB 3.8 server for the self-test: VNC authentication (when a password is set), raw encoding, a
/// fixed test pattern (four colored quadrants), and a record of the key and pointer events it receives.
/// Loopback only; one client at a time.
/// </summary>
internal sealed class FakeVncServer : IDisposable
{
    public const int Width = 640, Height = 400;

    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly string? password;

    public FakeVncServer(string? password)
    {
        this.password = password;
        listener.Start();
        _ = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary>Key symbols with their down flag, in the order received.</summary>
    public ConcurrentQueue<(bool Down, uint Key)> Keys { get; } = new();

    public int PointerEvents;
    public int AuthFailures;
    public int Sessions;

    /// <summary>Color of the test pattern at a position (0xRRGGBB).</summary>
    public static uint ColorAt(int x, int y) => (x < Width / 2, y < Height / 2) switch
    {
        (true, true) => 0xE53935,   // red
        (false, true) => 0x43A047,  // green
        (true, false) => 0x1E88E5,  // blue
        _ => 0xFDD835,              // yellow
    };

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
    }

    private async Task AcceptLoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        await ServeAsync(client.GetStream(), stop.Token);
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or EndOfStreamException or ObjectDisposedException)
                    {
                        // Client went away.
                    }
                }
            });
        }
    }

    private async Task ServeAsync(NetworkStream stream, CancellationToken cancel)
    {
        await stream.WriteAsync("RFB 003.008\n"u8.ToArray(), cancel);
        await ReadExactAsync(stream, 12, cancel);

        if (password is null)
        {
            await stream.WriteAsync(new byte[] { 1, 1 }, cancel); // one type: None
            await ReadExactAsync(stream, 1, cancel);
            await stream.WriteAsync(new byte[4], cancel);
        }
        else
        {
            await stream.WriteAsync(new byte[] { 1, 2 }, cancel); // one type: VNC Authentication
            await ReadExactAsync(stream, 1, cancel);
            byte[] challenge = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
            await stream.WriteAsync(challenge, cancel);
            byte[] response = await ReadExactAsync(stream, 16, cancel);
            if (!response.AsSpan().SequenceEqual(VncAuthResponse(password, challenge)))
            {
                Interlocked.Increment(ref AuthFailures);
                byte[] reason = "Authentication failed"u8.ToArray();
                byte[] failure = new byte[8 + reason.Length];
                BinaryPrimitives.WriteUInt32BigEndian(failure, 1);
                BinaryPrimitives.WriteUInt32BigEndian(failure.AsSpan(4), (uint)reason.Length);
                reason.CopyTo(failure, 8);
                await stream.WriteAsync(failure, cancel);
                return;
            }

            await stream.WriteAsync(new byte[4], cancel);
        }

        await ReadExactAsync(stream, 1, cancel); // ClientInit (shared flag)
        Interlocked.Increment(ref Sessions);
        byte[] name = Encoding.UTF8.GetBytes("Remote-Manager Selbsttest");
        byte[] init = new byte[24 + name.Length];
        BinaryPrimitives.WriteUInt16BigEndian(init, Width);
        BinaryPrimitives.WriteUInt16BigEndian(init.AsSpan(2), Height);
        var format = PixelFormat.Default;
        format.Write(init.AsSpan(4));
        BinaryPrimitives.WriteUInt32BigEndian(init.AsSpan(20), (uint)name.Length);
        name.CopyTo(init, 24);
        await stream.WriteAsync(init, cancel);

        bool sentFull = false;
        while (!cancel.IsCancellationRequested)
        {
            byte type = (await ReadExactAsync(stream, 1, cancel))[0];
            switch (type)
            {
                case 0: // SetPixelFormat
                    format = PixelFormat.Read((await ReadExactAsync(stream, 19, cancel)).AsSpan(3));
                    break;
                case 2: // SetEncodings
                {
                    byte[] header = await ReadExactAsync(stream, 3, cancel);
                    await ReadExactAsync(stream, 4 * BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1)), cancel);
                    break;
                }
                case 3: // FramebufferUpdateRequest
                {
                    byte[] request = await ReadExactAsync(stream, 9, cancel);
                    if (request[0] == 0 || !sentFull)
                    {
                        await stream.WriteAsync(FullFrame(format), cancel);
                        sentFull = true;
                    }
                    else
                    {
                        // Nothing changed: an empty update after a moment keeps the client's loop calm.
                        await Task.Delay(200, cancel);
                        await stream.WriteAsync(new byte[] { 0, 0, 0, 0 }, cancel);
                    }

                    break;
                }
                case 4: // KeyEvent
                {
                    byte[] key = await ReadExactAsync(stream, 7, cancel);
                    Keys.Enqueue((key[0] != 0, BinaryPrimitives.ReadUInt32BigEndian(key.AsSpan(3))));
                    break;
                }
                case 5: // PointerEvent
                    await ReadExactAsync(stream, 5, cancel);
                    Interlocked.Increment(ref PointerEvents);
                    break;
                case 6: // ClientCutText
                {
                    byte[] header = await ReadExactAsync(stream, 7, cancel);
                    await ReadExactAsync(stream, (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(3)), cancel);
                    break;
                }
                default:
                    return; // Not advertised; a real server would also drop the client.
            }
        }
    }

    private static byte[] FullFrame(PixelFormat format)
    {
        int bytesPerPixel = format.BitsPerPixel / 8;
        byte[] message = new byte[4 + 12 + Width * Height * bytesPerPixel];
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(8), Width);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(10), Height);
        // Encoding 0 (raw) is already zero.
        int position = 16;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                format.WritePixel(message.AsSpan(position, bytesPerPixel), ColorAt(x, y));
                position += bytesPerPixel;
            }
        }

        return message;
    }

    /// <summary>VNC authentication: DES-encrypt the challenge with the password (8 bytes, each byte bit-reversed).</summary>
    private static byte[] VncAuthResponse(string password, byte[] challenge)
    {
        byte[] key = new byte[8];
        byte[] bytes = Encoding.Latin1.GetBytes(password);
        for (int i = 0; i < Math.Min(8, bytes.Length); i++)
            key[i] = ReverseBits(bytes[i]);

        var des = new DesEngine();
        des.Init(true, new KeyParameter(key));
        byte[] response = new byte[16];
        des.ProcessBlock(challenge, 0, response, 0);
        des.ProcessBlock(challenge, 8, response, 8);
        return response;
    }

    private static byte ReverseBits(byte value)
    {
        int result = 0;
        for (int i = 0; i < 8; i++)
            result |= ((value >> i) & 1) << (7 - i);
        return (byte)result;
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken cancel)
    {
        byte[] buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, cancel);
        return buffer;
    }

    /// <summary>The true-color pixel formats a client may ask for.</summary>
    private readonly record struct PixelFormat(byte BitsPerPixel, bool BigEndian, ushort RedMax, ushort GreenMax, ushort BlueMax, byte RedShift, byte GreenShift, byte BlueShift)
    {
        public static PixelFormat Default => new(32, false, 255, 255, 255, 16, 8, 0);

        public static PixelFormat Read(ReadOnlySpan<byte> data) => new(data[0], data[2] != 0,
            BinaryPrimitives.ReadUInt16BigEndian(data[4..]), BinaryPrimitives.ReadUInt16BigEndian(data[6..]), BinaryPrimitives.ReadUInt16BigEndian(data[8..]),
            data[10], data[11], data[12]);

        public void Write(Span<byte> data)
        {
            data[0] = BitsPerPixel;
            data[1] = 24;
            data[2] = BigEndian ? (byte)1 : (byte)0;
            data[3] = 1; // true color
            BinaryPrimitives.WriteUInt16BigEndian(data[4..], RedMax);
            BinaryPrimitives.WriteUInt16BigEndian(data[6..], GreenMax);
            BinaryPrimitives.WriteUInt16BigEndian(data[8..], BlueMax);
            data[10] = RedShift;
            data[11] = GreenShift;
            data[12] = BlueShift;
        }

        public void WritePixel(Span<byte> target, uint rgb)
        {
            uint r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
            uint value = (r * RedMax / 255 << RedShift) | (g * GreenMax / 255 << GreenShift) | (b * BlueMax / 255 << BlueShift);
            switch (BitsPerPixel)
            {
                case 32 when BigEndian: BinaryPrimitives.WriteUInt32BigEndian(target, value); break;
                case 32: BinaryPrimitives.WriteUInt32LittleEndian(target, value); break;
                case 16 when BigEndian: BinaryPrimitives.WriteUInt16BigEndian(target, (ushort)value); break;
                case 16: BinaryPrimitives.WriteUInt16LittleEndian(target, (ushort)value); break;
                default: target[0] = (byte)value; break;
            }
        }
    }
}
