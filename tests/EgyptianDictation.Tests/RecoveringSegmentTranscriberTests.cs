using EgyptianDictation.Core.Asr;

namespace EgyptianDictation.Tests;

public sealed class RecoveringSegmentTranscriberTests
{
    [Fact]
    public async Task GenerationCapRetriesOnlyAsShorterAudioParts()
    {
        var engine = new CapEngine();
        var pcm = new byte[8 * 16_000 * 2];
        for (var i = 0; i < pcm.Length; i += 2)
        {
            pcm[i] = 0xE8;
            pcm[i + 1] = 0x03;
        }
        Array.Clear(pcm, 4 * 16_000 * 2, 3_200);

        var results = await new RecoveringSegmentTranscriber(engine).TranscribeAsync(pcm, "session");

        Assert.Equal(2, results.Count);
        Assert.Equal(3, engine.Calls);
        Assert.InRange(results.Sum(result => result.AudioSeconds), 7.99, 8.01);
    }

    [Fact]
    public async Task UnrelatedEngineErrorIsNotRetried()
    {
        var engine = new CapEngine { Error = "model missing" };
        var pcm = new byte[8 * 16_000 * 2];
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RecoveringSegmentTranscriber(engine).TranscribeAsync(pcm, "session"));
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task EarlierRecoveredTextIsDeliveredBeforeLaterPartFails()
    {
        var engine = new CapEngine { FailAtCall = 3 };
        var delivered = new List<string>();
        var pcm = new byte[8 * 16_000 * 2];
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RecoveringSegmentTranscriber(engine).TranscribeAsync(
                pcm, "session", result => delivered.Add(result.Text)));
        Assert.Single(delivered);
    }

    private sealed class CapEngine : ITranscriptionEngine
    {
        public bool IsReady => true;
        public int Calls { get; private set; }
        public string? Error { get; init; }
        public int FailAtCall { get; init; }
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CancelSessionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<AsrTranscriptionResult> TranscribeAsync(string audioPath, string sessionId,
            string language = "ar", CancellationToken cancellationToken = default)
        {
            Calls++;
            var bytes = new FileInfo(audioPath).Length - 44;
            var seconds = bytes / 32_000d;
            if (Error is not null) throw new InvalidOperationException(Error);
            if (Calls == FailAtCall) throw new InvalidOperationException("model missing");
            if (seconds > 4.5)
                throw new InvalidOperationException("output truncated: decode hit the context/generation cap before end-of-stream");
            return Task.FromResult(new AsrTranscriptionResult { Text = $"جزء {Calls}", AudioSeconds = seconds });
        }
    }
}
