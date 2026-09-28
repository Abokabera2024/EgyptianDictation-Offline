using System.Diagnostics;
using System.Text.Json;

namespace EgyptianDictation.Core.Asr;

public sealed class AsrWorkerOptions
{
    public string PythonExecutable { get; init; } = "python";
    public string WorkerScript { get; init; } = string.Empty;
    public bool FakeBackend { get; init; }
    public string? FakeText { get; init; }
}

public sealed class AsrTranscriptionResult
{
    public string Text { get; init; } = string.Empty;
    public double AudioSeconds { get; init; }
    public double InferenceSeconds { get; init; }
    public double RealTimeFactor { get; init; }
    public string Backend { get; init; } = string.Empty;
    public int WorkerThreads { get; init; }
    public long WorkingSetBytes { get; init; }
    public double ModelLoadSeconds { get; init; }
    public string ModelPath { get; init; } = string.Empty;
}

public sealed class AsrWorkerClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Process _process;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly Queue<string> _recentErrors = new();
    private readonly object _errorLock = new();
    private int _nextId;

    public AsrWorkerClient(AsrWorkerOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.WorkerScript))
            throw new ArgumentException("Worker script path is required.", nameof(options));

        var arguments = $"-u \"{Path.GetFullPath(options.WorkerScript)}\"";
        if (options.FakeBackend)
        {
            arguments += " --fake";
            if (!string.IsNullOrWhiteSpace(options.FakeText))
                arguments += $" --fake-text \"{options.FakeText.Replace("\"", "\\\"")}\"";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = options.PythonExecutable,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false)
        };
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";
        _process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        if (!_process.Start())
            throw new InvalidOperationException("Failed to start the ASR worker.");
        _ = DrainErrorsAsync(_process.StandardError);
    }

    public async Task LoadAsync(string model, string device = "cpu", string computeType = "int8", CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(new { command = "load", model, device, compute_type = computeType }, cancellationToken);
        EnsureSuccess(response.RootElement);
    }

    public async Task<AsrTranscriptionResult> TranscribeAsync(string audioPath, string language = "ar", CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(new { command = "transcribe", audio_path = Path.GetFullPath(audioPath), language }, cancellationToken);
        var root = response.RootElement;
        EnsureSuccess(root);
        return new AsrTranscriptionResult
        {
            Text = root.GetProperty("text").GetString() ?? string.Empty,
            AudioSeconds = root.GetProperty("audio_seconds").GetDouble(),
            InferenceSeconds = root.GetProperty("inference_seconds").GetDouble(),
            RealTimeFactor = root.GetProperty("real_time_factor").GetDouble()
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var response = await SendAsync(new { command = "shutdown" }, timeout.Token);
                await _process.WaitForExitAsync(timeout.Token);
            }
            catch
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
        }

        _process.Dispose();
        _requestLock.Dispose();
    }

    private async Task<JsonDocument> SendAsync(object body, CancellationToken cancellationToken)
    {
        await _requestLock.WaitAsync(cancellationToken);
        try
        {
            if (_process.HasExited)
                throw new InvalidOperationException($"ASR worker exited with code {_process.ExitCode}.");

            var requestId = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var json = JsonSerializer.Serialize(body, JsonOptions);
            using var bodyDocument = JsonDocument.Parse(json);
            var properties = bodyDocument.RootElement.EnumerateObject()
                .ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
            properties["id"] = requestId;

            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(properties, JsonOptions));
            await _process.StandardInput.FlushAsync(cancellationToken);
            var line = await _process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                await Task.Delay(50, CancellationToken.None);
                string detail;
                lock (_errorLock)
                    detail = string.Join(Environment.NewLine, _recentErrors);
                throw new EndOfStreamException($"ASR worker closed stdout.{Environment.NewLine}{detail}");
            }
            var response = JsonDocument.Parse(line);
            var responseId = response.RootElement.GetProperty("id").GetString();
            if (!string.Equals(requestId, responseId, StringComparison.Ordinal))
            {
                response.Dispose();
                throw new InvalidDataException($"Expected ASR response {requestId}, received {responseId}.");
            }
            return response;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private static void EnsureSuccess(JsonElement root)
    {
        if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
            return;
        var message = root.TryGetProperty("message", out var detail) ? detail.GetString() : "Unknown ASR worker error.";
        throw new InvalidOperationException(message);
    }

    private async Task DrainErrorsAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            lock (_errorLock)
            {
                _recentErrors.Enqueue(line);
                while (_recentErrors.Count > 20)
                    _recentErrors.Dequeue();
            }
        }
    }
}
