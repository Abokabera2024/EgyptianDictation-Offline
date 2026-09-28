using System.IO.Pipes;
using System.Text;
using EgyptianDictation.Contracts;

namespace EgyptianDictation.Core.Ipc;

public sealed class NamedPipeJsonServer
{
    private readonly string _pipeName;
    private readonly PipeMessageCodec _codec = new();
    private readonly Func<MessageEnvelope, CancellationToken, Task<MessageEnvelope?>> _handler;

    public NamedPipeJsonServer(
        string pipeName,
        Func<MessageEnvelope, CancellationToken, Task<MessageEnvelope?>> handler)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? throw new ArgumentException("Pipe name is required.", nameof(pipeName))
            : pipeName;
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public async Task RunSingleClientAsync(CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true
            };
            while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    break;

                MessageEnvelope? response;
                try
                {
                    response = await _handler(_codec.Deserialize(line), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log(ex);
                    response = new MessageEnvelope
                    {
                        Type = MessageTypes.Error,
                        SessionId = string.Empty,
                        PayloadJson = PipeMessageCodec.Payload(new { code = "invalid_request", message = ex.Message })
                    };
                }

                if (response is not null)
                    await writer.WriteLineAsync(_codec.Serialize(response)).ConfigureAwait(false);
            }
        }
        catch (IOException) when (!cancellationToken.IsCancellationRequested) { /* The client disconnected; accept the next one. */ }
        catch (ObjectDisposedException) when (!cancellationToken.IsCancellationRequested) { }
    }

    private static void Log(Exception ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "EgyptianDictation-host-errors.log"),
                $"{DateTime.UtcNow:o} {ex}{Environment.NewLine}");
        }
        catch { }
    }
}
