namespace EgyptianDictation.Core.Arabic;

using System.Text.RegularExpressions;

public enum VoiceCommandKind
{
    InsertText,
    NewLine,
    NewParagraph,
    DeleteLastWord,
    DeleteLastSentence,
    Undo
}

public sealed class VoiceCommandResult
{
    public VoiceCommandKind Kind { get; init; }
    public string Text { get; init; } = string.Empty;
    public bool IsDestructive => Kind is VoiceCommandKind.DeleteLastWord or VoiceCommandKind.DeleteLastSentence or VoiceCommandKind.Undo;
}

public sealed class VoiceCommandProcessor
{
    private readonly ArabicTextNormalizer _normalizer = new();

    private static readonly IReadOnlyDictionary<string, VoiceCommandResult> Commands =
        new Dictionary<string, VoiceCommandResult>(StringComparer.Ordinal)
        {
            ["نقطه"] = Insert("."),
            ["فاصله"] = Insert("،"),
            ["علامه استفهام"] = Insert("؟"),
            ["سطر جديد"] = new() { Kind = VoiceCommandKind.NewLine },
            ["اول السطر"] = new() { Kind = VoiceCommandKind.NewParagraph },
            ["من اول السطر"] = new() { Kind = VoiceCommandKind.NewParagraph },
            ["فقره جديده"] = new() { Kind = VoiceCommandKind.NewParagraph },
            ["امسح اخر كلمه"] = new() { Kind = VoiceCommandKind.DeleteLastWord },
            ["امسح اخر جمله"] = new() { Kind = VoiceCommandKind.DeleteLastSentence },
            ["تراجع"] = new() { Kind = VoiceCommandKind.Undo }
        };

    public bool TryParseExact(string transcript, out VoiceCommandResult result)
    {
        var key = _normalizer.NormalizeForMatching(transcript).TrimEnd('،', '؛', '؟', '.', '!', ':', ' ');
        return Commands.TryGetValue(key, out result!);
    }

    // Only a command at the beginning of a recognized chunk changes formatting.
    // A mention inside ordinary prose remains literal text.
    public bool TryParseLeadingNewParagraph(string transcript, out string remainingText)
    {
        var normalized = _normalizer.NormalizeForDocument(transcript);
        var match = Regex.Match(normalized,
            @"^(?:(?:ابد[أا] )?(?:من )?)?[أاآ]ول السطر(?=$|[\s،؛؟.!:])\s*[،؛؟.!:]?\s*(.*)$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            remainingText = normalized;
            return false;
        }
        remainingText = match.Groups[1].Value.Trim();
        return true;
    }

    private static VoiceCommandResult Insert(string text) =>
        new() { Kind = VoiceCommandKind.InsertText, Text = text };
}

