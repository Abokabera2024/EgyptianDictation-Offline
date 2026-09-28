using EgyptianDictation.Core.Streaming;

namespace EgyptianDictation.Tests;

public sealed class LocalAgreementCommitterTests
{
    [Fact]
    public void CommitsOnlyPrefixSharedByConsecutiveHypotheses()
    {
        var sut = new LocalAgreementCommitter();

        Assert.Equal(string.Empty, sut.Observe("تبين من فحص"));
        Assert.Equal("تبين من فحص", sut.Observe("تبين من فحص التوقيع"));
        Assert.Equal("التوقيع", sut.Observe("تبين من فحص التوقيع وجود اختلاف"));
        Assert.Equal("وجود اختلاف", sut.Flush("تبين من فحص التوقيع وجود اختلاف"));
    }

    [Fact]
    public void DoesNotCommitChangedTail()
    {
        var sut = new LocalAgreementCommitter();
        sut.Observe("فحص التوقيع");
        Assert.Equal("فحص", sut.Observe("فحص المستند"));
        Assert.Equal("فحص", sut.CommittedText);
    }
}
