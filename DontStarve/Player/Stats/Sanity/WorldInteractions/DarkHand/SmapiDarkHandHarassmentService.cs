#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Text;
using StardewModdingAPI;

namespace DontStarve.Player.Stats.Sanity.WorldInteractions.DarkHand;

internal readonly record struct DarkHandHarassmentRuntimeDiagnostic(
    MachineInteractionCapabilityStatus Status,
    string Reason,
    int CandidateTargetCount,
    int EnabledTargetCount,
    bool StableAuthorityRevisionAvailable,
    bool SafeItemLandingAdapterAvailable,
    bool RuntimeTargetAuthorityInstalled,
    bool WorldMutationHookInstalled,
    int SuccessfulTransactionCount
);

/// <summary>
/// Startup owner for the strict machine catalog and shared snapshot registry. Production enables
/// finite Loom delay; eject remains closed because no atomic landing reservation is installed.
/// </summary>
internal sealed class SmapiDarkHandHarassmentService : IDisposable
{
    private readonly IMonitor monitor;
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly MachineSnapshotRegistry registry = new();
    private bool runtimeWired;
    private Func<int> successfulCountProvider = static () => 0;
    private bool disposed;

    internal SmapiDarkHandHarassmentService(
        IModHelper helper,
        IMonitor monitor,
        SanitySystemLifecycleCoordinator lifecycle
    )
    {
        ArgumentNullException.ThrowIfNull(helper);
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));

        Catalog = LoadCatalog(helper.DirectoryPath);
        Evidence = DarkHandStardewVersionGate.IsVerified1615
            ? MachineRuntimeEvidence.VerifiedStardew1615
            : MachineRuntimeEvidence.Current;
        Capability = MachineInteractionCapabilityGate.Evaluate(Catalog, Evidence);

        lifecycle.StateEventPublished += OnStateEventPublished;
        lifecycle.WorldBoundaryStarting += OnWorldBoundaryStarting;
        lifecycle.SessionClearing += OnSessionClearing;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        monitor.Log(
            string.Concat(
                "DarkHand Harassment capability: status=",
                Capability.Status.ToString(),
                ", reason=",
                Capability.Reason,
                ", candidates=",
                Catalog.Targets.Count.ToString(CultureInfo.InvariantCulture),
                ", enabledTargets=",
                Capability.EnabledTargetCount.ToString(CultureInfo.InvariantCulture),
                ", stableRevision=",
                Evidence.StableAuthorityRevisionAvailable.ToString(),
                ", safeLanding=",
                Evidence.SafeItemLandingAdapterAvailable.ToString(),
                ", canDelay=",
                Capability.CanCommitDelay.ToString(),
                ", canEject=",
                Capability.CanCommitEject.ToString(),
                ", adapterReady=",
                Capability.CanCommitDelay.ToString(),
                ", runtime binding is finalized by the shared lease coordinator."
            ),
            Capability.CanCommitDelay || Capability.CanCommitEject
                ? LogLevel.Warn
                : LogLevel.Debug
        );
    }

    internal MachineTargetCatalog Catalog { get; }
    internal MachineRuntimeEvidence Evidence { get; }
    internal MachineInteractionCapability Capability { get; }
    internal MachineSnapshotRegistry Registry => registry;

    internal DarkHandHarassmentRuntimeDiagnostic Diagnostic =>
        new(
            Capability.Status,
            Capability.Reason,
            Catalog.Targets.Count,
            Capability.EnabledTargetCount,
            Evidence.StableAuthorityRevisionAvailable,
            Evidence.SafeItemLandingAdapterAvailable,
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

    private MachineTargetCatalog LoadCatalog(string modDirectory)
    {
        var path = Path.Combine(
            modDirectory,
            MachineTargetCatalog.RelativePath.Replace('/', Path.DirectorySeparatorChar)
        );
        try
        {
            var json = File.ReadAllText(path, new UTF8Encoding(false, true));
            var load = MachineTargetCatalog.Load(json);
            if (load.IsAvailable)
                return load.Catalog;
            monitor.Log(
                $"DarkHand machine catalog failed closed (path={MachineTargetCatalog.RelativePath}, reason={load.Reason}).",
                LogLevel.Error
            );
            return load.Catalog;
        }
        catch (Exception exception)
        {
            const string reason = "dark-hand.machine-targets.file-unavailable";
            monitor.Log(
                $"DarkHand machine catalog failed closed (path={MachineTargetCatalog.RelativePath}, reason={reason}, {exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
            return MachineTargetCatalog.Unavailable(reason);
        }
    }

    private void OnStateEventPublished(SanityStateEvent stateEvent)
    {
        if (
            !disposed
            && stateEvent.Kind
                is SanityStateEventKind.OwnerInvalidated
                    or SanityStateEventKind.SystemDisabled
                    or SanityStateEventKind.WorldCleanup
        )
        {
            registry.Clear();
        }
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
