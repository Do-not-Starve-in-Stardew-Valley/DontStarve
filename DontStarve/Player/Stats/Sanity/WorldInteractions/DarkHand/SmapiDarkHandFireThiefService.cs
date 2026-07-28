#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Text;
using StardewModdingAPI;

namespace DontStarve.Player.Stats.Sanity.WorldInteractions.DarkHand;

internal readonly record struct DarkHandFireRuntimeDiagnostic(
    DarkHandFireCapabilityStatus Status,
    string Reason,
    int CandidateTargetCount,
    int EnabledTargetCount,
    bool RuntimeTargetAuthorityInstalled,
    bool WorldMutationHookInstalled,
    int SuccessfulExtinguishCount
);

/// <summary>
/// Startup owner for the strict fire catalog and host revision registry. The shared coordinator
/// installs the verified 1.6.15 Torch adapter and private lease transport after all catalogs load.
/// </summary>
internal sealed class SmapiDarkHandFireThiefService : IDisposable
{
    private readonly IMonitor monitor;
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly DarkHandFireTargetRegistry registry;
    private bool runtimeWired;
    private Func<int> successfulCountProvider = static () => 0;
    private bool disposed;

    internal SmapiDarkHandFireThiefService(
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
            ? DarkHandFireRuntimeEvidence.VerifiedStardew1615
            : DarkHandFireRuntimeEvidence.Current;
        Capability = DarkHandFireCapabilityGate.Evaluate(Catalog, Evidence);
        registry = new DarkHandFireTargetRegistry(Catalog);

        lifecycle.StateEventPublished += OnStateEventPublished;
        lifecycle.WorldBoundaryStarting += OnWorldBoundaryStarting;
        lifecycle.SessionClearing += OnSessionClearing;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        monitor.Log(
            string.Concat(
                "DarkHand FireThief capability: status=",
                Capability.Status.ToString(),
                ", reason=",
                Capability.Reason,
                ", candidates=",
                Catalog.Targets.Count.ToString(CultureInfo.InvariantCulture),
                ", enabledTargets=",
                Capability.EnabledTargetCount.ToString(CultureInfo.InvariantCulture),
                ", stableRevision=",
                Capability.Evidence.StableTargetRevisionAvailable.ToString(),
                ", exactLightBinding=",
                Capability.Evidence.ExplainableLightTargetBindingAvailable.ToString(),
                ", atomicAdapter=",
                Capability.Evidence.AtomicExtinguishAdapterAvailable.ToString(),
                ", adapterReady=",
                Capability.CanExecute.ToString(),
                ", runtime binding is finalized by the shared lease coordinator."
            ),
            Capability.CanExecute ? LogLevel.Warn : LogLevel.Debug
        );
    }

    internal DarkHandFireTargetCatalog Catalog { get; }
    internal DarkHandFireRuntimeEvidence Evidence { get; }
    internal DarkHandFireCapability Capability { get; }
    internal DarkHandFireTargetRegistry Registry => registry;

    internal DarkHandFireRuntimeDiagnostic Diagnostic =>
        new(
            Capability.Status,
            Capability.Reason,
            Catalog.Targets.Count,
            Capability.EnabledTargetCount,
            RuntimeTargetAuthorityInstalled: runtimeWired,
            WorldMutationHookInstalled: runtimeWired,
            SuccessfulExtinguishCount: successfulCountProvider()
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

    private DarkHandFireTargetCatalog LoadCatalog(string modDirectory)
    {
        var path = Path.Combine(
            modDirectory,
            DarkHandFireTargetCatalog.RelativePath.Replace(
                '/',
                Path.DirectorySeparatorChar
            )
        );
        try
        {
            var json = File.ReadAllText(path, new UTF8Encoding(false, true));
            var load = DarkHandFireTargetCatalog.Load(json);
            if (load.IsAvailable)
                return load.Catalog;
            monitor.Log(
                $"DarkHand fire target catalog failed closed (path={DarkHandFireTargetCatalog.RelativePath}, reason={load.Reason}).",
                LogLevel.Error
            );
            return load.Catalog;
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"DarkHand fire target catalog failed closed (path={DarkHandFireTargetCatalog.RelativePath}, reason=dark-hand.fire-targets.file-unavailable, {exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
            return DarkHandFireTargetCatalog.Unavailable(
                "dark-hand.fire-targets.file-unavailable"
            );
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
