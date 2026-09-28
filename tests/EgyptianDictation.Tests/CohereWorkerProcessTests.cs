using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using EgyptianDictation.Contracts;

namespace EgyptianDictation.Tests;

[Collection("ProcessIntegration")]
public sealed class CohereWorkerProcessTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task PersistentWorkerSupportsPingLoadTranscribeAndShutdown()
    {
        var pipeName = $"EgyptianDictation.Test.{Guid.NewGuid():N}";
        using var process = StartWorker(pipeName, fake: true);
        var ping = await SendWhenReadyAsync(pipeName, new WorkerRequest { Type = WorkerMessageTypes.Ping });
        Assert.True(ping.Ok);

        var loaded = await SendAsync(pipeName, new WorkerRequest { Type = WorkerMessageTypes.Load });
        Assert.True(loaded.Ok);
        Assert.Equal(WorkerStates.Ready, loaded.State);

        var result = await SendAsync(pipeName, new WorkerRequest
        {
            Type = WorkerMessageTypes.Transcribe,
            SessionId = "session-1",
            AudioPath = "unused.wav"
        });
        Assert.True(result.Ok);
        Assert.Equal("اختبار ناجح", result.Text);

        var repeated = await SendAsync(pipeName, new WorkerRequest
        {
            Type = WorkerMessageTypes.Transcribe,
            SessionId = "session-2",
            AudioPath = "unused.wav"
        });
        Assert.True(repeated.Ok);
        Assert.Equal("اختبار ناجح", repeated.Text);

        var shutdown = await SendAsync(pipeName, new WorkerRequest { Type = WorkerMessageTypes.Shutdown });
        Assert.True(shutdown.Ok);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(timeout.Token);
    }

    [Fact]
    public async Task CancelStopsAnInflightRequest()
    {
        var pipeName = $"EgyptianDictation.Cancel.{Guid.NewGuid():N}";
        using var process = StartWorker(pipeName, fake: true, extraArguments: "--fake-delay-ms 3000");
        await SendWhenReadyAsync(pipeName, new WorkerRequest { Type = WorkerMessageTypes.Ping });
        await SendAsync(pipeName, new WorkerRequest { Type = WorkerMessageTypes.Load });
        var transcription = SendAsync(pipeName, new WorkerRequest
        {
            Type = WorkerMessageTypes.Transcribe,
            SessionId = "cancel-me",
            AudioPath = "unused.wav"
        }, 10_000);
        await Task.Delay(150);
        var cancelled = await SendAsync(pipeName, new WorkerRequest
        {
            Type = WorkerMessageTypes.Cancel,
            SessionId = "cancel-me"
        });
        Assert.True(cancelled.Ok);
        var result = await transcription;
        Assert.False(result.Ok);
        Assert.EndsWith("CanceledException", result.ErrorCode);
        if (!process.HasExited)
            await SendAsync(pipeName, new WorkerRequest { Type = WorkerMessageTypes.Shutdown });
    }

    [Fact]
    public async Task WorkerReportsMissingAndIncompleteModelWithoutCrashing()
    {
        var pipeName = $"EgyptianDictation.Test.{Guid.NewGuid():N}";
        using var process = StartWorker(pipeName, fake: false);
        await SendWhenReadyAsync(pipeName, new WorkerRequest { Type = WorkerMessageTypes.Ping });

        var missing = await SendAsync(pipeName, new WorkerRequest
        {
            Type = WorkerMessageTypes.Load,
            ModelPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.gguf")
        });
        Assert.False(missing.Ok);
        Assert.Equal("MODEL_MISSING", missing.ErrorCode);

        var invalidPath = Path.Combine(Path.GetTempPath(), $"invalid-{Guid.NewGuid():N}.gguf");
        await File.WriteAllBytesAsync(invalidPath, [1, 2, 3, 4]);
        try
        {
            var invalid = await SendAsync(pipeName, new WorkerRequest
            {
                Type = WorkerMessageTypes.Load,
                ModelPath = invalidPath
            });
            Assert.False(invalid.Ok);
            Assert.Equal(nameof(InvalidDataException), invalid.ErrorCode);
        }
        finally
        {
            File.Delete(invalidPath);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    [Trait("Category", "RealModel")]
    public async Task RealCohereModelTranscribesRepresentativeArabicAudio()
    {
        var root = FindRepositoryRoot();
        var model = Path.Combine(root, OfflineModel.FileName);
        if (!File.Exists(model))
            return;
        var pipeName = $"EgyptianDictation.RealModel.{Guid.NewGuid():N}";
        using var process = StartWorker(pipeName, fake: false);
        await SendWhenReadyAsync(pipeName, new WorkerRequest { Type = WorkerMessageTypes.Ping });
        try
        {
            var loaded = await SendAsync(pipeName, new WorkerRequest
            {
                Type = WorkerMessageTypes.Load,
                ModelPath = model,
                Threads = 6
            }, 120_000);
            Assert.True(loaded.Ok, loaded.Message);
            Assert.Equal("cpu", loaded.Backend, ignoreCase: true);

            var result = await SendAsync(pipeName, new WorkerRequest
            {
                Type = WorkerMessageTypes.Transcribe,
                SessionId = "real-model",
                AudioPath = Path.Combine(root, "benchmarks", "samples", "synthetic-ar-eg.wav"),
                Language = "ar"
            }, 120_000);
            Assert.True(result.Ok, result.Message);
            Assert.Contains("فحص", result.Text);
            Assert.Contains("الخصائص الخطية", result.Text);
            Assert.True(result.WorkingSetBytes > 1_000_000_000);
        }
        finally
        {
            if (!process.HasExited)
                await SendAsync(pipeName, new WorkerRequest { Type = WorkerMessageTypes.Shutdown });
        }
    }

    [Fact]
    [Trait("Category", "RealModelGpu")]
    public async Task AutoBackendTranscribesWithAvailableDeviceOrCpu()
    {
        var root = FindRepositoryRoot();
        var model = Path.Combine(root, OfflineModel.FileName);
        if (!File.Exists(model)) return;
        var pipeName = $"EgyptianDictation.Auto.{Guid.NewGuid():N}";
        using var process = StartWorker(pipeName, fake: false, extraArguments: "--backend auto");
        await SendWhenReadyAsync(pipeName, new WorkerRequest { Type = WorkerMessageTypes.Ping });
        try
        {
            var loaded = await SendAsync(pipeName, new WorkerRequest
            {
                Type = WorkerMessageTypes.Load, ModelPath = model, Threads = 6
            }, 120_000);
            Assert.True(loaded.Ok, loaded.Message);
            var result = await SendAsync(pipeName, new WorkerRequest
            {
                Type = WorkerMessageTypes.Transcribe,
                SessionId = "auto-model",
                AudioPath = Path.Combine(root, "benchmarks", "samples", "synthetic-ar-eg.wav"),
                Language = "ar"
            }, 120_000);
            Assert.True(result.Ok, result.Message);
            Assert.Contains("فحص", result.Text);
            Assert.Equal(loaded.Backend, result.Backend);
        }
        finally
        {
            if (!process.HasExited)
                await SendAsync(pipeName, new WorkerRequest { Type = WorkerMessageTypes.Shutdown });
        }
    }

    private static Process StartWorker(string pipeName, bool fake, string extraArguments = "")
    {
        var root = FindRepositoryRoot();
        var worker = Path.Combine(root, "src", "ArabicSTTWorker", "bin", "Release", "net8.0-windows", "win-x64", "ArabicSTTWorker.exe");
        Assert.True(File.Exists(worker), $"Worker binary not found: {worker}");
        return Process.Start(new ProcessStartInfo
        {
            FileName = worker,
            Arguments = $"--pipe {pipeName}{(fake ? " --fake" : string.Empty)} {extraArguments}",
            WorkingDirectory = Path.GetDirectoryName(worker)!,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start worker.");
    }

    private static async Task<WorkerResponse> SendWhenReadyAsync(string pipe, WorkerRequest request)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try { return await SendAsync(pipe, request, 250); }
            catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException) { last = ex; }
            await Task.Delay(50);
        }
        throw new TimeoutException("Worker did not start.", last);
    }

    private static async Task<WorkerResponse> SendAsync(string pipeName, WorkerRequest request, int timeoutMs = 5000)
    {
        request.RequestId = Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
        await pipe.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));
        var line = await reader.ReadLineAsync(timeout.Token);
        return JsonSerializer.Deserialize<WorkerResponse>(line!, JsonOptions)!;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "EgyptianDictation.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
