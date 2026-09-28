using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using EgyptianDictation.Contracts;

namespace EgyptianDictation.ArabicSTTWorker;

internal static class WorkerProgram
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(string[] args)
    {
        var benchmark = ValueAfter(args, "--benchmark");
        if (benchmark is not null)
            return await RunBenchmarkAsync(benchmark, ValueAfter(args, "--model"), ValueAfter(args, "--output"),
                int.TryParse(ValueAfter(args, "--threads"), out var threads) ? threads : 0);
        var pipe = ValueAfter(args, "--pipe");
        if (string.IsNullOrWhiteSpace(pipe))
        {
            Console.Error.WriteLine("Usage: ArabicSTTWorker --pipe NAME [--fake]");
            return 2;
        }
        IWorkerEngine engine = args.Contains("--fake", StringComparer.OrdinalIgnoreCase)
            ? new FakeWorkerEngine(int.TryParse(ValueAfter(args, "--fake-delay-ms"), out var delay) ? delay : 0)
            : new NativeCohereEngine(args.Contains("--cpu-safe", StringComparer.OrdinalIgnoreCase)
                ? Path.Combine(AppContext.BaseDirectory, "safe-cpu")
                : null, ValueAfter(args, "--backend") ?? "cpu");
        await using var server = new WorkerServer(pipe, engine);
        await server.RunAsync();
        return 0;
    }

    private static async Task<int> RunBenchmarkAsync(string folder, string? model, string? output, int threads)
    {
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(output))
            throw new ArgumentException("--benchmark requires --model and --output.");
        using var engine = new NativeCohereEngine();
        var process = Process.GetCurrentProcess();
        var cpuStarted = process.TotalProcessorTime;
        var wallStarted = Stopwatch.StartNew();
        engine.Load(model, threads);
        var realTimeFactors = new List<double>();
        await using var writer = new StreamWriter(output, false, new UTF8Encoding(true));
        await writer.WriteLineAsync("file,audio_duration,inference_time,RTF,transcript");
        foreach (var wav in Directory.EnumerateFiles(folder, "*.wav").OrderBy(path => path))
        {
            var result = engine.Transcribe(wav, "ar", CancellationToken.None);
            realTimeFactors.Add(result.RealTimeFactor);
            var escaped = (result.Text ?? string.Empty).Replace("\"", "\"\"");
            await writer.WriteLineAsync(string.Join(",",
                Csv(Path.GetFileName(wav)),
                result.AudioSeconds.ToString("F3", CultureInfo.InvariantCulture),
                result.InferenceSeconds.ToString("F3", CultureInfo.InvariantCulture),
                result.RealTimeFactor.ToString("F3", CultureInfo.InvariantCulture),
                $"\"{escaped}\""));
        }
        wallStarted.Stop();
        process.Refresh();
        var cpuSeconds = (process.TotalProcessorTime - cpuStarted).TotalSeconds;
        var cpuPercent = wallStarted.Elapsed.TotalSeconds <= 0 ? 0 :
            cpuSeconds / wallStarted.Elapsed.TotalSeconds / Environment.ProcessorCount * 100;
        Console.Error.WriteLine($"MODEL_LOAD_SECONDS={engine.ModelLoadSeconds:F3}");
        Console.Error.WriteLine($"WORKING_SET_BYTES={process.WorkingSet64}");
        Console.Error.WriteLine($"AVERAGE_RTF={(realTimeFactors.Count == 0 ? 0 : realTimeFactors.Average()):F3}");
        Console.Error.WriteLine($"AVERAGE_CPU_PERCENT={cpuPercent:F1}");
        Console.Error.WriteLine($"CPU_THREADS={engine.Threads}");
        return 0;
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    private static string? ValueAfter(string[] args, string key)
    {
        var index = Array.FindIndex(args, item => string.Equals(item, key, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private sealed class WorkerServer : IAsyncDisposable
    {
        private readonly string _pipeName;
        private readonly IWorkerEngine _engine;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly SemaphoreSlim _inference = new(1, 1);
        private readonly Dictionary<string, CancellationTokenSource> _active = new(StringComparer.Ordinal);
        private readonly object _activeLock = new();
        private string _state = WorkerStates.NotStarted;

        public WorkerServer(string pipeName, IWorkerEngine engine)
        {
            _pipeName = pipeName;
            _engine = engine;
        }

        public async Task RunAsync()
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 16,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await pipe.WaitForConnectionAsync(_lifetime.Token);
                    _ = HandleConnectionAsync(pipe);
                }
                catch (OperationCanceledException)
                {
                    await pipe.DisposeAsync();
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            lock (_activeLock)
                foreach (var cancellation in _active.Values)
                    cancellation.Cancel();
            _engine.Dispose();
            _inference.Dispose();
            _lifetime.Dispose();
            return ValueTask.CompletedTask;
        }

        private async Task HandleConnectionAsync(NamedPipeServerStream pipe)
        {
            await using (pipe)
            {
                WorkerRequest? request = null;
                try
                {
                    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                    await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                    var line = await reader.ReadLineAsync(_lifetime.Token);
                    request = JsonSerializer.Deserialize<WorkerRequest>(line ?? string.Empty, JsonOptions)
                        ?? throw new InvalidDataException("Invalid worker request.");
                    var response = await HandleAsync(request);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
                    if (request.Type == WorkerMessageTypes.Shutdown)
                        _lifetime.Cancel();
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !_lifetime.IsCancellationRequested)
                {
                    Log(ex);
                    if (pipe.IsConnected)
                    {
                        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                        await writer.WriteLineAsync(JsonSerializer.Serialize(Failure(request, ex), JsonOptions));
                    }
                }
            }
        }

        private async Task<WorkerResponse> HandleAsync(WorkerRequest request)
        {
            if (request.ProtocolVersion != Protocol.CurrentVersion)
                throw new InvalidDataException("Unsupported protocol version.");
            switch (request.Type)
            {
                case WorkerMessageTypes.Ping:
                    return Success(request);
                case WorkerMessageTypes.Load:
                    _state = WorkerStates.LoadingModel;
                    await _inference.WaitAsync(_lifetime.Token);
                    try
                    {
                        _engine.Load(request.ModelPath ?? string.Empty, request.Threads);
                        _state = WorkerStates.Ready;
                        return Success(request);
                    }
                    finally { _inference.Release(); }
                case WorkerMessageTypes.Transcribe:
                    return await TranscribeAsync(request);
                case WorkerMessageTypes.Cancel:
                    lock (_activeLock)
                        if (_active.TryGetValue(request.SessionId, out var active))
                            active.Cancel();
                    return Success(request);
                case WorkerMessageTypes.Shutdown:
                    return Success(request);
                default:
                    throw new InvalidDataException($"Unknown worker request '{request.Type}'.");
            }
        }

        private async Task<WorkerResponse> TranscribeAsync(WorkerRequest request)
        {
            if (!_engine.IsReady)
                throw new InvalidOperationException("Arabic speech model is not loaded.");
            if (string.IsNullOrWhiteSpace(request.SessionId))
                throw new InvalidDataException("Session ID is required.");
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            lock (_activeLock)
                _active[request.SessionId] = cancellation;
            await _inference.WaitAsync(cancellation.Token);
            try
            {
                _state = WorkerStates.Transcribing;
                var result = await Task.Run(() => _engine.Transcribe(
                    request.AudioPath ?? string.Empty, request.Language, cancellation.Token), cancellation.Token);
                _state = WorkerStates.Ready;
                return Success(request, result);
            }
            finally
            {
                _state = _engine.IsReady ? WorkerStates.Ready : WorkerStates.Error;
                _inference.Release();
                lock (_activeLock)
                    _active.Remove(request.SessionId);
            }
        }

        private WorkerResponse Success(WorkerRequest request, WorkerTranscription? result = null) => new()
        {
            RequestId = request.RequestId,
            Ok = true,
            State = _state,
            Text = result?.Text,
            Backend = _engine.Backend,
            Threads = _engine.Threads,
            ModelLoadSeconds = _engine.ModelLoadSeconds,
            AudioSeconds = result?.AudioSeconds ?? 0,
            InferenceSeconds = result?.InferenceSeconds ?? 0,
            RealTimeFactor = result?.RealTimeFactor ?? 0,
            WorkingSetBytes = Process.GetCurrentProcess().WorkingSet64
        };

        private static WorkerResponse Failure(WorkerRequest? request, Exception ex) => new()
        {
            RequestId = request?.RequestId ?? string.Empty,
            Ok = false,
            State = WorkerStates.Error,
            ErrorCode = ex is FileNotFoundException ? "MODEL_MISSING" : ex.GetType().Name,
            Message = ex.Message
        };

        private static void Log(Exception exception)
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EgyptianDictation", "Logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "ArabicSTTWorker.log"),
                    $"{DateTime.UtcNow:o} {exception.GetType().Name}: {exception.Message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}

internal interface IWorkerEngine : IDisposable
{
    bool IsReady { get; }
    string Backend { get; }
    int Threads { get; }
    double ModelLoadSeconds { get; }
    void Load(string modelPath, int threads);
    WorkerTranscription Transcribe(string audioPath, string language, CancellationToken cancellationToken);
}

internal sealed record WorkerTranscription(string Text, double AudioSeconds, double InferenceSeconds)
{
    public double RealTimeFactor => AudioSeconds <= 0 ? 0 : InferenceSeconds / AudioSeconds;
}

internal sealed class FakeWorkerEngine : IWorkerEngine
{
    private readonly int _delayMilliseconds;
    public FakeWorkerEngine(int delayMilliseconds = 0) => _delayMilliseconds = delayMilliseconds;
    public bool IsReady { get; private set; }
    public string Backend => "fake-cpu";
    public int Threads => 1;
    public double ModelLoadSeconds => 0.01;
    public void Load(string modelPath, int threads) => IsReady = true;
    public WorkerTranscription Transcribe(string audioPath, string language, CancellationToken cancellationToken)
    {
        if (_delayMilliseconds > 0 && cancellationToken.WaitHandle.WaitOne(_delayMilliseconds))
            cancellationToken.ThrowIfCancellationRequested();
        cancellationToken.ThrowIfCancellationRequested();
        return new WorkerTranscription("اختبار ناجح", 1, 0.01);
    }
    public void Dispose() { }
}
