using EgyptianDictation.Core.Sessions;

namespace EgyptianDictation.Tests;

public sealed class SessionStateMachineTests
{
    [Fact]
    public void HappyPathReturnsToReady()
    {
        var sut = new SessionStateMachine();
        sut.Start("s1");
        sut.MarkListening();
        sut.BeginStop();
        sut.CompleteStop();
        Assert.Equal(DictationState.Ready, sut.State);
        Assert.Null(sut.SessionId);
    }

    [Fact]
    public void RejectsConcurrentSessions()
    {
        var sut = new SessionStateMachine();
        sut.Start("s1");
        Assert.Throws<InvalidOperationException>(() => sut.Start("s2"));
    }
}

