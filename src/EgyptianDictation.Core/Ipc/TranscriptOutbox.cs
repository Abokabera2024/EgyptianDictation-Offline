using EgyptianDictation.Contracts;

namespace EgyptianDictation.Core.Ipc;

// A commit remains available until Word confirms that insertion succeeded.
public sealed class TranscriptOutbox
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Queue<TextEvent>> _pending = new(StringComparer.Ordinal);

    public TextEvent Enqueue(string sessionId, string text, double audioSeconds, bool newParagraphBefore = false)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("Session is required.", nameof(sessionId));
        var item = new TextEvent
        {
            CommitId = Guid.NewGuid().ToString("N"), Text = text,
            AudioEndSeconds = audioSeconds, NewParagraphBefore = newParagraphBefore
        };
        lock (_sync)
        {
            if (!_pending.TryGetValue(sessionId, out var queue))
                _pending[sessionId] = queue = new Queue<TextEvent>();
            queue.Enqueue(item);
        }
        return item;
    }

    public TextEvent? Peek(string sessionId)
    {
        lock (_sync)
            return _pending.TryGetValue(sessionId, out var queue) && queue.Count > 0 ? queue.Peek() : null;
    }

    public bool Acknowledge(string sessionId, string commitId)
    {
        lock (_sync)
        {
            if (!_pending.TryGetValue(sessionId, out var queue) || queue.Count == 0 ||
                !string.Equals(queue.Peek().CommitId, commitId, StringComparison.Ordinal))
                return false;
            queue.Dequeue();
            if (queue.Count == 0) _pending.Remove(sessionId);
            return true;
        }
    }
}
