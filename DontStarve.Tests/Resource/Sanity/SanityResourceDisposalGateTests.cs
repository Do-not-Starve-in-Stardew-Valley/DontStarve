using DontStarve.Resource.Sanity;
using Xunit;

namespace DontStarve.Tests.Resource.Sanity;

public sealed class SanityResourceDisposalGateTests
{
    [Fact]
    public void OwnedThreadDisposeCompletesExactlyOnce()
    {
        var gate = new SanityResourceDisposalGate(owningThreadId: 17);

        var first = gate.TryBegin(SanityResourceDisposeOrigin.Explicit, 17);
        Assert.Equal(
            SanityResourceDisposeDecision.BeginOwnedThreadDispose,
            first.Decision
        );
        gate.Complete();

        var repeated = gate.TryBegin(SanityResourceDisposeOrigin.Explicit, 17);
        Assert.Equal(SanityResourceDisposeDecision.AlreadyDisposed, repeated.Decision);
        Assert.True(gate.IsDisposed);
    }

    [Fact]
    public void WrongThreadExplicitDisposeUsesOneNormalDiagnosticThenAllowsOwnerRelease()
    {
        var gate = new SanityResourceDisposalGate(owningThreadId: 17);

        var first = gate.TryBegin(SanityResourceDisposeOrigin.Explicit, 23);
        var repeated = gate.TryBegin(SanityResourceDisposeOrigin.Explicit, 23);

        Assert.Equal(SanityResourceDisposeDecision.WrongThreadRejected, first.Decision);
        Assert.Equal("resource.dispose.wrong-thread", first.Reason);
        Assert.True(first.ShouldWriteDiagnostic);
        Assert.Equal(SanityResourceDisposeDecision.WrongThreadRejected, repeated.Decision);
        Assert.False(repeated.ShouldWriteDiagnostic);

        var owner = gate.TryBegin(SanityResourceDisposeOrigin.Explicit, 17);
        Assert.Equal(
            SanityResourceDisposeDecision.BeginOwnedThreadDispose,
            owner.Decision
        );
        gate.Complete();
        Assert.True(gate.IsDisposed);
    }

    [Theory]
    [InlineData(17)]
    [InlineData(23)]
    public void ProcessExitNeverRequestsMonitorOrNativeDisposal(int processExitThreadId)
    {
        var gate = new SanityResourceDisposalGate(owningThreadId: 17);

        var processExit = gate.TryBegin(
            SanityResourceDisposeOrigin.ProcessExit,
            processExitThreadId
        );

        Assert.Equal(
            SanityResourceDisposeDecision.ProcessTeardownReclaim,
            processExit.Decision
        );
        Assert.Equal("resource.dispose.process-exit-os-reclaim", processExit.Reason);
        Assert.False(processExit.ShouldWriteDiagnostic);
        Assert.False(gate.IsDisposed);

        var owner = gate.TryBegin(SanityResourceDisposeOrigin.Explicit, 17);
        Assert.Equal(
            SanityResourceDisposeDecision.BeginOwnedThreadDispose,
            owner.Decision
        );
    }

    [Fact]
    public void FailedOwnedThreadDisposeCanBeRetriedWithoutBeingMarkedComplete()
    {
        var gate = new SanityResourceDisposalGate(owningThreadId: 17);

        Assert.Equal(
            SanityResourceDisposeDecision.BeginOwnedThreadDispose,
            gate.TryBegin(SanityResourceDisposeOrigin.Explicit, 17).Decision
        );
        gate.Abort();
        Assert.False(gate.IsDisposed);

        Assert.Equal(
            SanityResourceDisposeDecision.BeginOwnedThreadDispose,
            gate.TryBegin(SanityResourceDisposeOrigin.Explicit, 17).Decision
        );
        gate.Complete();
        Assert.True(gate.IsDisposed);
    }
}
