namespace EgyptianDictation.Core.Streaming;

/// <summary>
/// Commits only the prefix shared by two consecutive hypotheses.
/// The caller should pass the complete hypothesis for the active audio buffer.
/// </summary>
public sealed class LocalAgreementCommitter
{
    private string[] _previous = Array.Empty<string>();
    private readonly List<string> _committed = new();

    public string CommittedText => string.Join(" ", _committed);

    public string Observe(string hypothesis)
    {
        var current = Tokenize(hypothesis);
        var commonCount = 0;
        var limit = Math.Min(_previous.Length, current.Length);

        while (commonCount < limit &&
               string.Equals(_previous[commonCount], current[commonCount], StringComparison.Ordinal))
        {
            commonCount++;
        }

        var alreadyCommitted = Math.Min(_committed.Count, commonCount);
        var newlyStable = current.Skip(alreadyCommitted).Take(commonCount - alreadyCommitted).ToArray();
        if (newlyStable.Length > 0)
        {
            _committed.AddRange(newlyStable);
        }

        _previous = current;
        return string.Join(" ", newlyStable);
    }

    public string Flush(string finalHypothesis)
    {
        var final = Tokenize(finalHypothesis);
        var newTokens = final.Skip(Math.Min(_committed.Count, final.Length)).ToArray();
        _committed.AddRange(newTokens);
        _previous = final;
        return string.Join(" ", newTokens);
    }

    public void Reset()
    {
        _previous = Array.Empty<string>();
        _committed.Clear();
    }

    private static string[] Tokenize(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

