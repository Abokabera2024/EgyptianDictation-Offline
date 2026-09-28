using System.IO;
using System.Threading.Channels;
using System.Text.Json;
using EgyptianDictation.Contracts;
using EgyptianDictation.Core.Arabic;
using EgyptianDictation.Core.Asr;
using EgyptianDictation.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EgyptianDictation.Host;

public sealed class MicrophoneDictationController : IAsyncDisposable
{
    private readonly PcmSpeechSegmenter _segmenter;
    private readonly ArabicTextNormalizer _normalizer = new();
    private readonly VoiceCommandProcessor _voiceCommands = new();
    private readonly EngineSettings _settings;
    private readonly Channel<PendingSegment> _segments = Channel.CreateUnbounded<PendingSegment>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _consumer;
    private WasapiCapture? _capture;
    private ITranscriptionEngine? _engine;
    private bool _modelLoaded;
    private string? _sessionId;
    private bool _draining;
    private int _pendingCount;
    private long _queuedAudioBytes;
    private int _backlogStopRequested;

    public MicrophoneDictationController(ITranscriptionEngine? engine = null)
    {
        var settings = _settings = EngineSettings.Load();
        _segmenter = new PcmSpeechSegmenter(new SpeechSegmenterOptions
        {
            PreRoll = TimeSpan.FromMilliseconds(settings.VadPreRollMs),
            MinimumSpeech = TimeSpan.FromMilliseconds(settings.VadMinimumSpeechMs),
            EndSilence = TimeSpan.FromMilliseconds(settings.VadEndSilenceMs),
            TargetSegment = TimeSpan.FromSeconds(settings.TargetUtteranceSeconds),
            BoundarySilence = TimeSpan.FromMilliseconds(settings.VadBoundarySilenceMs),
            MaximumSegment = TimeSpan.FromSeconds(settings.MaxUtteranceSeconds)
        });
        _engine = engine;
        _segmenter.SegmentCompleted += (_, segment) =>
        {
            var session = _sessionId;
            if (session is not null)
            {
                Interlocked.Increment(ref _pendingCount);
                Interlocked.Add(ref _queuedAudioBytes, segment.Length);
                if (!_segments.Writer.TryWrite(new PendingSegment(session, segment)))
                {
                    Interlocked.Decrement(ref _pendingCount);
                    Interlocked.Add(ref _queuedAudioBytes, -segment.Length);
                }
                else if (Interlocked.Read(ref _queuedAudioBytes) > 120L * 16_000 * 2 &&
                    Interlocked.Exchange(ref _backlogStopRequested, 1) == 0)
                {
                    StatusChanged?.Invoke("الجهاز أبطأ من الإملاء؛ أوقف التسجيل وأكمل تحويل الصوت المحفوظ.");
                    _ = Task.Run(Stop);
                }
            }
        };
        _consumer = ConsumeSegmentsAsync(_lifetime.Token);
    }

    public event Action<string>? StatusChanged;
    public event Action<string, string, AsrTranscriptionResult, bool>? TextCommitted;
    public bool IsListening => _capture is not null;
    public bool IsDraining => _draining;

    public async Task StartAsync(string? sessionId = null)
    {
        if (_capture is not null)
            return;
        if (_draining)
            throw new InvalidOperationException("انتظر إكمال تحويل آخر كلام قبل بدء جلسة جديدة.");

        StatusChanged?.Invoke("تحميل النموذج...");
        if (_engine is null)
        {
            var paths = RuntimePaths.Resolve();
            _engine = new CohereOfflineTranscriptionEngine(new CohereEngineOptions
            {
                WorkerExecutable = paths.WorkerExecutable,
                ModelPath = paths.ModelPath,
                Threads = paths.Threads,
                PreferredBackend = _settings.Backend
            });
        }
        await _engine.LoadAsync();
        _modelLoaded = true;

        _sessionId = sessionId ?? Guid.NewGuid().ToString("N");
        Interlocked.Exchange(ref _backlogStopRequested, 0);
        _segmenter.Reset();
        _capture = new WasapiCapture();
        _capture.DataAvailable += CaptureOnDataAvailable;
        _capture.RecordingStopped += (_, args) =>
        {
            if (args.Exception is not null)
            {
                StatusChanged?.Invoke($"خطأ الميكروفون: {args.Exception.Message}");
                Stop();
            }
        };
        _capture.StartRecording();
        StatusChanged?.Invoke("أستمع الآن...");
    }

    public void Stop()
    {
        if (_capture is null)
            return;
        _capture.StopRecording();
        _capture.Dispose();
        _capture = null;
        _segmenter.Flush();
        _segmenter.Reset();
        _draining = Volatile.Read(ref _pendingCount) > 0;
        if (!_draining) _sessionId = null;
        StatusChanged?.Invoke(_draining ? "أكمل تحويل آخر كلام..." : "جاهز");
    }

