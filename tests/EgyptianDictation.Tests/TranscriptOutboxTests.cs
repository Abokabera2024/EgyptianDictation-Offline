using EgyptianDictation.Core.Ipc;

namespace EgyptianDictation.Tests;

public sealed class TranscriptOutboxTests
{
    [Fact]
    public void CommitSurvivesRepeatedDeliveryUntilMatchingAcknowledgement()
    {
        var outbox = new TranscriptOutbox();
        var first = outbox.Enqueue("one", "اختبار", 1.2);
        var second = outbox.Enqueue("one", "ثان", 2.0);
        outbox.Enqueue("two", "مستقل", 1.0);

        Assert.Equal(first.CommitId, outbox.Peek("one")!.CommitId);
        Assert.Equal(first.CommitId, outbox.Peek("one")!.CommitId);
        Assert.False(outbox.Acknowledge("two", first.CommitId));
        Assert.False(outbox.Acknowledge("one", second.CommitId));
        Assert.True(outbox.Acknowledge("one", first.CommitId));
        Assert.False(outbox.Acknowledge("one", first.CommitId));
        Assert.Equal(second.CommitId, outbox.Peek("one")!.CommitId);
        Assert.NotNull(outbox.Peek("two"));
    }

    [Fact]
    public void NewParagraphCommandCanBeCommittedWithoutText()
    {
        var outbox = new TranscriptOutbox();
        var item = outbox.Enqueue("session", string.Empty, 1.0, newParagraphBefore: true);
        Assert.True(outbox.Peek("session")!.NewParagraphBefore);
        Assert.Equal(string.Empty, item.Text);
        Assert.True(outbox.Acknowledge("session", item.CommitId));
    }
}
