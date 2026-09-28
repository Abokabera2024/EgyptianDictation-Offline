using System.Runtime.Serialization;

namespace EgyptianDictation.Contracts;

public static class Protocol
{
    public const int CurrentVersion = 2;
    public const string DefaultPipeName = "EgyptianDictation.v2";
}

public static class OfflineModel
{
    public const string Name = "Cohere Transcribe Arabic 07-2026";
    public const string FileName = "cohere-transcribe-arabic-07-2026-Q5_K_M.gguf";
    public const string Quantization = "Q5_K_M";
    public const long FileSize = 1_770_270_112;
    public const string Sha256 = "55E61C9B047E36F0E084D367F6B0BFECC71A6A0527DA6EEA4F0C687F3584775F";
    public const string RuntimeCommit = "d89ecb75062e8457681c563994675dc60e31db80";
}

public static class MessageTypes
{
    public const string Hello = "hello";
    public const string StartSession = "start_session";
    public const string StopSession = "stop_session";
    public const string CancelSession = "cancel_session";
    public const string GetStatus = "get_status";
    public const string Ping = "ping";
    public const string EngineState = "engine_state";
    public const string PartialText = "partial_text";
    public const string CommitText = "commit_text";
    public const string CommitAck = "commit_ack";
    public const string Command = "command";
    public const string Metrics = "metrics";
    public const string Error = "error";
}

public static class WorkerMessageTypes
{
    public const string Ping = "ping";
    public const string Load = "load";
    public const string Transcribe = "transcribe";
    public const string Cancel = "cancel";
    public const string Shutdown = "shutdown";
}

public static class WorkerStates
{
    public const string NotStarted = "NOT_STARTED";
    public const string LoadingModel = "LOADING_MODEL";
    public const string Ready = "READY";
    public const string Transcribing = "TRANSCRIBING";
    public const string Error = "ERROR";
}

public sealed class WorkerRequest
{
    public int ProtocolVersion { get; set; } = Protocol.CurrentVersion;
    public string Type { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string? ModelPath { get; set; }
    public string? AudioPath { get; set; }
    public string Language { get; set; } = "ar";
    public int Threads { get; set; }
}

public sealed class WorkerResponse
{
    public int ProtocolVersion { get; set; } = Protocol.CurrentVersion;
    public string RequestId { get; set; } = string.Empty;
    public bool Ok { get; set; }
    public string State { get; set; } = WorkerStates.NotStarted;
    public string? Text { get; set; }
    public string? ErrorCode { get; set; }
    public string? Message { get; set; }
    public string? Backend { get; set; }
    public int Threads { get; set; }
    public double ModelLoadSeconds { get; set; }
    public double AudioSeconds { get; set; }
    public double InferenceSeconds { get; set; }
    public double RealTimeFactor { get; set; }
    public long WorkingSetBytes { get; set; }
}

[DataContract]
public sealed class MessageEnvelope
{
    [DataMember(Name = "protocolVersion")]
    public int ProtocolVersion { get; set; } = Protocol.CurrentVersion;
    [DataMember(Name = "type")]
    public string Type { get; set; } = string.Empty;
    [DataMember(Name = "sessionId")]
    public string SessionId { get; set; } = string.Empty;
    [DataMember(Name = "sequenceNumber")]
    public long SequenceNumber { get; set; }
    [DataMember(Name = "correlationId")]
    public string? CorrelationId { get; set; }
    [DataMember(Name = "payloadJson")]
    public string? PayloadJson { get; set; }
}

[DataContract]
public sealed class StartSessionRequest
{
    [DataMember(Name = "language")]
    public string Language { get; set; } = "ar";
    [DataMember(Name = "profile")]
    public string Profile { get; set; } = "egyptian";
    [DataMember(Name = "voiceCommandsEnabled")]
    public bool VoiceCommandsEnabled { get; set; } = true;
}

[DataContract]
public sealed class TextEvent
{
    [DataMember(Name = "commitId")]
    public string CommitId { get; set; } = string.Empty;

    [DataMember(Name = "text")]
    public string Text { get; set; } = string.Empty;
    [DataMember(Name = "newParagraphBefore")]
    public bool NewParagraphBefore { get; set; }
    [DataMember(Name = "audioStartSeconds")]
    public double AudioStartSeconds { get; set; }
    [DataMember(Name = "audioEndSeconds")]
    public double AudioEndSeconds { get; set; }
}

[DataContract]
public sealed class CommitAckRequest
{
    [DataMember(Name = "commitId")]
    public string CommitId { get; set; } = string.Empty;
}

[DataContract]
public sealed class EngineStateEvent
{
    [DataMember(Name = "state")]
    public string State { get; set; } = "ready";
    [DataMember(Name = "detail")]
    public string? Detail { get; set; }
}
