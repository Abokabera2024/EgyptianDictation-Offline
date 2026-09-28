namespace EgyptianDictation.Core.Audio;

public sealed class SpeechSegmenterOptions
{
    public int SampleRate { get; init; } = 16_000;
    public double SpeechThreshold { get; init; } = 0.012;
    public TimeSpan PreRoll { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan EndSilence { get; init; } = TimeSpan.FromMilliseconds(650);
    public TimeSpan MinimumSpeech { get; init; } = TimeSpan.FromMilliseconds(300);
    public TimeSpan MaximumSegment { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan TargetSegment { get; init; } = TimeSpan.FromSeconds(4);
    public TimeSpan BoundarySilence { get; init; } = TimeSpan.FromMilliseconds(160);
}

public sealed class PcmSpeechSegmenter
{
    private readonly SpeechSegmenterOptions _options;
    private readonly Queue<byte[]> _preRoll = new();
    private readonly List<byte> _active = new();
    private int _preRollBytes;
    private int _silenceBytes;
    private int _speechBytes;
    private readonly List<byte> _partialFrame = new();

    public PcmSpeechSegmenter(SpeechSegmenterOptions? options = null)
    {
        _options = options ?? new SpeechSegmenterOptions();
    }

    public event EventHandler<byte[]>? SegmentCompleted;
    public bool IsSpeaking => _active.Count > 0;

    public void Push(ReadOnlySpan<byte> pcm16Mono)
    {
        if (pcm16Mono.Length < 2)
            return;

        // VAD decisions use fixed 20 ms frames, independent of microphone callback size.
        var frameBytes = _options.SampleRate * 2 / 50;
        for (var index = 0; index + 1 < pcm16Mono.Length; index += 2)
        {
            _partialFrame.Add(pcm16Mono[index]);
            _partialFrame.Add(pcm16Mono[index + 1]);
            if (_partialFrame.Count == frameBytes)
            {
                ProcessFrame(_partialFrame.ToArray());
                _partialFrame.Clear();
            }
        }
    }

    private void ProcessFrame(byte[] block)
    {
        var speech = RootMeanSquare(block) >= _options.SpeechThreshold;
        if (!IsSpeaking)
        {
            if (!speech)
            {
                AddPreRoll(block);
                return;
            }

            foreach (var buffered in _preRoll)
                _active.AddRange(buffered);
            _active.AddRange(block);
            _speechBytes += block.Length;
            _preRoll.Clear();
            _preRollBytes = 0;
            return;
        }

        _active.AddRange(block);
        if (speech)
        {
            _speechBytes += block.Length;
            _silenceBytes = 0;
        }
        else
        {
            _silenceBytes += block.Length;
        }

        if (_silenceBytes >= BytesFor(_options.EndSilence) ||
            (_active.Count >= BytesFor(_options.TargetSegment) && _silenceBytes >= BytesFor(_options.BoundarySilence)) ||
            _active.Count >= BytesFor(_options.MaximumSegment))
            Complete();
    }

    public void Flush()
    {
        if (_partialFrame.Count > 0)
        {
            var block = _partialFrame.ToArray();
            _partialFrame.Clear();
            ProcessFrame(block);
        }
        if (IsSpeaking)
            Complete();
    }

    public void Reset()
    {
        _preRoll.Clear();
        _active.Clear();
        _preRollBytes = 0;
        _silenceBytes = 0;
        _speechBytes = 0;
        _partialFrame.Clear();
    }

    private void Complete()
    {
        var enoughSpeech = _speechBytes >= BytesFor(_options.MinimumSpeech);
        var segment = enoughSpeech ? _active.ToArray() : null;
        _active.Clear();
        _silenceBytes = 0;
        _speechBytes = 0;
        if (segment is not null)
            SegmentCompleted?.Invoke(this, segment);
    }

    private void AddPreRoll(byte[] block)
    {
        _preRoll.Enqueue(block);
        _preRollBytes += block.Length;
        var max = BytesFor(_options.PreRoll);
        while (_preRollBytes > max && _preRoll.TryDequeue(out var removed))
            _preRollBytes -= removed.Length;
    }

    private int BytesFor(TimeSpan duration) => (int)(_options.SampleRate * 2 * duration.TotalSeconds);

    private static double RootMeanSquare(ReadOnlySpan<byte> pcm)
    {
        double sum = 0;
        var samples = pcm.Length / 2;
        for (var index = 0; index + 1 < pcm.Length; index += 2)
        {
            var sample = (short)(pcm[index] | pcm[index + 1] << 8);
            var normalized = sample / 32768.0;
            sum += normalized * normalized;
        }
        return Math.Sqrt(sum / Math.Max(1, samples));
    }
}
