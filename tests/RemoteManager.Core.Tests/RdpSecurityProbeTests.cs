using System.Net;
using System.Net.Sockets;
using RemoteManager.Core.Rdp;

namespace RemoteManager.Core.Tests;

public sealed class RdpSecurityProbeTests
{
    [Fact]
    public void ConnectionRequestOffersOnlyWhatIsAsked()
    {
        byte[] packet = RdpSecurityProbe.ConnectionRequest(1);
        Assert.Equal(19, packet.Length);
        Assert.Equal([3, 0, 0, 19, 14, 0xE0], packet[..6]);
        Assert.Equal(1, packet[11]);           // negotiation request
        Assert.Equal([1, 0, 0, 0], packet[15..]); // protocols: TLS only
    }

    [Theory]
    [InlineData(3, 5u, true)]   // failure: NLA required by the server
    [InlineData(3, 1u, false)]  // failure for another reason: not "NLA required"
    [InlineData(2, 1u, false)]  // the server took TLS
    [InlineData(1, 0u, null)]   // not an answer to a request
    public void AnswerIsInterpreted(byte type, uint value, bool? expected)
    {
        byte[] answer = new byte[19];
        answer[0] = 3;
        answer[11] = type;
        BitConverter.GetBytes(value).CopyTo(answer, 15);
        Assert.Equal(expected, RdpSecurityProbe.Interpret(answer));
        Assert.Null(RdpSecurityProbe.Interpret(answer.AsSpan(0, 10)));
    }

    [Fact]
    public async Task ServerThatRequiresNlaIsRecognized()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            NetworkStream stream = client.GetStream();
            byte[] request = new byte[19];
            await stream.ReadExactlyAsync(request, TestContext.Current.CancellationToken);
            byte[] answer = new byte[19];
            answer[0] = 3;
            answer[11] = 3;
            answer[15] = 5;
            await stream.WriteAsync(answer, TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        bool? nla = await RdpSecurityProbe.RequiresNlaAsync("127.0.0.1", port, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await server;
        Assert.True(nla);
    }

    [Fact]
    public async Task UnreachableServerIsUnclear()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Assert.Null(await RdpSecurityProbe.RequiresNlaAsync("127.0.0.1", port, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
    }
}
