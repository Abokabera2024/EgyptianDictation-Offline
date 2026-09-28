using System.Buffers.Binary;

namespace EgyptianDictation.Core.Asr;

// A native generation cap is a segment-level failure, not a reason to restart the model.
// Retry only that segment as shorter pieces, cutting at the quietest nearby audio frame.
public sealed class RecoveringSegmentTranscriber
{
    private const int SampleRate = 16_000;
    private const int MinimumSplitBytes = 2 * SampleRate * 2;
    private const int MaximumSplitDepth = 3;
    private readonly ITranscriptionEngine _engine;

    public RecoveringSegmentTranscriber(ITranscriptionEngine engine) => _engine = engine;

    public async Task<IReadOnlyList<AsrTranscriptionResult>> TranscribeAsync(
        byte[] pcm16Mono, string sessionId, CancellationToken cancellationToken = default)
    {
        var results = new List<AsrTranscriptionResult>();
        await TranscribeAsync(pcm16Mono, sessionId, results.Add, cancellationToken);
        return results;
    }

    public Task TranscribeAsync(byte[] pcm16Mono, string sessionId,
        Action<AsrTranscriptionResult> onResult, CancellationToken cancellationToken = default) =>
        TranscribePartAsync(pcm16Mono, sessionId, 0, onResult, cancellationToken);

    private async Task TranscribePartAsync(byte[] pcm, string sessionId, int depth,
        Action<AsrTranscriptionResult> onResult, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"egyptian-dictation-{Guid.NewGuid():N}.wav");
        try
        {
            await WriteWaveAsync(path, pcm, cancellationToken);
            try
            {
                onResult(await _engine.TranscribeAsync(path, sessionId, cancellationToken: cancellationToken));
            }
            catch (InvalidOperationException ex) when (depth < MaximumSplitDepth &&
                pcm.Length >= MinimumSplitBytes && IsGenerationCap(ex))
            {
                var boundary = FindQuietBoundary(pcm);
                await TranscribePartAsync(pcm[..boundary], sessionId, depth + 1, onResult, cancellationToken);
                await TranscribePartAsync(pcm[boundary..], sessionId, depth + 1, onResult, cancellationToken);
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    public static bool IsGenerationCap(Exception error) =>
        error.Message.Contains("output truncated", StringComparison.OrdinalIgnoreCase) ||
        error.Message.Contains("context/generation cap", StringComparison.OrdinalIgnoreCase);

    private static int FindQuietBoundary(byte[] pcm)
    {
        const int frameBytes = SampleRate * 2 / 10;
        var middle = pcm.Length / 2;
        var first = Math.Max(frameBytes, ((int)(pcm.Length * 0.35) / frameBytes) * frameBytes);
        var last = Math.Min(pcm.Length - frameBytes, ((int)(pcm.Length * 0.65) / frameBytes) * frameBytes);
        long bestEnergy = long.MaxValue;
        var best = middle & ~1;
        for (var start = first; start <= last; start += frameBytes)
        {
            long energy = 0;
            for (var i = start; i + 1 < Math.Min(start + frameBytes, pcm.Length); i += 2)
            {
                var sample = (short)(pcm[i] | pcm[i + 1] << 8);
                energy += (long)sample * sample;
            }
            if (energy < bestEnergy)
            {
                bestEnergy = energy;
                best = start;
            }
        }
        return best;
    }

    private static async Task WriteWaveAsync(string path, byte[] pcm, CancellationToken cancellationToken)
    {
        var header = new byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 36 + pcm.Length);
        "WAVEfmt "u8.CopyTo(header.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), 16);
        "data"u8.CopyTo(header.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), pcm.Length);
        await using var stream = File.Create(path);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(pcm, cancellationToken);
    }
}
