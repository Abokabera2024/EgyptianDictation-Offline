namespace EgyptianDictation.Core.Asr;

public interface ITranscriptionEngine : IAsyncDisposable
{
    bool IsReady { get; }
    Task LoadAsync(CancellationToken cancellationToken = default);
    Task<AsrTranscriptionResult> TranscribeAsync(
        string audioPath,
        string sessionId,
        string language = "ar",
        CancellationToken cancellationToken = default);
    Task CancelSessionAsync(string sessionId, CancellationToken cancellationToken = default);
}

public sealed class CohereEngineOptions
{
    public required string WorkerExecutable { get; init; }
    public required string ModelPath { get; init; }
    public int Threads { get; init; }
    public string PreferredBackend { get; init; } = "auto";
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(15);
}
