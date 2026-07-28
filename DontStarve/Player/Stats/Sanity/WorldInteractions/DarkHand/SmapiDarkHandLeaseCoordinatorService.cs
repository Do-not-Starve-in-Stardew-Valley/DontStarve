#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using DontStarve.Player.Stats.Sanity.Illusions.Projection;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.WorldInteractions.DarkHand;

internal enum DarkHandLeaseCoordinatorCapabilityStatus
{
    Available,
    PrivateOwnerTransportUnavailable,
    OwnerCommitTransportUnavailable,
    OperationBindingsUnavailable,
}

internal readonly record struct DarkHandLeaseCoordinatorRuntimeDiagnostic(
    DarkHandLeaseCoordinatorCapabilityStatus Status,
    string Reason,
    int BoundOperationCount,
    bool SharedTask07AuthorityReused,
    bool PrivateOwnerLeaseTransportInstalled,
    bool OwnerLocalCommitTransportInstalled,
    bool ObserverLeaseBroadcastInstalled,
    bool WorldMutationHookInstalled,
    int SuccessfulTransactionCount
);

internal readonly record struct DarkHandRuntimeConfigIdentity(
    int SchemaVersion,
    string Fingerprint
)
{
    internal bool IsAvailable =>
        SchemaVersion > 0
        && Fingerprint.Length == 64
        && DarkHandInteractionLeaseProtocol.IsIdentifier(Fingerprint);

    internal static DarkHandRuntimeConfigIdentity Unavailable => new(0, string.Empty);
}

