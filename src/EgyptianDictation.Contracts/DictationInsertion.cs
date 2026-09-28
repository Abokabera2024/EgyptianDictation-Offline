namespace EgyptianDictation.Contracts;

public static class DictationInsertion
{
    public static string Compose(TextEvent committed)
    {
        var text = committed.NewParagraphBefore ? "\r" : string.Empty;
        if (!string.IsNullOrWhiteSpace(committed.Text))
        {
            text += committed.Text;
            if (!char.IsWhiteSpace(text[text.Length - 1])) text += " ";
        }
        return text;
    }
}
