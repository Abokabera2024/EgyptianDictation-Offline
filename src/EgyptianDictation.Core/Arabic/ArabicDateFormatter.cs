using System.Text.RegularExpressions;

namespace EgyptianDictation.Core.Arabic;

public static partial class ArabicDateFormatter
{
    private const char LeftToRightMark = '\u200E';

    public static string FormatForDocument(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return NumericDateOrYear().Replace(text, match =>
        {
            var value = NormalizeDigits(match.Value);
            if (match.Groups["year"].Success &&
                !(value.StartsWith("19", StringComparison.Ordinal) ||
                  value.StartsWith("20", StringComparison.Ordinal)))
                return match.Value;
            if (match.Groups["date"].Success)
            {
                var parts = DateSeparator().Split(value);
                var separator = DateSeparator().Match(value).Value.Trim()[0];
                value = string.Join(separator.ToString(), parts.Select(part => part.Trim()));
            }
            return $"{LeftToRightMark}{value}{LeftToRightMark}";
        });
    }

    private static string NormalizeDigits(string value)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= '\u0660' and <= '\u0669')
                chars[i] = (char)('0' + chars[i] - '\u0660');
            else if (chars[i] is >= '\u06F0' and <= '\u06F9')
                chars[i] = (char)('0' + chars[i] - '\u06F0');
        }
        return new string(chars);
    }

    [GeneratedRegex(@"(?<![\d\u200E])(?:(?<date>\d{1,2}\s*[-/.]\s*\d{1,2}\s*[-/.]\s*\d{4}|\d{4}\s*[-/.]\s*\d{1,2}\s*[-/.]\s*\d{1,2})|(?<year>\d{4}))(?![\d\u200E])")]
    private static partial Regex NumericDateOrYear();

    [GeneratedRegex(@"\s*[-/.]\s*")]
    private static partial Regex DateSeparator();
}
