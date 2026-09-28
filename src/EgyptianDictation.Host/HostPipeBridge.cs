using System.IO;
using EgyptianDictation.Contracts;
using EgyptianDictation.Core.Ipc;

namespace EgyptianDictation.Host;

public sealed class HostPipeBridge : IAsyncDisposable
{
    private readonly MicrophoneDictationController _controller;
    private readonly TranscriptOutbox _commits = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _serverLoop;
    private readonly string _pipeName;
    private readonly object _requestGate = new();
    private long _sequence;
    private string? _activeSession;
    private Task? _startTask;
    private string? _startError;

    public HostPipeBridge(MicrophoneDictationController controller, string? pipeName = null)
    {
        _controller = controller;
        _pipeName = pipeName ?? Environment.GetEnvironmentVariable("EGYPTIAN_DICTATION_PIPE_NAME") ?? Protocol.DefaultPipeName;
        _controller.TextCommitted += OnTextCommitted;
        _serverLoop = Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RunServerLoopAsync(_lifetime.Token)));
    }

    public async ValueTask DisposeAsync()
    {
        _controller.TextCommitted -= OnTextCommitted;
        _lifetime.Cancel();
        try { await _serverLoop; } catch (OperationCanceledException) { }
        _lifetime.Dispose();
    }

    private void OnTextCommitted(string sessionId, string text, Core.Asr.AsrTranscriptionResult metrics, bool newParagraph) =>
        _commits.Enqueue(sessionId, text, metrics.AudioSeconds, newParagraph);

    private async Task RunServerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var server = new NamedPipeJsonServer(_pipeName, HandleAsync);
            try { await server.RunSingleClientAsync(cancellationToken); }
            catch (IOException) when (!cancellationToken.IsCancellationRequested) { await Task.Delay(250, cancellationToken); }
        }
    }

    private Task<MessageEnvelope?> HandleAsync(MessageEnvelope request, CancellationToken cancellationToken)
    {
        lock (_requestGate)
            return Task.FromResult<MessageEnvelope?>(HandleCore(request));
    }

    private MessageEnvelope HandleCore(MessageEnvelope request)
    {
        if (request.ProtocolVersion != Protocol.CurrentVersion)
            throw new InvalidDataException("إصدار إضافة Word لا يطابق إصدار برنامج الإملاء. حدّثهما معًا.");
        MessageEnvelope result;
        switch (request.Type)
        {
            case MessageTypes.Hello:
            case MessageTypes.Ping:
                result = State(request, "ready");
                break;
            case MessageTypes.StartSession:
                if (string.IsNullOrWhiteSpace(request.SessionId))
                    throw new InvalidDataException("Session ID is required.");
                if (_activeSession is not null && _activeSession != request.SessionId &&
                    (_controller.IsListening || _controller.IsDraining || _startTask is { IsCompleted: false }))
                    throw new InvalidOperationException("توجد جلسة إملاء أخرى نشطة. أوقفها أولًا.");
                if (_activeSession != request.SessionId || _startTask is null || _startError is not null)
                {
                    _activeSession = request.SessionId;
                    _startError = null;
                    _startTask = StartSafelyAsync(request.SessionId);
                }
                result = State(request, _controller.IsListening ? "listening" : "preparing");
                break;
            case MessageTypes.StopSession:
                if (request.SessionId == _activeSession)
                {
                    if (_startTask is { IsCompleted: false }) _ = StopAfterStartAsync(_startTask);
                    else _controller.Stop();
                }
                result = State(request, _controller.IsDraining ? "draining" : "ready");
                break;
            case MessageTypes.CancelSession:
                if (request.SessionId == _activeSession) _controller.Cancel();
                result = State(request, "ready");
                break;
            case MessageTypes.CommitAck:
                var ack = PipeMessageCodec.ReadPayload<CommitAckRequest>(request)
                    ?? throw new InvalidDataException("Missing commit acknowledgement.");
                _commits.Acknowledge(request.SessionId, ack.CommitId);
                result = State(request, "acknowledged");
                break;
            case MessageTypes.GetStatus when _commits.Peek(request.SessionId) is { } committed:
                result = Reply(request, MessageTypes.CommitText, PipeMessageCodec.Payload(committed));
                break;
            case MessageTypes.GetStatus:
                result = State(request, _startError is not null && request.SessionId == _activeSession ? "error" :
                    _controller.IsListening ? "listening" : _controller.IsDraining ? "draining" :
                    _startTask is { IsCompleted: false } ? "preparing" : "ready",
                    request.SessionId == _activeSession ? _startError : null);
                break;
            default:
                throw new InvalidDataException($"Unknown message type '{request.Type}'.");
        }
        return result;
    }

    private async Task StartSafelyAsync(string sessionId)
    {
        try { await _controller.StartAsync(sessionId); }
        catch (Exception ex) { _startError = ex.Message; }
    }

    private async Task StopAfterStartAsync(Task startTask)
    {
        await startTask;
        _controller.Stop();
    }

    private MessageEnvelope State(MessageEnvelope request, string state, string? detail = null) =>
        Reply(request, MessageTypes.EngineState, PipeMessageCodec.Payload(new EngineStateEvent { State = state, Detail = detail }));

    private MessageEnvelope Reply(MessageEnvelope request, string type, string payload) => new()
    {
        Type = type,
        SessionId = request.SessionId,
        SequenceNumber = Interlocked.Increment(ref _sequence),
        CorrelationId = request.CorrelationId,
        PayloadJson = payload
    };
}
