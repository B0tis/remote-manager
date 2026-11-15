using System.Buffers.Binary;
using System.Net.Sockets;

namespace RemoteManager.Core.Rdp;

/// <summary>
/// Asks an RDP server, without logging on, whether it insists on network level authentication (CredSSP). The
/// client offers plain TLS only; a server that requires NLA refuses that with HYBRID_REQUIRED_BY_SERVER. Used to
/// know whether a logon that went through really checked the password before the desktop appeared: a server that
/// also takes TLS (xrdp by default) shows its own login screen instead, whatever password was sent.
/// </summary>
public static class RdpSecurityProbe
{
    private const byte NegotiationFailure = 3;
    private const uint HybridRequiredByServer = 5;
    private const uint ProtocolTls = 1;

    /// <summary>True when the server refuses a connection without NLA, false when it accepts one, null when unclear.</summary>
    public static async Task<bool?> RequiresNlaAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(ConnectionRequest(ProtocolTls), linked.Token).ConfigureAwait(false);

            byte[] answer = new byte[19];
            int read = 0;
            while (read < answer.Length)
            {
                int n = await stream.ReadAsync(answer.AsMemory(read), linked.Token).ConfigureAwait(false);
                if (n == 0)
                    break;
                read += n;
            }

            return Interpret(answer.AsSpan(0, read));
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>TPKT + X.224 connection request with an RDP negotiation request for <paramref name="protocols"/>.</summary>
    internal static byte[] ConnectionRequest(uint protocols)
    {
        byte[] packet = new byte[19];
        packet[0] = 3;                                         // TPKT version
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 19);
        packet[4] = 14;                                        // X.224 length indicator
        packet[5] = 0xE0;                                      // connection request
        packet[11] = 1;                                        // TYPE_RDP_NEG_REQ
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(13), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(15), protocols);
        return packet;
    }

    /// <summary>The server's answer: a failure "NLA required" is true, a selected protocol false, anything else null.</summary>
    internal static bool? Interpret(ReadOnlySpan<byte> answer)
    {
        if (answer.Length < 19 || answer[0] != 3)
            return null;
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(answer[15..]);
        return answer[11] switch
        {
            NegotiationFailure => value == HybridRequiredByServer,
            2 => false,                                        // TYPE_RDP_NEG_RSP: the server took TLS
            _ => null,
        };
    }
}
