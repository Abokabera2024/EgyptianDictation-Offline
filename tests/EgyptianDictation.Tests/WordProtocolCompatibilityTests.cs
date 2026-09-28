using System.Runtime.Serialization.Json;
using System.Text;
using EgyptianDictation.Contracts;

namespace EgyptianDictation.Tests;

public sealed class WordProtocolCompatibilityTests
{
    [Fact]
    public void ProductAttributionContainsOwnerAndContact()
    {
        Assert.Contains("د. زكريا ابو كبيره", ProductAttribution.FullNotice);
        Assert.Contains("خبير مكافحة الجرائم المادية والرقمية", ProductAttribution.FullNotice);
        Assert.Contains("الطب الشرعي - مصر", ProductAttribution.FullNotice);
        Assert.Contains("zakariapharm@gmail.com", ProductAttribution.FullNotice);
    }

    [Fact]
    public void DataContractClientReadsCamelCaseHostEnvelope()
    {
        const string json = "{\"protocolVersion\":2,\"type\":\"commit_text\",\"sessionId\":\"s\",\"sequenceNumber\":7,\"correlationId\":\"c\",\"payloadJson\":\"{\\\"text\\\":\\\"اختبار\\\"}\"}";
        var serializer = new DataContractJsonSerializer(typeof(MessageEnvelope));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var envelope = Assert.IsType<MessageEnvelope>(serializer.ReadObject(stream));

        Assert.Equal(MessageTypes.CommitText, envelope.Type);
        Assert.Equal("s", envelope.SessionId);
        Assert.Equal(7, envelope.SequenceNumber);
        Assert.Contains("اختبار", envelope.PayloadJson);
    }

    [Fact]
    public void DataContractClientWritesCamelCaseRequest()
    {
        var serializer = new DataContractJsonSerializer(typeof(MessageEnvelope));
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, new MessageEnvelope { Type = MessageTypes.StartSession, SessionId = "s" });
        var json = Encoding.UTF8.GetString(stream.ToArray());

        Assert.Contains("\"protocolVersion\"", json);
        Assert.Contains("\"type\":\"start_session\"", json);
        Assert.DoesNotContain("\"ProtocolVersion\"", json);
    }

    [Theory]
    [InlineData("", true, "\r")]
    [InlineData("تاريخ \u200E15-3-2025\u200E", true, "\rتاريخ \u200E15-3-2025\u200E ")]
    [InlineData("العام \u200E2026\u200E", false, "العام \u200E2026\u200E ")]
    public void WordInsertionKeepsParagraphCommandAndDateDirection(string text, bool newParagraph, string expected)
    {
        var committed = new TextEvent { Text = text, NewParagraphBefore = newParagraph };
        Assert.Equal(expected, DictationInsertion.Compose(committed));
    }

    [Fact]
    public void DataContractClientReadsParagraphCommandFromHostPayload()
    {
        const string json = "{\"commitId\":\"c\",\"text\":\"\\u200e15-3-2025\\u200e\",\"newParagraphBefore\":true}";
        var serializer = new DataContractJsonSerializer(typeof(TextEvent));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var committed = Assert.IsType<TextEvent>(serializer.ReadObject(stream));

        Assert.True(committed.NewParagraphBefore);
        Assert.Equal("\u200e15-3-2025\u200e", committed.Text);
    }
}
