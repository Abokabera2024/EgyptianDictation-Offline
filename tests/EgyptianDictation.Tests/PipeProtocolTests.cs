using System.IO.Pipes;
using System.Text;
using EgyptianDictation.Contracts;
using EgyptianDictation.Core.Ipc;

namespace EgyptianDictation.Tests;

public sealed class PipeProtocolTests
{
    [Fact]
    public async Task RoundTripsRequestAndResponseOverNamedPipe()
    {
        var pipeName = $"EgyptianDictation.Tests.{Guid.NewGuid():N}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = new NamedPipeJsonServer(pipeName, (message, _) => Task.FromResult<MessageEnvelope?>(new()
        {
            Type = MessageTypes.EngineState,
            SessionId = message.SessionId,
            SequenceNumber = message.SequenceNumber,
            PayloadJson = PipeMessageCodec.Payload(new EngineStateEvent { State = "ready" })
        }));
        var serverTask = server.RunSingleClientAsync(timeout.Token);

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(client, Encoding.UTF8, false, 4096, true);
        var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        var codec = new PipeMessageCodec();
        await writer.WriteLineAsync(codec.Serialize(new MessageEnvelope
        {
            Type = MessageTypes.Ping,
            SessionId = "session-1",
            SequenceNumber = 7
        }));

        var responseLine = await reader.ReadLineAsync(timeout.Token);
        var response = codec.Deserialize(responseLine!);
        var state = PipeMessageCodec.ReadPayload<EngineStateEvent>(response);

        Assert.Equal(MessageTypes.EngineState, response.Type);
        Assert.Equal("session-1", response.SessionId);
        Assert.Equal(7, response.SequenceNumber);
        Assert.Equal("ready", state!.State);
        await writer.DisposeAsync();
        reader.Dispose();
        client.Close();
        await serverTask;
    }

    [Fact]
    public void RejectsWrongProtocolVersion()
    {
        var codec = new PipeMessageCodec();
        var message = new MessageEnvelope { ProtocolVersion = 99, Type = MessageTypes.Ping };
        Assert.Throws<InvalidDataException>(() => codec.Serialize(message));
    }

    [Fact]
    public async Task ClientDisconnectDoesNotFaultServer()
    {
        var pipeName = $"EgyptianDictation.Tests.{Guid.NewGuid():N}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = new NamedPipeJsonServer(pipeName, (message, _) => Task.FromResult<MessageEnvelope?>(new()
        {
            Type = MessageTypes.EngineState,
            SessionId = message.SessionId
        }));
        var task = server.RunSingleClientAsync(timeout.Token);
        await using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(timeout.Token);
            var bytes = Encoding.UTF8.GetBytes("{\"type\":\"ping\"}");
            await client.WriteAsync(bytes, timeout.Token);
        }
        await task.WaitAsync(timeout.Token);

        var next = server.RunSingleClientAsync(timeout.Token);
        await using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(timeout.Token);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, false, 4096, true);
            await writer.WriteLineAsync(new PipeMessageCodec().Serialize(new MessageEnvelope { Type = MessageTypes.Ping }));
            Assert.NotNull(await reader.ReadLineAsync(timeout.Token));
        }
        await next.WaitAsync(timeout.Token);
    }
}
