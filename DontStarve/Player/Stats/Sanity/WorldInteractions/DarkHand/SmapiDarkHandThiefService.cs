#nullable enable

using System;
using System.Globalization;
using StardewModdingAPI;

namespace DontStarve.Player.Stats.Sanity.WorldInteractions.DarkHand;

internal readonly record struct DarkHandThiefRuntimeDiagnostic(
    DarkHandThiefCapabilityStatus Status,
    string Reason,
    int CandidateTargetCount,
    int EnabledTargetCount,
    int AuthorizedTargetCount,
    bool StableAuthorityRevisionAvailable,
    bool AtomicContentDeletionAdapterAvailable,
    bool RuntimeTargetAuthorityInstalled,
    bool WorldMutationHookInstalled,
    int SuccessfulTransactionCount
);

/// <summary>
/// Durable Thief capability owner. It reuses Harassment's startup catalog/evidence and is reachable
/// only through the explicit Thief mode plus the exact Loom content-deletion allowlist.
/// </summary>
internal sealed class SmapiDarkHandThiefService : IDisposable
{
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly MachineSnapshotRegistry registry = new();
    private bool runtimeWired;
    private Func<int> successfulCountProvider = static () => 0;
    private bool disposed;

    internal SmapiDarkHandThiefService(
        IMonitor monitor,
        SanitySystemLifecycleCoordinator lifecycle,
        MachineTargetCatalog catalog,
        MachineRuntimeEvidence evidence
    )
    {
        ArgumentNullException.ThrowIfNull(monitor);
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Evidence = evidence;
        Capability = DarkHandThiefCapabilityGate.Evaluate(Catalog, Evidence);

        lifecycle.StateEventPublished += OnStateEventPublished;
        lifecycle.WorldBoundaryStarting += OnWorldBoundaryStarting;
        lifecycle.SessionClearing += OnSessionClearing;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        monitor.Log(
            string.Concat(
                "DarkHand Thief capability: status=",
                Capability.Status.ToString(),
                ", reason=",
                Capability.Reason,
                ", candidates=",
                Capability.CandidateTargetCount.ToString(CultureInfo.InvariantCulture),
                ", enabledTargets=",
                Capability.EnabledTargetCount.ToString(CultureInfo.InvariantCulture),
                ", authorizedTargets=",
                Capability.AuthorizedTargetCount.ToString(CultureInfo.InvariantCulture),
                ", stableRevision=",
                Evidence.StableAuthorityRevisionAvailable.ToString(),
                ", atomicDeletion=",
                Evidence.AtomicContentDeletionAdapterAvailable.ToString(),
                ", adapterReady=",
                Capability.CanCommitDelete.ToString(),
                ", runtime binding is finalized by the shared lease coordinator."
            ),
            Capability.CanCommitDelete ? LogLevel.Warn : LogLevel.Debug
        );
    }

    internal MachineTargetCatalog Catalog { get; }
    internal MachineRuntimeEvidence Evidence { get; }
    internal DarkHandThiefCapability Capability { get; }

    internal DarkHandThiefRuntimeDiagnostic Diagnostic =>
        new(
            Capability.Status,
            Capability.Reason,
            Capability.CandidateTargetCount,
            Capability.EnabledTargetCount,
            Capability.AuthorizedTargetCount,
            Evidence.StableAuthorityRevisionAvailable,
            Evidence.AtomicContentDeletionAdapterAvailable,
            RuntimeTargetAuthorityInstalled: runtimeWired,
            WorldMutationHookInstalled: runtimeWired,
            SuccessfulTransactionCount: successfulCountProvider()
        );

    internal void MarkRuntimeWired(bool wired, Func<int> successfulCount)
    {
        runtimeWired = wired;
        successfulCountProvider = successfulCount
            ?? throw new ArgumentNullException(nameof(successfulCount));
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        lifecycle.StateEventPublished -= OnStateEventPublished;
        lifecycle.WorldBoundaryStarting -= OnWorldBoundaryStarting;
        lifecycle.SessionClearing -= OnSessionClearing;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        registry.Clear();
    }

    private void OnStateEventPublished(SanityStateEvent stateEvent)
    {
        if (!disposed && stateEvent.Kind is SanityStateEventKind.OwnerInvalidated
            or SanityStateEventKind.SystemDisabled
            or SanityStateEventKind.WorldCleanup)
            registry.Clear();
    }

    private void OnWorldBoundaryStarting(SanityWorldBoundary boundary)
    {
        if (!disposed)
            registry.Clear();
    }

    private void OnSessionClearing(SanitySessionBoundary boundary)
    {
        if (!disposed)
            registry.Clear();
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        Dispose();
    }
}
