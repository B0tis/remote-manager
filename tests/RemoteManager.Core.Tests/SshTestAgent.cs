using System.Buffers.Binary;
using System.IO.Pipes;
using RemoteManager.Core.Ssh;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace RemoteManager.Core.Tests;

/// <summary>
/// A working SSH agent on a private named pipe: lists the given keys and signs with them, like the
/// Windows OpenSSH agent would. Lets the tests drive <see cref="OpenSshAgent"/> end to end.
/// </summary>
internal sealed class SshTestAgent : IDisposable
{
    private readonly IReadOnlyList<(IPrivateKeySource Key, string Comment)> keys;
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;

    public SshTestAgent(params (IPrivateKeySource Key, string Comment)[] keys)
    {
        this.keys = keys;
        PipeName = $"rm-test-agent-{Guid.NewGuid():N}";
        loop = Task.Run(ServeAsync);
    }

    public string PipeName { get; }
    public int SignRequests { get; private set; }
    public List<uint> SignFlags { get; } = [];

    public OpenSshAgent Client => new(PipeName);

    private async Task ServeAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(stop.Token);
                byte[] header = new byte[4];
                await pipe.ReadExactlyAsync(header, stop.Token);
                byte[] body = new byte[BinaryPrimitives.ReadUInt32BigEndian(header)];
                await pipe.ReadExactlyAsync(body, stop.Token);
                byte[] answer = Handle(body);
                byte[] framed = new byte[4 + answer.Length];
                BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)answer.Length);
                answer.CopyTo(framed, 4);
                await pipe.WriteAsync(framed, stop.Token);
                await pipe.FlushAsync(stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // Client went away; serve the next one.
            }
        }
    }

    private byte[] Handle(byte[] request)
    {
        using var answer = new MemoryStream();
        switch (request[0])
        {
            case 11:
                answer.WriteByte(12);
                SshWire.WriteUInt32(answer, (uint)keys.Count);
                foreach ((IPrivateKeySource key, string comment) in keys)
                {
                    SshWire.WriteBytes(answer, key.HostKeyAlgorithms.First().Data);
                    SshWire.WriteString(answer, comment);
                }

                break;
            case 13:
                int offset = 1;
                byte[] blob = SshWire.ReadBytes(request, ref offset);
                byte[] data = SshWire.ReadBytes(request, ref offset);
                uint flags = SshWire.ReadUInt32(request, ref offset);
                SignRequests++;
                SignFlags.Add(flags);
                IPrivateKeySource? match = keys.Select(k => k.Key).FirstOrDefault(k => k.HostKeyAlgorithms.First().Data.AsSpan().SequenceEqual(blob));
                if (match is null)
                {
                    answer.WriteByte(5); // SSH_AGENT_FAILURE
                    break;
                }

                string wanted = (flags & SshAgent.RsaSha2_512) != 0 ? "rsa-sha2-512" : (flags & SshAgent.RsaSha2_256) != 0 ? "rsa-sha2-256" : "";
                HostAlgorithm algorithm = match.HostKeyAlgorithms.FirstOrDefault(a => a.Name == wanted) ?? match.HostKeyAlgorithms.First();
                answer.WriteByte(14);
                SshWire.WriteBytes(answer, algorithm.Sign(data));
                break;
            default:
                answer.WriteByte(5);
                break;
        }

        return answer.ToArray();
    }

    public void Dispose()
    {
        stop.Cancel();
        try
        {
            loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        stop.Dispose();
    }
}
