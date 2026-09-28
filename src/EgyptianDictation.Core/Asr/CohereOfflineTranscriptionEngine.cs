using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using EgyptianDictation.Contracts;

namespace EgyptianDictation.Core.Asr;

public sealed class CohereOfflineTranscriptionEngine : ITranscriptionEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CohereEngineOptions _options;
    private readonly string _pipeName = $"EgyptianDictation.Cohere.{Environment.ProcessId}.{Guid.NewGuid():N}";
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Process? _process;
    private bool _loaded;
    private bool _safeCpu;
    private bool _forceCpu;
    private bool _disposed;

    public CohereOfflineTranscriptionEngine(CohereEngineOptions options)
    {
        _options = options;
        _forceCpu = string.Equals(options.PreferredBackend, "cpu", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsReady => _loaded && _process is { HasExited: false };

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (IsReady)
                return;
            WorkerResponse response;
            try
            {
                response = await LoadWorkerAsync(cancellationToken);
                EnsureSuccess(response);
            }
            catch (Exception first) when (!cancellationToken.IsCancellationRequested)
            {
                if (_safeCpu) throw;
                await RestartOwnedWorkerAsync();
                if (_forceCpu) _safeCpu = true;
                else _forceCpu = true;
                try
                {
                    response = await LoadWorkerAsync(cancellationToken);
                    EnsureSuccess(response);
                }
                catch (Exception fallback)
                {
                    throw new InvalidOperationException(
                        $"تعذر تشغيل محرك Cohere حتى في وضع توافق CPU. السبب: {fallback.Message}",
                        new AggregateException(first, fallback));
                }
            }
            _loaded = true;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task<WorkerResponse> LoadWorkerAsync(CancellationToken cancellationToken)
    {
        await EnsureWorkerStartedAsync(cancellationToken);
        return await SendAsync(new WorkerRequest
        {
            Type = WorkerMessageTypes.Load,
            ModelPath = _options.ModelPath,
            Threads = _options.Threads
        }, cancellationToken, responseTimeout: TimeSpan.FromSeconds(120));
    }

    private async Task<bool> WorkerStoppedAsync()
    {
        if (_process is null)
            return true;
        if (_process.HasExited)
            return true;
        await Task.Delay(250);
        return _process.HasExited;
    }

    private void ResetStoppedWorker()
    {
        _loaded = false;
        _process?.Dispose();
        _process = null;
    }

    public async Task<AsrTranscriptionResult> TranscribeAsync(
        string audioPath,
        string sessionId,
        string language = "ar",
        CancellationToken cancellationToken = default)
    {
        if (!IsReady)
            await LoadAsync(cancellationToken);
        WorkerResponse response;
        var duration = Math.Max(0, (new FileInfo(audioPath).Length - 44) / 32_000d);
        var responseTimeout = TimeSpan.FromSeconds(Math.Clamp(duration * 5 + 15, 45, 180));
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!IsReady) await LoadAsync(cancellationToken);
                response = await SendAsync(new WorkerRequest
                {
                    Type = WorkerMessageTypes.Transcribe,
                    SessionId = sessionId,
                    AudioPath = Path.GetFullPath(audioPath),
                    Language = language
                }, cancellationToken, responseTimeout: responseTimeout);
                EnsureSuccess(response);
                break;
            }
            catch (Exception ex) when (attempt == 0 && !cancellationToken.IsCancellationRequested &&
                (ex is IOException or TimeoutException or EndOfStreamException ||
                 (ex is InvalidOperationException && !_forceCpu && IsGpuFailure(ex)) ||
                 _process?.HasExited == true))
            {
                await RestartOwnedWorkerAsync();
                _forceCpu = true;
                await LoadAsync(cancellationToken);
            }
        }
        return new AsrTranscriptionResult
        {
            Text = response.Text ?? string.Empty,
            AudioSeconds = response.AudioSeconds,
            InferenceSeconds = response.InferenceSeconds,
            RealTimeFactor = response.RealTimeFactor,
            Backend = response.Backend ?? "cpu",
            WorkerThreads = response.Threads,
            WorkingSetBytes = response.WorkingSetBytes,
            ModelLoadSeconds = response.ModelLoadSeconds,
            ModelPath = _options.ModelPath
        };
    }

    public async Task CancelSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (_process is null || _process.HasExited)
            return;
        var response = await SendAsync(new WorkerRequest
        {
            Type = WorkerMessageTypes.Cancel,
            SessionId = sessionId
        }, cancellationToken);
        EnsureSuccess(response);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_process is { HasExited: false })
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await SendAsync(new WorkerRequest { Type = WorkerMessageTypes.Shutdown }, timeout.Token);
                await _process.WaitForExitAsync(timeout.Token);
            }
            catch
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
        }
        _process?.Dispose();
        _lifecycle.Dispose();
    }

    private async Task EnsureWorkerStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false })
            return;
        if (!File.Exists(_options.WorkerExecutable))
            throw new FileNotFoundException("ملف محرك Cohere غير مثبت.", _options.WorkerExecutable);
        _process?.Dispose();
        _process = Process.Start(new ProcessStartInfo
        {
            FileName = _options.WorkerExecutable,
            Arguments = $"--pipe {_pipeName} --backend {(_forceCpu ? "cpu" : "auto")}" +
                (_safeCpu ? " --cpu-safe" : string.Empty),
            WorkingDirectory = Path.GetDirectoryName(_options.WorkerExecutable)!,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("تعذر تشغيل محرك Cohere المحلي.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.StartupTimeout);
        Exception? last = null;
        while (!timeout.IsCancellationRequested)
        {
            if (_process.HasExited)
                throw new InvalidOperationException($"توقف محرك Cohere أثناء البدء (رمز {_process.ExitCode}).");
            try
            {
                var ping = await SendAsync(new WorkerRequest { Type = WorkerMessageTypes.Ping }, timeout.Token, 500);
                EnsureSuccess(ping);
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
            {
                last = ex;
                await Task.Delay(100, timeout.Token);
            }
        }
        throw new TimeoutException("لم يستجب محرك Cohere المحلي في الوقت المحدد.", last);
    }

    private async Task<WorkerResponse> SendAsync(WorkerRequest request, CancellationToken cancellationToken,
        int timeoutMs = 5000, TimeSpan? responseTimeout = null)
    {
        request.RequestId = Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeoutMs, cancellationToken);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));
        using var responseDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        responseDeadline.CancelAfter(responseTimeout ?? TimeSpan.FromSeconds(10));
        string? line;
        try { line = await reader.ReadLineAsync(responseDeadline.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("تجاوز محرك Cohere مهلة الاستجابة.");
        }
        if (line is null) throw new EndOfStreamException("أغلق محرك Cohere قناة الاتصال.");
        var response = JsonSerializer.Deserialize<WorkerResponse>(line, JsonOptions)
            ?? throw new InvalidDataException("استجابة محرك Cohere غير صالحة.");
        if (!string.Equals(request.RequestId, response.RequestId, StringComparison.Ordinal))
            throw new InvalidDataException("معرّف استجابة محرك Cohere غير مطابق.");
        return response;
    }

    private static void EnsureSuccess(WorkerResponse response)
    {
        if (!response.Ok)
            throw new InvalidOperationException(response.Message ?? response.ErrorCode ?? "خطأ غير معروف في محرك Cohere.");
    }

    private static bool IsGpuFailure(Exception exception) =>
        exception.Message.Contains("Vulkan", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("GPU", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("device lost", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("out of memory", StringComparison.OrdinalIgnoreCase);

    private async Task RestartOwnedWorkerAsync()
    {
        _loaded = false;
        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            catch (InvalidOperationException) { }
        }
        _process?.Dispose();
        _process = null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
