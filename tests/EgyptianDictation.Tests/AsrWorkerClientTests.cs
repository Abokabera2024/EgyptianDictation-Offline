using EgyptianDictation.Core.Asr;

namespace EgyptianDictation.Tests;

[Collection("ProcessIntegration")]
public sealed class AsrWorkerClientTests
{
    [Fact]
    public async Task CommunicatesWithPythonWorkerProcess()
    {
        var root = FindRepositoryRoot();
        var audio = Path.Combine(Path.GetTempPath(), $"egyptian-dictation-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(audio, [82, 73, 70, 70]);
        try
        {
            await using var client = new AsrWorkerClient(new AsrWorkerOptions
            {
                PythonExecutable = "python",
                WorkerScript = Path.Combine(root, "src", "AsrWorker", "worker.py"),
                FakeBackend = true,
                FakeText = "تكامل ناجح"
            });
            await client.LoadAsync("fake");
            var result = await client.TranscribeAsync(audio);
            Assert.Equal("تكامل ناجح", result.Text);
            Assert.True(result.RealTimeFactor < 1);
        }
        finally
        {
            File.Delete(audio);
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
