using EgyptianDictation.Core.Arabic;

namespace EgyptianDictation.Tests;

public sealed class ArabicProcessingTests
{
    [Theory]
    [InlineData("  نص   عربي  ، بدون  مسافات ", "نص عربي، بدون مسافات")]
    [InlineData("هل هذا صحيح؟نعم", "هل هذا صحيح؟ نعم")]
    [InlineData("للمضاهاة @@@فراغ شكرا جزيلا", "للمضاهاة شكرا جزيلا")]
    [InlineData("للمضاهاة @@ شكرا جزيلا", "للمضاهاة شكرا جزيلا")]
    [InlineData("@@فراغ", "")]
    [InlineData("@@", "")]
    [InlineData("البريد zakariapharm@gmail.com", "البريد zakariapharm@gmail.com")]
    [InlineData("اكتب كلمة فراغ كما هي", "اكتب كلمة فراغ كما هي")]
    [InlineData("القيمة ١٢٣، والتوقيع صحيح.", "القيمة ١٢٣، والتوقيع صحيح.")]
    public void NormalizesDocumentSpacing(string input, string expected)
    {
        Assert.Equal(expected, new ArabicTextNormalizer().NormalizeForDocument(input));
    }

    [Theory]
    [InlineData("نُقْطَة", VoiceCommandKind.InsertText, ".")]
    [InlineData("علامة استفهام", VoiceCommandKind.InsertText, "؟")]
    [InlineData("امسح آخر كلمة", VoiceCommandKind.DeleteLastWord, "")]
    [InlineData("أول السطر", VoiceCommandKind.NewParagraph, "")]
    [InlineData("من أول السطر.", VoiceCommandKind.NewParagraph, "")]
    public void ParsesExactCommands(string input, VoiceCommandKind kind, string text)
    {
        var parsed = new VoiceCommandProcessor().TryParseExact(input, out var result);
        Assert.True(parsed);
        Assert.Equal(kind, result.Kind);
        Assert.Equal(text, result.Text);
    }

    [Fact]
    public void DoesNotTreatCommandInsideSentenceAsACommand()
    {
        Assert.False(new VoiceCommandProcessor().TryParseExact("قلت له سطر جديد بعدين كمل", out _));
    }

    [Theory]
    [InlineData("أول السطر", "")]
    [InlineData("من اول السطر وبالاطلاع على المستند", "وبالاطلاع على المستند")]
    [InlineData("ابدأ من أول السطر: بتاريخ 15-3-2025", "بتاريخ 15-3-2025")]
    public void LeadingLineCommandRetainsFollowingSpeech(string input, string expected)
    {
        Assert.True(new VoiceCommandProcessor().TryParseLeadingNewParagraph(input, out var remaining));
        Assert.Equal(expected, remaining);
    }

    [Theory]
    [InlineData("وردت عبارة أول السطر في المستند")]
    [InlineData("أول السطرين مختلف")]
    public void DoesNotChangeFormattingForCommandMentionedInProse(string input)
    {
        Assert.False(new VoiceCommandProcessor().TryParseLeadingNewParagraph(input, out var remaining));
        Assert.Equal(input, remaining);
    }

    [Theory]
    [InlineData("السنة 2026", "السنة \u200E2026\u200E")]
    [InlineData("في 15-3-2025 تم الفحص", "في \u200E15-3-2025\u200E تم الفحص")]
    [InlineData("بتاريخ ١٥/٣/٢٠٢٥", "بتاريخ \u200E15/3/2025\u200E")]
    [InlineData("بتاريخ 15.3.2025", "بتاريخ \u200E15.3.2025\u200E")]
    [InlineData("العام ٢٠٢٦ والرقم 1234", "العام \u200E2026\u200E والرقم 1234")]
    public void KeepsDatesLeftToRightInsideArabicText(string input, string expected)
    {
        var normalized = new ArabicTextNormalizer().NormalizeForDocument(input);
        var formatted = ArabicDateFormatter.FormatForDocument(normalized);
        Assert.Equal(expected, formatted);
        Assert.Equal(formatted, ArabicDateFormatter.FormatForDocument(formatted));
    }
}
