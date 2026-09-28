using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using EgyptianDictation.Contracts;
using EgyptianDictation.Core.Ipc;

namespace EgyptianDictation.Tests;

[Collection("ProcessIntegration")]
public sealed class HostProcessTests
{
    [Fact]
    public async Task HeadlessHostAnswersPipePing()
    {
        var root = FindRepositoryRoot();
        var host = Environment.GetEnvironmentVariable("EGYPTIAN_DICTATION_HOST_TEST_PATH") ??
            Path.Combine(root, "src", "EgyptianDictation.Host", "bin", "Release", "net8.0-windows", "EgyptianDictation.Host.exe");
        Assert.True(File.Exists(host), $"Host binary not found: {host}");

        var pipeName = $"EgyptianDictation.Tests.Host.{Guid.NewGuid():N}";
        var startInfo = new ProcessStartInfo(host, "--headless")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(host)!
        };
        startInfo.Environment["EGYPTIAN_DICTATION_PIPE_NAME"] = pipeName;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the host process.");

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var codec = new PipeMessageCodec();
            for (var i = 0; i < 100; i++)
            {
                await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(timeout.Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                await writer.WriteLineAsync(codec.Serialize(new MessageEnvelope { Type = MessageTypes.Ping, SequenceNumber = i + 1 }));
                var line = await reader.ReadLineAsync(timeout.Token);
                var response = codec.Deserialize(line!);
                Assert.Equal(MessageTypes.EngineState, response.Type);
                Assert.Equal("ready", PipeMessageCodec.ReadPayload<EngineStateEvent>(response)!.State);
            }
            await using var first = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await using var second = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.WhenAll(first.ConnectAsync(timeout.Token), second.ConnectAsync(timeout.Token));
            using var firstReader = new StreamReader(first, Encoding.UTF8, false, 4096, true);
            using var secondReader = new StreamReader(second, Encoding.UTF8, false, 4096, true);
            await using var firstWriter = new StreamWriter(first, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            await using var secondWriter = new StreamWriter(second, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            await Task.WhenAll(
                firstWriter.WriteLineAsync(codec.Serialize(new MessageEnvelope { Type = MessageTypes.Ping, SessionId = "first" })),
                secondWriter.WriteLineAsync(codec.Serialize(new MessageEnvelope { Type = MessageTypes.Ping, SessionId = "second" })));
            var lines = await Task.WhenAll(firstReader.ReadLineAsync(timeout.Token).AsTask(),
                secondReader.ReadLineAsync(timeout.Token).AsTask());
            Assert.Equal("first", codec.Deserialize(lines[0]!).SessionId);
            Assert.Equal("second", codec.Deserialize(lines[1]!).SessionId);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "EgyptianDictation.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