    public void Cancel()
    {
        var session = _sessionId;
        Stop();
        _sessionId = null;
        _draining = false;
        if (session is not null && _engine is not null)
            _ = CancelSessionSafelyAsync(session);
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        _segments.Writer.TryComplete();
        _lifetime.Cancel();
        try { await _consumer; } catch (OperationCanceledException) { }
        if (_engine is not null)
            await _engine.DisposeAsync();
        _lifetime.Dispose();
    }

    private void CaptureOnDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (_capture is null || args.BytesRecorded == 0)
            return;
        try
        {
            using var input = new RawSourceWaveStream(new MemoryStream(args.Buffer, 0, args.BytesRecorded, writable: false), _capture.WaveFormat);
            using var resampler = new MediaFoundationResampler(input, new WaveFormat(16_000, 16, 1)) { ResamplerQuality = 60 };
            using var output = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
                output.Write(buffer, 0, read);
            _segmenter.Push(output.ToArray());
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"خطأ تحويل الصوت: {ex.Message}");
        }
    }

    private async Task ConsumeSegmentsAsync(CancellationToken cancellationToken)
    {
        await foreach (var pending in _segments.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                if (_engine is null || !_modelLoaded || !string.Equals(pending.SessionId, _sessionId, StringComparison.Ordinal))
                    continue;
                StatusChanged?.Invoke("أحوّل الكلام إلى نص...");
                await new RecoveringSegmentTranscriber(_engine)
                    .TranscribeAsync(pending.Pcm, pending.SessionId, result =>
                    {
                    var text = _normalizer.NormalizeForDocument(result.Text);
                    var newParagraph = _voiceCommands.TryParseLeadingNewParagraph(text, out text);
                    text = ArabicDateFormatter.FormatForDocument(text);
                    if ((newParagraph || !string.IsNullOrWhiteSpace(text)) &&
                        string.Equals(pending.SessionId, _sessionId, StringComparison.Ordinal))
                        TextCommitted?.Invoke(pending.SessionId, text, result, newParagraph);
                    }, cancellationToken);
                StatusChanged?.Invoke(_capture is null ? "جاهز" : "أستمع الآن...");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StatusChanged?.Invoke($"خطأ الاستدلال: {ex.Message}");
            }
            finally
            {
                Interlocked.Add(ref _queuedAudioBytes, -pending.Pcm.Length);
                if (Interlocked.Decrement(ref _pendingCount) == 0 && _draining)
                {
                    _draining = false;
                    _sessionId = null;
                    StatusChanged?.Invoke("جاهز");
                }
            }
        }
    }

    private async Task CancelSessionSafelyAsync(string sessionId)
    {
        try { await _engine!.CancelSessionAsync(sessionId); }
        catch { }
    }

    private sealed record PendingSegment(string SessionId, byte[] Pcm);
}

internal sealed record RuntimePaths(string WorkerExecutable, string ModelPath, int Threads)
{
    public static RuntimePaths Resolve()
    {
        var settings = EngineSettings.Load();
        var baseDirectory = AppContext.BaseDirectory;
        var normalizedBaseDirectory = Path.TrimEndingDirectorySeparator(baseDirectory);
        foreach (var root in new[]
        {
            normalizedBaseDirectory,
            Directory.GetParent(normalizedBaseDirectory)?.FullName
        })
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            var packaged = new RuntimePaths(
                Path.Combine(root, "runtime", "cohere", "ArabicSTTWorker.exe"),
                ResolveModelPath(root, settings.ModelPath), settings.Threads);
            if (File.Exists(packaged.WorkerExecutable))
                return packaged;
        }

        var current = new DirectoryInfo(baseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "EgyptianDictation.sln")))
            current = current.Parent;
        if (current is null)
            throw new DirectoryNotFoundException("لم يتم العثور على ملفات تشغيل المحرك.");
        var worker = Path.Combine(current.FullName, "src", "ArabicSTTWorker", "bin", "Release", "net8.0-windows", "win-x64", "ArabicSTTWorker.exe");
        return new RuntimePaths(worker, ResolveModelPath(current.FullName, settings.ModelPath), settings.Threads);
    }

    private static string ResolveModelPath(string root, string? configured)
    {
        const string file = OfflineModel.FileName;
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("EGYPTIAN_DICTATION_MODEL_PATH"),
            configured,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EgyptianDictation", "Models", file),
            Path.Combine(root, "Models", file),
            Path.Combine(root, "models", file),
            Path.Combine(root, file)
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            ?? candidates.First(path => !string.IsNullOrWhiteSpace(path))!;
    }
}

internal sealed class EngineSettings
{
    public string? ModelPath { get; set; }
    public int Threads { get; set; }
    public string Backend { get; set; } = "auto";
    public int VadPreRollMs { get; set; } = 250;
    public int VadMinimumSpeechMs { get; set; } = 240;
    public int VadEndSilenceMs { get; set; } = 450;
    public int VadBoundarySilenceMs { get; set; } = 160;
    public int TargetUtteranceSeconds { get; set; } = 4;
    public int MaxUtteranceSeconds { get; set; } = 12;

    public static EngineSettings Load()
    {
        var paths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "engine-settings.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EgyptianDictation", "engine-settings.json")
        };
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<EngineSettings>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new();
            }
            catch { }
        }
        return new();
    }
}
