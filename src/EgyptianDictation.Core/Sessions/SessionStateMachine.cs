namespace EgyptianDictation.Core.Sessions;

public enum DictationState
{
    Ready,
    Loading,
    Listening,
    Stopping,
    Faulted
}

public sealed class SessionStateMachine
{
    public DictationState State { get; private set; } = DictationState.Ready;
    public string? SessionId { get; private set; }

    public void Start(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("A session id is required.", nameof(sessionId));
        if (State != DictationState.Ready)
            throw new InvalidOperationException($"Cannot start from {State}.");

        SessionId = sessionId;
        State = DictationState.Loading;
    }

    public void MarkListening()
    {
        if (State != DictationState.Loading)
            throw new InvalidOperationException($"Cannot listen from {State}.");
        State = DictationState.Listening;
    }

    public void BeginStop()
    {
        if (State is not (DictationState.Listening or DictationState.Loading))
            throw new InvalidOperationException($"Cannot stop from {State}.");
        State = DictationState.Stopping;
    }

    public void CompleteStop()
    {
        if (State != DictationState.Stopping)
            throw new InvalidOperationException($"Cannot complete stop from {State}.");
        SessionId = null;
        State = DictationState.Ready;
    }

    public void Fail() => State = DictationState.Faulted;

    public void Reset()
    {
        SessionId = null;
        State = DictationState.Ready;
    }
}

