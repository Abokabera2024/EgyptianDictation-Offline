using EgyptianDictation.Core.Audio;

namespace EgyptianDictation.Tests;

public sealed class PcmSpeechSegmenterTests
{
    [Fact]
    public void EmitsSpeechAfterTrailingSilence()
    {
        var sut = new PcmSpeechSegmenter(new SpeechSegmenterOptions
        {
            SpeechThreshold = 0.01,
            PreRoll = TimeSpan.Zero,
            MinimumSpeech = TimeSpan.FromMilliseconds(100),
            EndSilence = TimeSpan.FromMilliseconds(200)
        });
        byte[]? emitted = null;
        sut.SegmentCompleted += (_, bytes) => emitted = bytes;

        sut.Push(Tone(milliseconds: 300));
        sut.Push(Silence(milliseconds: 250));

        Assert.NotNull(emitted);
        Assert.True(emitted!.Length >= 16_000 * 2 * 0.5);
    }

    [Fact]
    public void IgnoresShortNoiseBurst()
    {
        var sut = new PcmSpeechSegmenter(new SpeechSegmenterOptions
        {
            SpeechThreshold = 0.01,
            PreRoll = TimeSpan.Zero,
            MinimumSpeech = TimeSpan.FromMilliseconds(300),
            EndSilence = TimeSpan.FromMilliseconds(100)
        });
        var count = 0;
        sut.SegmentCompleted += (_, _) => count++;
        sut.Push(Tone(milliseconds: 50));
        sut.Push(Silence(milliseconds: 150));
        Assert.Equal(0, count);
    }

    [Fact]
    public void SilenceDoesNotProduceAnUtterance()
    {
        var sut = new PcmSpeechSegmenter();
        var count = 0;
        sut.SegmentCompleted += (_, _) => count++;
        sut.Push(Silence(5_000));
        sut.Flush();
        Assert.Equal(0, count);
    }

    [Theory]
    [InlineData(5_000)]
    [InlineData(10_000)]
    public void PreservesRepresentativeUtteranceDurations(int milliseconds)
    {
        var sut = new PcmSpeechSegmenter(new SpeechSegmenterOptions
        {
            PreRoll = TimeSpan.Zero,
            MinimumSpeech = TimeSpan.FromMilliseconds(400),
            EndSilence = TimeSpan.FromMilliseconds(600),
            MaximumSegment = TimeSpan.FromSeconds(12)
        });
        byte[]? emitted = null;
        sut.SegmentCompleted += (_, bytes) => emitted = bytes;
        sut.Push(Tone(milliseconds));
        sut.Push(Silence(700));
        Assert.NotNull(emitted);
        Assert.True(emitted!.Length >= 16_000 * 2 * milliseconds / 1000);
    }

    [Fact]
    public void SplitsAtShortPauseAfterTargetWithoutWaitingForLongSilence()
    {
        var sut = new PcmSpeechSegmenter(new SpeechSegmenterOptions
        {
            PreRoll = TimeSpan.Zero,
            MinimumSpeech = TimeSpan.FromMilliseconds(200),
            TargetSegment = TimeSpan.FromSeconds(4),
            BoundarySilence = TimeSpan.FromMilliseconds(160),
            EndSilence = TimeSpan.FromMilliseconds(500),
            MaximumSegment = TimeSpan.FromSeconds(12)
        });
        var segments = new List<byte[]>();
        sut.SegmentCompleted += (_, segment) => segments.Add(segment);
        sut.Push(Tone(4_300));
        sut.Push(Silence(180));
        Assert.Single(segments);
        Assert.InRange(segments[0].Length / 32_000d, 4.4, 4.5);
    }

    [Fact]
    public void SameSpeechAcrossUnevenCallbackSizesMatchesSingleBlock()
    {
        var audio = Tone(700).Concat(Silence(500)).ToArray();
        static byte[] Capture(byte[] audio, int chunkSize)
        {
            var sut = new PcmSpeechSegmenter(new SpeechSegmenterOptions { PreRoll = TimeSpan.Zero });
            byte[]? result = null;
            sut.SegmentCompleted += (_, segment) => result = segment;
            for (var i = 0; i < audio.Length; i += chunkSize)
                sut.Push(audio.AsSpan(i, Math.Min(chunkSize, audio.Length - i)));
            sut.Flush();
            return result!;
        }
        Assert.Equal(Capture(audio, audio.Length), Capture(audio, 960));
    }

    private static byte[] Silence(int milliseconds) => new byte[16_000 * 2 * milliseconds / 1000];

    private static byte[] Tone(int milliseconds)
    {
        var samples = 16_000 * milliseconds / 1000;
        var result = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * 440 * i / 16_000) * 8000);
            result[i * 2] = (byte)(sample & 0xff);
            result[i * 2 + 1] = (byte)(sample >> 8);
        }
        return result;
    }
}
