using System.Text.RegularExpressions;

namespace EgyptianDictation.Core.Arabic;

public sealed partial class ArabicTextNormalizer
{
    public string NormalizeForDocument(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace('\u00A0', ' ').Trim();
        normalized = NonSpeechBlankMarker().Replace(normalized, " ");
        normalized = DecoderAtMarker().Replace(normalized, " ");
        normalized = RepeatedWhitespace().Replace(normalized, " ");
        normalized = SpaceBeforePunctuation().Replace(normalized, "$1");
        normalized = SpaceAfterPunctuation().Replace(normalized, "$1 ");
        return normalized.Trim();
    }

    public string NormalizeForMatching(string text)
    {
        var normalized = NormalizeForDocument(text).ToLowerInvariant();
        normalized = ArabicDiacritics().Replace(normalized, string.Empty);
        normalized = normalized.Replace('أ', 'ا').Replace('إ', 'ا').Replace('آ', 'ا');
        normalized = normalized.Replace('ى', 'ي');
        normalized = normalized.Replace('ة', 'ه');
        return normalized;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex RepeatedWhitespace();

    [GeneratedRegex(@"\s+([،؛؟.!,:;])")]
    private static partial Regex SpaceBeforePunctuation();

    [GeneratedRegex(@"([،؛؟!,:;]|(?<![A-Za-z0-9])\.)(?=[^\s،؛؟.!,:;])(?![0-9٠-٩۰-۹])")]
    private static partial Regex SpaceAfterPunctuation();

    [GeneratedRegex("[\\u064B-\\u065F\\u0670]")]
    private static partial Regex ArabicDiacritics();

    // A decoder artifact observed during a pause. The @@ prefix makes this
    // distinct from a user dictating the ordinary Arabic word "فراغ".
    [GeneratedRegex(@"@{2,}\s*فراغ", RegexOptions.IgnoreCase)]
    private static partial Regex NonSpeechBlankMarker();

    // A run of two or more @ characters is not part of an email address.
    [GeneratedRegex(@"@{2,}")]
    private static partial Regex DecoderAtMarker();
}
