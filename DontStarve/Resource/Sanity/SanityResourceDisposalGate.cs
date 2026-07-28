#nullable enable

namespace DontStarve.Resource.Sanity;

internal enum SanityResourceDisposeOrigin
{
    Explicit,
    ProcessExit,
}

internal enum SanityResourceDisposeDecision
{
    BeginOwnedThreadDispose,
    AlreadyDisposed,
    DisposeInProgress,
    WrongThreadRejected,
    ProcessTeardownReclaim,
}

internal readonly record struct SanityResourceDisposeRequest(
    SanityResourceDisposeDecision Decision,
    string Reason,
    bool ShouldWriteDiagnostic
);

/// <summary>
/// Keeps SMAPI/XNA disposal on the thread that created the physical resources. AppDomain
/// ProcessExit runs after SMAPI may have closed its monitor and console writers, so that path
/// must neither log nor touch native resources; the operating system owns final process reclaim.
/// </summary>
internal sealed class SanityResourceDisposalGate
{
    private readonly int owningThreadId;
    private bool disposeInProgress;
    private bool disposed;
    private bool wrongThreadDiagnosticIssued;

    internal SanityResourceDisposalGate(int owningThreadId)
    {
        this.owningThreadId = owningThreadId;
    }

    internal bool IsDisposed => disposed;

    internal SanityResourceDisposeRequest TryBegin(
        SanityResourceDisposeOrigin origin,
        int currentThreadId
    )
    {
        if (disposed)
        {
            return Result(
                SanityResourceDisposeDecision.AlreadyDisposed,
                "resource.dispose.already-completed"
            );
        }

        if (origin == SanityResourceDisposeOrigin.ProcessExit)
        {
            return Result(
                SanityResourceDisposeDecision.ProcessTeardownReclaim,
                "resource.dispose.process-exit-os-reclaim"
            );
        }

        if (disposeInProgress)
        {
            return Result(
                SanityResourceDisposeDecision.DisposeInProgress,
                "resource.dispose.in-progress"
            );
        }

        if (currentThreadId != owningThreadId)
        {
            var shouldWriteDiagnostic = !wrongThreadDiagnosticIssued;
            wrongThreadDiagnosticIssued = true;
            return new SanityResourceDisposeRequest(
                SanityResourceDisposeDecision.WrongThreadRejected,
                "resource.dispose.wrong-thread",
                shouldWriteDiagnostic
            );
        }

        disposeInProgress = true;
        return Result(
            SanityResourceDisposeDecision.BeginOwnedThreadDispose,
            "resource.dispose.owned-thread"
        );
    }

    internal void Complete()
    {
        if (!disposeInProgress)
            throw new System.InvalidOperationException("No resource dispose is in progress.");

        disposeInProgress = false;
        disposed = true;
    }

    internal void Abort()
    {
        if (!disposeInProgress)
            throw new System.InvalidOperationException("No resource dispose is in progress.");

        disposeInProgress = false;
    }

    private static SanityResourceDisposeRequest Result(
        SanityResourceDisposeDecision decision,
        string reason
    )
    {
        return new SanityResourceDisposeRequest(decision, reason, false);
    }
}