/// <summary>
/// Production owner-local observer plus host-only two-phase transaction router. It binds to the
/// existing hostile-shadow transport and consumes that host's one DarkHandInteractionLeaseAuthority;
/// it never creates a second nonce/session/lease authority.
/// </summary>
internal sealed class SmapiDarkHandLeaseCoordinatorService
    : IDarkHandLocalTargetObserver,
        IDarkHandInteractionTransportHandler,
        IDisposable
{
    internal const int MaximumPendingOwners = 16;
    internal const int MaximumIssuedBindings = 256;
    internal const int MaximumTerminalResults = 256;
    internal const int MaximumCooldowns = 256;
    internal const long LocalRequestRetryTicks = 120;
    internal const long MutationCooldownTicks = 600;

    private sealed record IssuedBinding(
        DarkHandInteractionLease Lease,
        string ConfigFingerprint,
        int ConfigSchemaVersion,
        string RuleRevision,
        string ModeId
    );

    private sealed record LocalPending(
        string TargetId,
        string OperationId,
        long RetryAtTick
    );

    private readonly IMonitor monitor;
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly SmapiHostileShadowHost transport;
    private readonly IDarkHandModeResolver modeResolver;
    private readonly Func<DarkHandRuntimeConfigIdentity> configIdentityProvider;
    private readonly DarkHandInteractionLeaseAuthority sharedLeaseAuthority;
    private readonly SmapiDarkHandWorldTransactionAdapters adapters;
    private readonly DarkHandLeaseCoordinator coordinator;
    private readonly SmapiDarkHandFireThiefService fire;
    private readonly SmapiDarkHandHarassmentService harassment;
    private readonly SmapiDarkHandThiefService thief;
    private readonly Dictionary<string, IssuedBinding> issuedBindings =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, DarkHandInteractionCommitResult> terminalResults =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, LocalPending> localPending =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> cooldownUntil =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> loggedReasons = new(StringComparer.Ordinal);
    private bool transportBound;
    private int successfulTransactions;
    private int successfulFireTransactions;
    private int successfulHarassmentTransactions;
    private int successfulThiefTransactions;
    private bool disposed;

    internal SmapiDarkHandLeaseCoordinatorService(
        IMonitor monitor,
        SanitySystemLifecycleCoordinator lifecycle,
        SmapiHostileShadowHost transport,
        IDarkHandModeResolver modeResolver,
        Func<DarkHandRuntimeConfigIdentity> configIdentityProvider,
        SmapiDarkHandFireThiefService fire,
        SmapiDarkHandHarassmentService harassment,
        SmapiDarkHandThiefService thief
    )
    {
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.modeResolver = modeResolver
            ?? throw new ArgumentNullException(nameof(modeResolver));
        this.configIdentityProvider = configIdentityProvider
            ?? throw new ArgumentNullException(nameof(configIdentityProvider));
        this.fire = fire ?? throw new ArgumentNullException(nameof(fire));
        this.harassment = harassment
            ?? throw new ArgumentNullException(nameof(harassment));
        this.thief = thief ?? throw new ArgumentNullException(nameof(thief));
        sharedLeaseAuthority = transport.DarkHandLeaseAuthority;

        adapters = new SmapiDarkHandWorldTransactionAdapters(
            fire.Catalog,
            fire.Capability,
            fire.Registry,
            harassment.Catalog,
            harassment.Evidence,
            harassment.Registry
        );
        var fireOperation = new DarkHandFireThiefOperationService(
            fire.Catalog,
            fire.Capability,
            sharedLeaseAuthority,
            fire.Registry
        );
        var harassmentOperation = new DarkHandHarassmentOperationService(
            harassment.Catalog,
            harassment.Capability,
            sharedLeaseAuthority,
            harassment.Registry
        );
        var thiefOperation = new DarkHandThiefOperationService(
            harassment.Catalog,
            thief.Capability,
            sharedLeaseAuthority,
            harassment.Registry
        );
        coordinator = new DarkHandLeaseCoordinator(
            sharedLeaseAuthority,
            new IDarkHandLeaseOperationBinding[]
            {
                new DarkHandFireThiefLeaseOperationBinding(fireOperation, adapters),
                new DarkHandHarassmentLeaseOperationBinding(
                    harassmentOperation,
                    new SmapiDarkHandHarassmentRandomSource(),
                    adapters
                ),
                new DarkHandThiefLeaseOperationBinding(thiefOperation, adapters),
            }
        );

        transportBound = transport.BindDarkHandInteractionHandler(this, out var bindReason);
        fire.MarkRuntimeWired(
            transportBound && fire.Capability.CanExecute,
            () => successfulFireTransactions
        );
        harassment.MarkRuntimeWired(
            transportBound && harassment.Capability.CanCommitDelay,
            () => successfulHarassmentTransactions
        );
        thief.MarkRuntimeWired(
            transportBound && thief.Capability.CanCommitDelete,
            () => successfulThiefTransactions
        );
        lifecycle.StateEventPublished += OnStateEventPublished;
        lifecycle.WorldBoundaryStarting += OnWorldBoundaryStarting;
        lifecycle.SessionClearing += OnSessionClearing;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        LogOnce(
            bindReason,
            transportBound ? LogLevel.Debug : LogLevel.Error
        );
        monitor.Log(
            string.Concat(
                "DarkHand world transaction capability: status=",
                Diagnostic.Status.ToString(),
                ", version=",
                DarkHandStardewVersionGate.IsVerified1615
                    ? DarkHandStardewVersionGate.VerifiedFileVersion
                    : "unsupported",
                ", bindings=",
                coordinator.BindingCount.ToString(CultureInfo.InvariantCulture),
                ", fire=",
                fire.Capability.Status.ToString(),
                ", harassment=",
                harassment.Capability.Status.ToString(),
                ", thief=",
                thief.Capability.Status.ToString(),
                ", privateLeaseTransport=",
                transportBound.ToString(),
                ", ownerCommitTransport=",
                transportBound.ToString(),
                "."
            ),
            Diagnostic.Status == DarkHandLeaseCoordinatorCapabilityStatus.Available
                ? LogLevel.Debug
                : LogLevel.Error
        );
    }

    internal DarkHandLeaseCoordinatorRuntimeDiagnostic Diagnostic
    {
        get
        {
            var available =
                transportBound
                && coordinator.BindingCount == DarkHandLeaseCoordinator.MaximumBindings
                && fire.Capability.CanExecute
                && harassment.Capability.CanCommitDelay
                && thief.Capability.CanCommitDelete;
            return new DarkHandLeaseCoordinatorRuntimeDiagnostic(
                available
                    ? DarkHandLeaseCoordinatorCapabilityStatus.Available
                    : !transportBound
                        ? DarkHandLeaseCoordinatorCapabilityStatus.PrivateOwnerTransportUnavailable
                        : DarkHandLeaseCoordinatorCapabilityStatus.OperationBindingsUnavailable,
                available
                    ? "dark-hand.lease-coordinator.available"
                    : !transportBound
                        ? "dark-hand.lease-coordinator.transport-unavailable"
                        : "dark-hand.lease-coordinator.operation-capability-unavailable",
                coordinator.BindingCount,
                SharedTask07AuthorityReused: true,
                PrivateOwnerLeaseTransportInstalled: transportBound,
                OwnerLocalCommitTransportInstalled: transportBound,
                ObserverLeaseBroadcastInstalled: transportBound,
                WorldMutationHookInstalled: available,
                successfulTransactions
            );
        }
    }

    public DarkHandLocalTargetObservation Observe(
        HarmlessProjectionOwnerContext owner,
        HarmlessProjectionWorldPoint ownerStandingWorldPixel,
        DarkHandProjectionMode mode
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (
            disposed
            || !transportBound
            || owner.LocationReference is not GameLocation location
            || !TryValidateLocalOwner(owner, location, out var player)
        )
        {
            return DarkHandLocalTargetObservation.Unavailable(
                "dark-hand.owner-local-observer-context-unavailable"
            );
        }
        if (
            !adapters.TryFindNearest(
                location,
                ownerStandingWorldPixel,
                mode,
                out var observed,
                out var reason
            )
            || observed is null
        )
        {
            return DarkHandLocalTargetObservation.Unavailable(reason);
        }

        var pendingKey = string.Concat(
            owner.PlayerKey,
            "|",
            owner.ScreenId.ToString(CultureInfo.InvariantCulture)
        );
        var now = Game1.ticks;
        var shouldRequest =
            !localPending.TryGetValue(pendingKey, out var pending)
            || !string.Equals(
                pending.TargetId,
                observed.TargetId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                pending.OperationId,
                observed.OperationId,
                StringComparison.Ordinal
            )
            || now >= pending.RetryAtTick;
        if (shouldRequest)
        {
            if (
                localPending.Count >= MaximumPendingOwners
                && !localPending.ContainsKey(pendingKey)
            )
            {
                return DarkHandLocalTargetObservation.Rejected(
                    "dark-hand.local-pending-cap-reached"
                );
            }
            if (
                !transport.RequestDarkHandInteractionLease(
                    observed.TargetId,
                    observed.OperationId,
                    observed.Revision,
                    out reason
                )
            )
            {
                return DarkHandLocalTargetObservation.Rejected(reason);
            }
            localPending[pendingKey] = new LocalPending(
                observed.TargetId,
                observed.OperationId,
                unchecked(now + LocalRequestRetryTicks)
            );
        }

        return DarkHandLocalTargetObservation.FrozenExplainable(
            observed.TargetId,
            observed.OperationId,
            observed.Revision,
            observed.WorldPixel,
            "dark-hand.target-private-lease-requested"
        );
    }

    public DarkHandLocalActionFeedback Commit(
        HarmlessProjectionOwnerContext owner,
        string targetId,
        string operationId,
        long targetRevision
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        var reason = "dark-hand.commit-not-submitted";
        if (
            disposed
            || !transportBound
            || targetRevision <= 0
            || !transport.CommitDarkHandInteraction(
                targetId,
                operationId,
                out reason
            )
        )
        {
            return new DarkHandLocalActionFeedback(
                Submitted: false,
                Applied: false,
                "Rejected",
                string.IsNullOrWhiteSpace(reason)
                    ? "dark-hand.commit-not-submitted"
                    : reason
            );
        }

        if (
            transport.TryTakeDarkHandCommitResult(
                targetId,
                operationId,
                out var result
            )
            && result is not null
        )
        {
            var applied = string.Equals(
                result.Disposition,
                DarkHandLeaseBoundCommitDisposition.Applied.ToString(),
                StringComparison.Ordinal
            );
            return new DarkHandLocalActionFeedback(
                Submitted: true,
                applied,
                applied ? "Applied" : result.Disposition,
                result.Reason
            );
        }
        return new DarkHandLocalActionFeedback(
            Submitted: true,
            Applied: false,
            "Requested",
            reason
        );
    }

    public bool TryTakeFeedback(
        HarmlessProjectionOwnerContext owner,
        string targetId,
        string operationId,
        out DarkHandLocalActionFeedback feedback
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (
            disposed
            || !transport.TryTakeDarkHandCommitResult(
                targetId,
                operationId,
                out var result
            )
            || result is null
        )
        {
            feedback = default;
            return false;
        }
        var applied = string.Equals(
            result.Disposition,
            DarkHandLeaseBoundCommitDisposition.Applied.ToString(),
            StringComparison.Ordinal
        );
        feedback = new DarkHandLocalActionFeedback(
            Submitted: true,
            applied,
            applied ? "Applied" : result.Disposition,
            result.Reason
        );
        return true;
    }

    public DarkHandLeaseIssueResult HandleLeaseRequest(
        DarkHandInteractionLeaseRequest request,
        long senderPlayerId
    )
    {
        var expectedOwner = SanityPlayerKey.FromUniqueMultiplayerId(senderPlayerId);
        var player = Game1.GetPlayer(senderPlayerId, onlyOnline: true);
        var reason = "dark-hand.lease-host-revalidation-failed";
        var modeId = string.Empty;
        long revision = 0;
        var distanceTiles = double.PositiveInfinity;
        var context = default(DarkHandLeaseOwnerContext);
        var configIdentity = DarkHandRuntimeConfigIdentity.Unavailable;
        if (
            disposed
            || !transportBound
            || player?.currentLocation is null
            || !DarkHandInteractionLeaseProtocol.IsValidRequest(
                request,
                expectedOwner,
                lifecycle.SessionId,
                out reason
            )
            || !string.Equals(
                request.LocationId,
                player.currentLocation.NameOrUniqueName,
                StringComparison.Ordinal
            )
            || !TryResolveCurrentMode(out modeId, out reason)
            || !OperationMatchesMode(request.OperationId, modeId)
            || !adapters.TryPrepareHostTarget(
                player,
                request.TargetId,
                request.OperationId,
                out revision,
                out distanceTiles,
                out reason
            )
            || !TryBuildHostContext(
                player,
                expectedOwner,
                distanceTiles,
                modeId,
                out context,
                out configIdentity,
                out reason
            )
            || IsCoolingDown(expectedOwner, request.TargetId, Game1.ticks)
            || !CanTrackCooldown(expectedOwner, request.TargetId)
            || issuedBindings.Count >= MaximumIssuedBindings
        )
        {
            return IssueRejected(
                issuedBindings.Count >= MaximumIssuedBindings
                    ? "dark-hand.issued-binding-window-full"
                    : !CanTrackCooldown(
                        expectedOwner,
                        request?.TargetId ?? string.Empty
                    )
                        ? "dark-hand.cooldown-window-full"
                        : IsCoolingDown(expectedOwner, request?.TargetId ?? string.Empty, Game1.ticks)
                        ? "dark-hand.target-cooldown-active"
                        : string.IsNullOrWhiteSpace(reason)
                            ? "dark-hand.lease-host-revalidation-failed"
                            : reason
            );
        }

        var authoritativeRequest = new DarkHandInteractionLeaseRequest
        {
            ProtocolVersion = request.ProtocolVersion,
            SchemaVersion = request.SchemaVersion,
            SessionId = request.SessionId,
            Nonce = request.Nonce,
            OwnerPlayerKey = expectedOwner,
            LocationId = player.currentLocation.NameOrUniqueName,
            TargetId = request.TargetId,
            OperationId = request.OperationId,
            ObservedTargetRevision = revision,
        };
        var result = coordinator.TryIssue(
            authoritativeRequest,
            context,
            Game1.ticks
        );
        if (!result.Issued || result.Lease is null)
            return result;

        issuedBindings.Add(
            result.Lease.LeaseId,
            new IssuedBinding(
                result.Lease.Clone(),
                configIdentity.Fingerprint,
                configIdentity.SchemaVersion,
                RuleRevision(result.Lease.OperationId),
                modeId
            )
        );
        return result;
    }

    public DarkHandInteractionCommitResult HandleCommitRequest(
        DarkHandInteractionLeaseCommitRequest request,
        long senderPlayerId
    )
    {
        var expectedOwner = SanityPlayerKey.FromUniqueMultiplayerId(senderPlayerId);
        if (
            terminalResults.TryGetValue(request?.LeaseId ?? string.Empty, out var terminal)
            && terminal.Nonce == request!.Nonce
            && string.Equals(
                terminal.OwnerPlayerKey,
                expectedOwner,
                StringComparison.Ordinal
            )
        )
        {
            return CopyResult(
                terminal,
                DarkHandLeaseBoundCommitDisposition.Duplicate.ToString(),
                "dark-hand.commit-result-duplicate",
                mutationApplied: false
            );
        }
        IssuedBinding? binding = null;
        if (
            disposed
            || request is null
            || !issuedBindings.TryGetValue(request.LeaseId, out binding)
            || binding.Lease.Nonce != request.Nonce
            || !string.Equals(
                binding.Lease.OwnerPlayerKey,
                expectedOwner,
                StringComparison.Ordinal
            )
        )
        {
            return RejectedResult(
                binding?.Lease,
                expectedOwner,
                request?.Nonce ?? 0,
                "dark-hand.commit-binding-invalid"
            );
        }

        var player = Game1.GetPlayer(senderPlayerId, onlyOnline: true);
        var reason = string.Empty;
        if (
            player?.currentLocation is null
            || !adapters.RefreshForCommit(
                binding.Lease.TargetId,
                binding.Lease.OperationId,
                out _,
                out var distanceTiles,
                out reason
            )
            || !TryResolveCurrentMode(out var modeId, out reason)
            || !TryBuildHostContext(
                player,
                expectedOwner,
                distanceTiles,
                modeId,
                out var context,
                out var configIdentity,
                out reason
            )
        )
        {
            RetireOutstanding(request, expectedOwner);
            return Remember(
                RejectedResult(
                    binding.Lease,
                    expectedOwner,
                    request.Nonce,
                    string.IsNullOrWhiteSpace(reason)
                        ? "dark-hand.commit-host-revalidation-failed"
                        : reason
                )
            );
        }
        if (
            !string.Equals(binding.ModeId, modeId, StringComparison.Ordinal)
            || binding.ConfigSchemaVersion != configIdentity.SchemaVersion
            || !string.Equals(
                binding.ConfigFingerprint,
                configIdentity.Fingerprint,
                StringComparison.Ordinal
            )
            || !string.Equals(
                binding.RuleRevision,
                RuleRevision(binding.Lease.OperationId),
                StringComparison.Ordinal
            )
            || IsCoolingDown(expectedOwner, binding.Lease.TargetId, Game1.ticks)
            || !CanTrackCooldown(expectedOwner, binding.Lease.TargetId)
        )
        {
            RetireOutstanding(request, expectedOwner);
            return Remember(
                RejectedResult(
                    binding.Lease,
                    expectedOwner,
                    request.Nonce,
                    !CanTrackCooldown(expectedOwner, binding.Lease.TargetId)
                        ? "dark-hand.cooldown-window-full"
                        : IsCoolingDown(
                        expectedOwner,
                        binding.Lease.TargetId,
                        Game1.ticks
                    )
                        ? "dark-hand.target-cooldown-active"
                        : "dark-hand.commit-config-mode-or-rule-drifted"
                )
            );
        }

        var commit = coordinator.Commit(request, context, Game1.ticks);
        if (commit.Disposition == DarkHandLeaseBoundCommitDisposition.Rejected)
            RetireOutstanding(request, expectedOwner);
        if (
            commit.Disposition == DarkHandLeaseBoundCommitDisposition.Applied
            && commit.WorldMutationApplied
        )
        {
            successfulTransactions++;
            if (
                string.Equals(
                    binding.Lease.OperationId,
                    DarkHandFireOperationIds.Extinguish,
                    StringComparison.Ordinal
                )
            )
            {
                successfulFireTransactions++;
            }
            else if (
                string.Equals(
                    binding.Lease.OperationId,
                    DarkHandHarassmentOperationIds.Harassment,
                    StringComparison.Ordinal
                )
            )
            {
                successfulHarassmentTransactions++;
            }
            else
            {
                successfulThiefTransactions++;
            }
            SetCooldown(expectedOwner, binding.Lease.TargetId, Game1.ticks);
        }
        return Remember(
            new DarkHandInteractionCommitResult
            {
                SessionId = binding.Lease.SessionId,
                LeaseId = binding.Lease.LeaseId,
                Nonce = binding.Lease.Nonce,
                OwnerPlayerKey = binding.Lease.OwnerPlayerKey,
                TargetId = binding.Lease.TargetId,
                OperationId = binding.Lease.OperationId,
                Disposition = commit.Disposition.ToString(),
                Reason = commit.Reason,
                WorldMutationApplied = commit.WorldMutationApplied,
            }
        );
    }

    public void HandleOwnerContextInvalidated(long playerId)
    {
        if (disposed || playerId <= 0)
            return;

        var ownerPlayerKey = SanityPlayerKey.FromUniqueMultiplayerId(playerId);
        // The hostile-shadow session lifecycle has already cleared this disconnected/warped owner
        // from the one shared lease authority. Remove the matching stage-05 windows as the other
        // half of that boundary. Clearing operation receipts is safe for other owners because their
        // authority leases remain current and every later commit still performs full host revalidation.
        coordinator.ClearOperationWindows();
        RemoveOwnerEntries(issuedBindings, pair =>
            string.Equals(
                pair.Value.Lease.OwnerPlayerKey,
                ownerPlayerKey,
                StringComparison.Ordinal
            )
        );
        RemoveOwnerEntries(terminalResults, pair =>
            string.Equals(
                pair.Value.OwnerPlayerKey,
                ownerPlayerKey,
                StringComparison.Ordinal
            )
        );
        RemoveOwnerEntries(localPending, pair =>
            pair.Key.StartsWith(
                string.Concat(ownerPlayerKey, "|"),
                StringComparison.Ordinal
            )
        );
        RemoveOwnerEntries(cooldownUntil, pair =>
            pair.Key.StartsWith(
                string.Concat(ownerPlayerKey, "|"),
                StringComparison.Ordinal
            )
        );
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
        ClearWindow();
        fire.MarkRuntimeWired(false, static () => 0);
        harassment.MarkRuntimeWired(false, static () => 0);
        thief.MarkRuntimeWired(false, static () => 0);
    }

    private bool TryBuildHostContext(
        Farmer player,
        string ownerPlayerKey,
        double distanceTiles,
        string modeId,
        out DarkHandLeaseOwnerContext context,
        out DarkHandRuntimeConfigIdentity configIdentity,
        out string reason
    )
    {
        configIdentity = configIdentityProvider();
        var tierEligible = false;
        long sanityRevision = 0;
        if (
            lifecycle.IsEnabled
            && lifecycle.TryGetTierState(ownerPlayerKey, out var tier)
            && tier is { IsAvailable: true, Revision: > 0 }
        )
        {
            sanityRevision = tier.Revision;
            foreach (var activeTierId in tier.ActiveTierIds)
            {
                if (
                    string.Equals(
                        activeTierId,
                        SanityTierIds.DarkHand,
                        StringComparison.Ordinal
                    )
                )
                {
                    tierEligible = true;
                    break;
                }
            }
        }
        tierEligible &=
            !lifecycle.IsEventCoverageActiveForPlayer(ownerPlayerKey)
            && configIdentity.IsAvailable
            && player.currentLocation is not null
            && !Game1.paused
            && !Game1.eventUp
            && Game1.CurrentEvent is null
            && player.currentLocation.currentEvent is null
            && Game1.activeClickableMenu is null;
        context = new DarkHandLeaseOwnerContext(
            IsHostAuthority:
                Game1.IsMasterGame
                && lifecycle.AuthorityRole == SanityAuthorityRole.Host,
            SenderPlayerKey: ownerPlayerKey,
            OwnerPlayerKey: ownerPlayerKey,
            OwnerLocationId: player.currentLocation?.NameOrUniqueName ?? string.Empty,
            distanceTiles,
            tierEligible,
            sanityRevision,
            modeId
        );
        reason = tierEligible
            ? "dark-hand.host-owner-context-current"
            : "dark-hand.host-owner-sanity-or-config-ineligible";
        return tierEligible;
    }

    private bool TryResolveCurrentMode(out string modeId, out string reason)
    {
        try
        {
            var resolution = modeResolver.Resolve();
            modeId = resolution.Mode switch
            {
                DarkHandProjectionMode.FireThief => DarkHandFireModeIds.FireThief,
                DarkHandProjectionMode.Harassment =>
                    DarkHandHarassmentModeIds.Harassment,
                DarkHandProjectionMode.Thief => DarkHandThiefModeIds.Thief,
                _ => string.Empty,
            };
            reason = resolution.Reason;
            return resolution.IsAvailable
                && DarkHandInteractionLeaseProtocol.IsIdentifier(modeId);
        }
        catch (Exception)
        {
            modeId = string.Empty;
            reason = "dark-hand.mode-resolver-failed";
            return false;
        }
    }

    private static bool OperationMatchesMode(string operationId, string modeId) =>
        (string.Equals(modeId, DarkHandFireModeIds.FireThief, StringComparison.Ordinal)
            && string.Equals(
                operationId,
                DarkHandFireOperationIds.Extinguish,
                StringComparison.Ordinal
            ))
        || (string.Equals(
                modeId,
                DarkHandHarassmentModeIds.Harassment,
                StringComparison.Ordinal
            )
            && string.Equals(
                operationId,
                DarkHandHarassmentOperationIds.Harassment,
                StringComparison.Ordinal
            ))
        || (string.Equals(modeId, DarkHandThiefModeIds.Thief, StringComparison.Ordinal)
            && string.Equals(
                operationId,
                DarkHandThiefOperationIds.Thief,
                StringComparison.Ordinal
            ));

    private string RuleRevision(string operationId)
    {
        return string.Equals(
            operationId,
            DarkHandFireOperationIds.Extinguish,
            StringComparison.Ordinal
        )
            ? string.Concat(
                DarkHandFireTargetCatalog.ContractId,
                ".",
                fire.Catalog.SchemaVersion.ToString(CultureInfo.InvariantCulture)
            )
            : string.Concat(
                MachineTargetCatalog.ContractId,
                ".",
                harassment.Catalog.SchemaVersion.ToString(
                    CultureInfo.InvariantCulture
                )
            );
    }

    private static bool TryValidateLocalOwner(
        HarmlessProjectionOwnerContext owner,
        GameLocation location,
        out Farmer player
    )
    {
        player = Game1.player;
        return player is not null
            && owner.ScreenId == Context.ScreenId
            && ReferenceEquals(player.currentLocation, location)
            && string.Equals(
                owner.PlayerKey,
                SanityPlayerKey.FromUniqueMultiplayerId(
                    player.UniqueMultiplayerID
                ),
                StringComparison.Ordinal
            )
            && string.Equals(
                owner.LocationNameOrUniqueName,
                location.NameOrUniqueName,
                StringComparison.Ordinal
            );
    }

    private bool IsCoolingDown(string ownerPlayerKey, string targetId, long now)
    {
        return cooldownUntil.TryGetValue(
                string.Concat(ownerPlayerKey, "|", targetId),
                out var until
            )
            && now < until;
    }

    private bool CanTrackCooldown(string ownerPlayerKey, string targetId)
    {
        var key = string.Concat(ownerPlayerKey, "|", targetId);
        return cooldownUntil.Count < MaximumCooldowns
            || cooldownUntil.ContainsKey(key);
    }

    private void SetCooldown(string ownerPlayerKey, string targetId, long now)
    {
        var key = string.Concat(ownerPlayerKey, "|", targetId);
        cooldownUntil[key] = unchecked(now + MutationCooldownTicks);
    }

    private void RetireOutstanding(
        DarkHandInteractionLeaseCommitRequest request,
        string ownerPlayerKey
    )
    {
        var lookup = sharedLeaseAuthority.TryResolveForCommit(
            request,
            ownerPlayerKey,
            Game1.ticks
        );
        if (lookup.Resolved && !lookup.IsDuplicate && lookup.Lease is not null)
            sharedLeaseAuthority.RetireResolvedCommit(lookup.Lease, ownerPlayerKey);
    }

    private DarkHandInteractionCommitResult Remember(
        DarkHandInteractionCommitResult result
    )
    {
        if (
            terminalResults.Count < MaximumTerminalResults
            || terminalResults.ContainsKey(result.LeaseId)
        )
        {
            terminalResults[result.LeaseId] = result;
        }
        return result;
    }

    private DarkHandInteractionCommitResult RejectedResult(
        DarkHandInteractionLease? lease,
        string ownerPlayerKey,
        long nonce,
        string reason
    ) =>
        new()
        {
            SessionId = lease?.SessionId ?? lifecycle.SessionId,
            LeaseId = lease?.LeaseId ?? "dark-hand.commit.unknown",
            Nonce = nonce > 0 ? nonce : 1,
            OwnerPlayerKey = SanityPlayerKey.IsCanonical(ownerPlayerKey)
                ? ownerPlayerKey
                : "0",
            TargetId = lease?.TargetId ?? "dark-hand.target.unknown",
            OperationId = lease?.OperationId ?? "dark-hand.operation.unknown",
            Disposition = DarkHandLeaseBoundCommitDisposition.Rejected.ToString(),
            Reason = reason,
            WorldMutationApplied = false,
        };

    private static DarkHandInteractionCommitResult CopyResult(
        DarkHandInteractionCommitResult source,
        string disposition,
        string reason,
        bool mutationApplied
    ) =>
        new()
        {
            ProtocolVersion = source.ProtocolVersion,
            SchemaVersion = source.SchemaVersion,
            SessionId = source.SessionId,
            LeaseId = source.LeaseId,
            Nonce = source.Nonce,
            OwnerPlayerKey = source.OwnerPlayerKey,
            TargetId = source.TargetId,
            OperationId = source.OperationId,
            Disposition = disposition,
            Reason = reason,
            WorldMutationApplied = mutationApplied,
        };

    private static DarkHandLeaseIssueResult IssueRejected(string reason) =>
        new(DarkHandLeaseIssueStatus.Rejected, reason, null);

    private void ClearWindow()
    {
        coordinator.ClearOperationWindows();
        adapters.Clear();
        issuedBindings.Clear();
        terminalResults.Clear();
        localPending.Clear();
        cooldownUntil.Clear();
        loggedReasons.Clear();
        successfulTransactions = 0;
        successfulFireTransactions = 0;
        successfulHarassmentTransactions = 0;
        successfulThiefTransactions = 0;
    }

    private static void RemoveOwnerEntries<TValue>(
        Dictionary<string, TValue> source,
        Func<KeyValuePair<string, TValue>, bool> predicate
    )
    {
        if (source.Count == 0)
            return;

        var remove = new List<string>();
        foreach (var pair in source)
        {
            if (predicate(pair))
                remove.Add(pair.Key);
        }
        foreach (var key in remove)
            source.Remove(key);
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
            ClearWindow();
        }
    }

    private void OnWorldBoundaryStarting(SanityWorldBoundary boundary)
    {
        if (!disposed)
            ClearWindow();
    }

    private void OnSessionClearing(SanitySessionBoundary boundary)
    {
        if (!disposed)
            ClearWindow();
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        Dispose();
    }

    private void LogOnce(string reason, LogLevel level)
    {
        if (
            string.IsNullOrWhiteSpace(reason)
            || loggedReasons.Count >= 64
            || !loggedReasons.Add(reason)
        )
        {
            return;
        }
        monitor.Log($"DarkHand transaction runtime: {reason}", level);
    }
}
