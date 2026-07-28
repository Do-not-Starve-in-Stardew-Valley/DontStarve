#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using DontStarve.Player.Stats.Sanity.Illusions.Projection;
using Microsoft.Xna.Framework;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.WorldInteractions.DarkHand;

internal static class DarkHandStardewVersionGate
{
    internal const string VerifiedFileVersion = "1.6.15.24356";

    internal static bool IsVerified1615 { get; } = Resolve();

    private static bool Resolve()
    {
        try
        {
            return string.Equals(
                FileVersionInfo.GetVersionInfo(typeof(Game1).Assembly.Location).FileVersion,
                VerifiedFileVersion,
                StringComparison.Ordinal
            );
        }
        catch (Exception)
        {
            return false;
        }
    }
}

internal sealed record SmapiDarkHandObservedTarget(
    string TargetId,
    string OperationId,
    long Revision,
    HarmlessProjectionWorldPoint WorldPixel
);

/// <summary>
/// One session-bounded host revision owner for exact fire/machine object references. Scans are
/// cadence-controlled by the projection and capped here; fingerprints only detect drift and are
/// never themselves treated as an authority revision.
/// </summary>
internal sealed class SmapiDarkHandWorldTransactionAdapters
    : IDarkHandFireExtinguishAdapter,
        IDarkHandHarassmentMachineAdapter,
        IDarkHandThiefMachineAdapter
{
    internal const int MaximumTrackedTargets = 256;
    internal const int MaximumObjectsPerScan = 256;
    internal const double MaximumTargetDistanceTiles = 20d;

    private sealed class Entry
    {
        internal string TargetId { get; init; } = string.Empty;
        internal string OperationId { get; init; } = string.Empty;
        internal GameLocation Location { get; init; } = null!;
        internal Vector2 Tile { get; init; }
        internal StardewValley.Object Target { get; init; } = null!;
        internal string QualifiedItemId { get; init; } = string.Empty;
        internal long Revision { get; set; }
        internal string StateFingerprint { get; set; } = string.Empty;
        internal long OwnerPlayerId { get; set; }
    }

    private readonly DarkHandFireTargetCatalog fireCatalog;
    private readonly DarkHandFireCapability fireCapability;
    private readonly DarkHandFireTargetRegistry fireRegistry;
    private readonly MachineTargetCatalog machineCatalog;
    private readonly MachineRuntimeEvidence machineEvidence;
    private readonly MachineSnapshotRegistry machineRegistry;
    private readonly SmapiMachineInteractionAdapter machineReader;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private long nextRevision;

    internal SmapiDarkHandWorldTransactionAdapters(
        DarkHandFireTargetCatalog fireCatalog,
        DarkHandFireCapability fireCapability,
        DarkHandFireTargetRegistry fireRegistry,
        MachineTargetCatalog machineCatalog,
        MachineRuntimeEvidence machineEvidence,
        MachineSnapshotRegistry machineRegistry
    )
    {
        this.fireCatalog = fireCatalog ?? throw new ArgumentNullException(nameof(fireCatalog));
        this.fireCapability = fireCapability;
        this.fireRegistry = fireRegistry ?? throw new ArgumentNullException(nameof(fireRegistry));
        this.machineCatalog = machineCatalog
            ?? throw new ArgumentNullException(nameof(machineCatalog));
        this.machineEvidence = machineEvidence;
        this.machineRegistry = machineRegistry
            ?? throw new ArgumentNullException(nameof(machineRegistry));
        machineReader = new SmapiMachineInteractionAdapter(machineCatalog, machineEvidence);
    }

    internal int TrackedTargetCount => entries.Count;

    internal bool TryFindNearest(
        GameLocation location,
        HarmlessProjectionWorldPoint ownerWorldPixel,
        DarkHandProjectionMode mode,
        out SmapiDarkHandObservedTarget? observed,
        out string reason
    )
    {
        ArgumentNullException.ThrowIfNull(location);
        observed = null;
        if (!ownerWorldPixel.IsFinite || !DarkHandStardewVersionGate.IsVerified1615)
        {
            reason = "dark-hand.runtime-version-or-owner-point-invalid";
            return false;
        }

        var bestDistanceSquared = double.MaxValue;
        var scanned = 0;
        foreach (var pair in location.Objects.Pairs)
        {
            if (++scanned > MaximumObjectsPerScan)
                break;
            if (
                !TryResolveOperation(mode, pair.Value, out var operationId)
                || !IsCatalogCandidate(pair.Value, operationId, location.NameOrUniqueName)
                || !TryRefresh(
                    location,
                    pair.Key,
                    pair.Value,
                    operationId,
                    ownerPlayerId: 0,
                    out var entry,
                    out _
                )
            )
            {
                continue;
            }

            var worldPixel = WorldPoint(pair.Key);
            var distanceSquared = DistanceSquared(ownerWorldPixel, worldPixel);
            if (
                distanceSquared
                    > MaximumTargetDistanceTiles
                        * MaximumTargetDistanceTiles
                        * Game1.tileSize
                        * Game1.tileSize
                || distanceSquared >= bestDistanceSquared
            )
            {
                continue;
            }
            if (!IsEligibleForMode(entry!, mode))
                continue;

            bestDistanceSquared = distanceSquared;
            observed = new SmapiDarkHandObservedTarget(
                entry!.TargetId,
                entry.OperationId,
                entry.Revision,
                worldPixel
            );
        }

        reason = observed is null
            ? "dark-hand.target-no-eligible-object"
            : "dark-hand.target-owner-local-observed";
        return observed is not null;
    }

    internal bool TryPrepareHostTarget(
        Farmer owner,
        string targetId,
        string operationId,
        out long revision,
        out double ownerDistanceTiles,
        out string reason
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        revision = 0;
        ownerDistanceTiles = double.PositiveInfinity;
        var location = owner.currentLocation;
        if (
            location is null
            || !DarkHandInteractionLeaseProtocol.IsIdentifier(targetId)
            || !DarkHandInteractionLeaseProtocol.IsIdentifier(operationId)
            || !DarkHandStardewVersionGate.IsVerified1615
        )
        {
            reason = "dark-hand.host-target-context-invalid";
            return false;
        }

        var scanned = 0;
        foreach (var pair in location.Objects.Pairs)
        {
            if (++scanned > MaximumObjectsPerScan)
                break;
            if (
                !TryResolveOperation(operationId, pair.Value)
                || !IsCatalogCandidate(pair.Value, operationId, location.NameOrUniqueName)
                || !string.Equals(
                    CreateTargetId(
                        location.NameOrUniqueName,
                        pair.Key,
                        pair.Value.QualifiedItemId,
                        operationId
                    ),
                    targetId,
                    StringComparison.Ordinal
                )
                || !TryRefresh(
                    location,
                    pair.Key,
                    pair.Value,
                    operationId,
                    owner.UniqueMultiplayerID,
                    out var entry,
                    out reason
                )
            )
            {
                continue;
            }

            ownerDistanceTiles =
                Math.Sqrt(DistanceSquared(WorldPoint(owner.Position / Game1.tileSize), WorldPoint(pair.Key)))
                / Game1.tileSize;
            if (
                !double.IsFinite(ownerDistanceTiles)
                || ownerDistanceTiles > MaximumTargetDistanceTiles
            )
            {
                reason = "dark-hand.host-target-owner-out-of-range";
                return false;
            }
            revision = entry!.Revision;
            reason = "dark-hand.host-target-prepared";
            return true;
        }

        reason = "dark-hand.host-target-not-found";
        return false;
    }

    internal bool RefreshForCommit(
        string targetId,
        string operationId,
        out long revision,
        out double ownerDistanceTiles,
        out string reason
    )
    {
        revision = 0;
        ownerDistanceTiles = double.PositiveInfinity;
        reason = "dark-hand.commit-target-unavailable";
        if (
            !entries.TryGetValue(targetId, out var entry)
            || !string.Equals(entry.OperationId, operationId, StringComparison.Ordinal)
            || !entry.Location.Objects.TryGetValue(entry.Tile, out var current)
            || !ReferenceEquals(current, entry.Target)
        )
        {
            reason = "dark-hand.commit-target-reference-drifted";
            return false;
        }
        var owner = Game1.GetPlayer(entry.OwnerPlayerId, onlyOnline: true);
        if (
            owner?.currentLocation is null
            || !ReferenceEquals(owner.currentLocation, entry.Location)
            || !TryRefresh(
                entry.Location,
                entry.Tile,
                entry.Target,
                operationId,
                entry.OwnerPlayerId,
                out entry,
                out reason
            )
        )
        {
            return false;
        }

        ownerDistanceTiles =
            Math.Sqrt(DistanceSquared(WorldPoint(owner.Position / Game1.tileSize), WorldPoint(entry!.Tile)))
            / Game1.tileSize;
        revision = entry.Revision;
        reason = "dark-hand.commit-target-refreshed";
        return true;
    }

    public DarkHandFireAdapterResult Commit(DarkHandFireCommitPlan plan)
    {
        if (
            !TryGetExactEntry(
                plan.TargetId,
                DarkHandFireOperationIds.Extinguish,
                plan.LocationId,
                plan.QualifiedItemId,
                plan.TargetRevision,
                out var entry
            )
            || entry!.Target is not Torch torch
            || !string.Equals(
                torch.GetType().FullName,
                DarkHandFireRuntimeTypeNames.Torch,
                StringComparison.Ordinal
            )
            || !torch.isOn.Value
        )
        {
            return FireRejected("dark-hand.fire-adapter-target-drifted");
        }

        var owner = Game1.GetPlayer(entry.OwnerPlayerId, onlyOnline: true);
        if (owner is null || !ReferenceEquals(owner.currentLocation, entry.Location))
            return FireRejected("dark-hand.fire-adapter-owner-invalid");

        try
        {
            var handled = torch.checkForAction(owner, justCheckingForActivity: false);
            var stillPresent =
                entry.Location.Objects.TryGetValue(entry.Tile, out var current)
                && ReferenceEquals(current, torch);
            if (handled && stillPresent && !torch.isOn.Value)
            {
                RefreshAfterMutation(entry);
                return new DarkHandFireAdapterResult(
                    DarkHandFireAdapterStatus.Applied,
                    "dark-hand.fire-adapter-safe-off-applied",
                    TargetStillPresent: true,
                    IsFireOnAfter: false,
                    LightRefreshRequested: true
                );
            }
        }
        catch (Exception)
        {
            // The exact rollback below uses the same object's public toggle path.
        }

        var rolledBack = TryRestoreTorch(entry, torch);
        return new DarkHandFireAdapterResult(
            rolledBack
                ? DarkHandFireAdapterStatus.RolledBack
                : DarkHandFireAdapterStatus.Rejected,
            rolledBack
                ? "dark-hand.fire-adapter-rolled-back"
                : "dark-hand.fire-adapter-rollback-failed",
            TargetStillPresent:
                entry.Location.Objects.TryGetValue(entry.Tile, out var currentAfter)
                && ReferenceEquals(currentAfter, torch),
            IsFireOnAfter: torch.isOn.Value,
            LightRefreshRequested: rolledBack
        );
    }

    public DarkHandHarassmentLandingResult ReserveLanding(
        DarkHandHarassmentLandingRequest request
    ) =>
        new(
            IsReserved: false,
            "dark-hand.harassment-eject-disabled-no-landing-adapter",
            Reservation: null
        );

    public DarkHandHarassmentAdapterResult CommitDelay(
        DarkHandHarassmentDelayCommitPlan plan
    )
    {
        if (!TryGetExactMachine(plan.Before, out var entry, out var before))
            return DelayRejected(plan.Before, "dark-hand.machine-delay-target-drifted");
        var machine = entry!.Target;
        var originalMinutes = machine.MinutesUntilReady;
        try
        {
            machine.MinutesUntilReady = plan.MinutesUntilReadyAfter;
            var after = CaptureMachine(entry, NextRevision());
            if (
                after.MinutesUntilReady == plan.MinutesUntilReadyAfter
                && entry.Location.Objects.TryGetValue(entry.Tile, out var current)
                && ReferenceEquals(current, machine)
            )
            {
                AcceptMachineMutation(entry, after);
                return new DarkHandHarassmentAdapterResult(
                    DarkHandHarassmentAdapterStatus.Applied,
                    "dark-hand.machine-delay-applied",
                    after,
                    WorldMutationApplied: true,
                    RollbackVerified: false,
                    ItemLandedExactlyOnce: false
                );
            }
        }
        catch (Exception)
        {
            // Restore and verify the complete captured state below.
        }

        machine.MinutesUntilReady = originalMinutes;
        var rollback = CaptureMachine(entry, plan.Before.AuthorityRevision);
        var exact = Equals(MachineMutationStateImage.Capture(rollback), plan.Before);
        return new DarkHandHarassmentAdapterResult(
            exact
                ? DarkHandHarassmentAdapterStatus.RolledBack
                : DarkHandHarassmentAdapterStatus.Rejected,
            exact
                ? "dark-hand.machine-delay-rolled-back"
                : "dark-hand.machine-delay-rollback-failed",
            rollback,
            WorldMutationApplied: false,
            RollbackVerified: exact,
            ItemLandedExactlyOnce: false
        );
    }

    public DarkHandHarassmentAdapterResult CommitEject(
        DarkHandHarassmentEjectCommitPlan plan
    ) =>
        new(
            DarkHandHarassmentAdapterStatus.Rejected,
            "dark-hand.harassment-eject-disabled-no-atomic-landing",
            SnapshotAfter: null,
            WorldMutationApplied: false,
            RollbackVerified: false,
            ItemLandedExactlyOnce: false
        );

    public DarkHandThiefAdapterResult CommitDelete(DarkHandThiefCommitPlan plan)
    {
        if (!TryGetExactMachine(plan.Before, out var entry, out _))
            return ThiefRejected(null, "dark-hand.thief-adapter-target-drifted");
        var machine = entry!.Target;
        var held = machine.heldObject.Value;
        var input = machine.lastInputItem.Value;
        var rule = machine.lastOutputRuleId.Value;
        var minutes = machine.MinutesUntilReady;
        var ready = machine.readyForHarvest.Value;
        var next = machine.showNextIndex.Value;
        if (
            !IsExactOrdinaryContent(held, plan.DeletedOutput)
            || !IsExactOrdinaryContent(input, plan.DeletedInput)
            || (held is null && input is null)
        )
        {
            return ThiefRejected(
                CaptureMachine(entry, plan.Before.AuthorityRevision),
                "dark-hand.thief-adapter-content-drifted"
            );
        }

        try
        {
            machine.heldObject.Value = null;
            machine.lastInputItem.Value = null;
            machine.lastOutputRuleId.Value = null;
            machine.MinutesUntilReady = 0;
            machine.readyForHarvest.Value = false;
            machine.showNextIndex.Value = false;
            var after = CaptureMachine(entry, NextRevision());
            if (
                entry.Location.Objects.TryGetValue(entry.Tile, out var current)
                && ReferenceEquals(current, machine)
                && after.HeldOutput is null
                && after.LastInputItem is null
                && after.State == MachineLifecycleState.Empty
            )
            {
                AcceptMachineMutation(entry, after);
                return new DarkHandThiefAdapterResult(
                    DarkHandThiefAdapterStatus.Applied,
                    "dark-hand.thief-machine-content-deleted",
                    after,
                    WorldMutationApplied: true,
                    RollbackVerified: false,
                    ContentPermanentlyDeleted: true,
                    MachineObjectPreserved: true,
                    ExternalStatePreserved: true
                );
            }
        }
        catch (Exception)
        {
            // Restore every touched public field and verify against the frozen image below.
        }

        machine.heldObject.Value = held;
        machine.lastInputItem.Value = input;
        machine.lastOutputRuleId.Value = rule;
        machine.MinutesUntilReady = minutes;
        machine.readyForHarvest.Value = ready;
        machine.showNextIndex.Value = next;
        var rollback = CaptureMachine(entry, plan.Before.AuthorityRevision);
        var exact = Equals(MachineMutationStateImage.Capture(rollback), plan.Before);
        return new DarkHandThiefAdapterResult(
            exact
                ? DarkHandThiefAdapterStatus.RolledBack
                : DarkHandThiefAdapterStatus.Rejected,
            exact
                ? "dark-hand.thief-adapter-rolled-back"
                : "dark-hand.thief-adapter-rollback-failed",
            rollback,
            WorldMutationApplied: false,
            RollbackVerified: exact,
            ContentPermanentlyDeleted: false,
            MachineObjectPreserved:
                entry.Location.Objects.TryGetValue(entry.Tile, out var restored)
                && ReferenceEquals(restored, machine),
            ExternalStatePreserved: true
        );
    }

    internal void Clear()
    {
        entries.Clear();
        nextRevision = 0;
    }

    private bool TryRefresh(
        GameLocation location,
        Vector2 tile,
        StardewValley.Object target,
        string operationId,
        long ownerPlayerId,
        out Entry? entry,
        out string reason
    )
    {
        var targetId = CreateTargetId(
            location.NameOrUniqueName,
            tile,
            target.QualifiedItemId,
            operationId
        );
        entries.TryGetValue(targetId, out entry);
        if (
            entry is not null
            && (
                !ReferenceEquals(entry.Location, location)
                || !ReferenceEquals(entry.Target, target)
                || entry.Tile != tile
                || !string.Equals(
                    entry.QualifiedItemId,
                    target.QualifiedItemId,
                    StringComparison.Ordinal
                )
                || !string.Equals(entry.OperationId, operationId, StringComparison.Ordinal)
            )
        )
        {
            entries.Remove(targetId);
            entry = null;
        }
        if (entry is null)
        {
            if (entries.Count >= MaximumTrackedTargets)
            {
                reason = "dark-hand.runtime-target-cap-reached";
                return false;
            }
            entry = new Entry
            {
                TargetId = targetId,
                OperationId = operationId,
                Location = location,
                Tile = tile,
                Target = target,
                QualifiedItemId = target.QualifiedItemId,
                Revision = NextRevision(),
                OwnerPlayerId = ownerPlayerId,
            };
            entries.Add(targetId, entry);
        }
        else if (ownerPlayerId > 0)
        {
            entry.OwnerPlayerId = ownerPlayerId;
        }

        if (string.Equals(operationId, DarkHandFireOperationIds.Extinguish, StringComparison.Ordinal))
        {
            if (!RefreshFire(entry, out reason))
                return false;
        }
        else if (!RefreshMachine(entry, out reason))
        {
            return false;
        }
        return true;
    }

    private bool RefreshFire(Entry entry, out string reason)
    {
        var definition = fireCatalog.FindByQualifiedItemId(entry.QualifiedItemId);
        if (
            !fireCapability.CanExecute
            || definition is not { Enabled: true, TargetKind: DarkHandFireTargetKind.Campfire }
            || !definition.AllowsLocation(entry.Location.NameOrUniqueName)
            || entry.Target is not Torch torch
            || !string.Equals(
                torch.GetType().FullName,
                definition.RuntimeTypeFullName,
                StringComparison.Ordinal
            )
        )
        {
            reason = "dark-hand.fire-runtime-target-ineligible";
            return false;
        }
        var fingerprint = string.Concat(
            entry.QualifiedItemId,
            "|",
            torch.isOn.Value ? "1" : "0"
        );
        if (
            entry.StateFingerprint.Length > 0
            && !string.Equals(entry.StateFingerprint, fingerprint, StringComparison.Ordinal)
        )
        {
            entry.Revision = NextRevision();
        }
        entry.StateFingerprint = fingerprint;
        var snapshot = new DarkHandFireTargetSnapshot(
            definition.Id,
            entry.TargetId,
            entry.Location.NameOrUniqueName,
            entry.QualifiedItemId,
            definition.RuntimeTypeFullName,
            definition.TargetKind,
            definition.OperationId,
            entry.Revision,
            IsPresent: true,
            torch.isOn.Value,
            HasExplainableLightSource: true,
            AtomicAdapterAvailable: true
        );
        var update = fireRegistry.Upsert(snapshot);
        reason = update.Reason;
        return update.Status
            is DarkHandFireRegistryUpdateStatus.Accepted
                or DarkHandFireRegistryUpdateStatus.IgnoredDuplicate;
    }

    private bool RefreshMachine(Entry entry, out string reason)
    {
        var snapshot = machineReader.Capture(
            entry.Target,
            entry.TargetId,
            entry.Location.NameOrUniqueName,
            entry.Revision
        );
        if (
            entry.StateFingerprint.Length > 0
            && !string.Equals(
                entry.StateFingerprint,
                snapshot.StateFingerprint,
                StringComparison.Ordinal
            )
        )
        {
            entry.Revision = NextRevision();
            snapshot = machineReader.Capture(
                entry.Target,
                entry.TargetId,
                entry.Location.NameOrUniqueName,
                entry.Revision
            );
        }
        entry.StateFingerprint = snapshot.StateFingerprint;
        var update = machineRegistry.Upsert(snapshot);
        reason = update.Reason;
        return update.Status
            is MachineSnapshotRegistryUpdateStatus.Accepted
                or MachineSnapshotRegistryUpdateStatus.IgnoredDuplicate;
    }

    private bool IsEligibleForMode(Entry entry, DarkHandProjectionMode mode)
    {
        if (mode == DarkHandProjectionMode.FireThief)
        {
            return fireRegistry.TryGet(entry.TargetId, out var fire)
                && fire is { IsOn: true };
        }
        if (!machineRegistry.TryGet(entry.TargetId, out var machine) || machine is null)
            return false;
        return mode switch
        {
            DarkHandProjectionMode.Harassment => machine.CanDelay,
            DarkHandProjectionMode.Thief => machine.CanDeleteContent,
            _ => false,
        };
    }

    private bool IsCatalogCandidate(
        StardewValley.Object target,
        string operationId,
        string locationId
    )
    {
        if (
            string.Equals(
                operationId,
                DarkHandFireOperationIds.Extinguish,
                StringComparison.Ordinal
            )
        )
        {
            var definition = fireCatalog.FindByQualifiedItemId(
                target.QualifiedItemId
            );
            return definition is { Enabled: true }
                && definition.AllowsLocation(locationId);
        }

        var machine = machineCatalog.Find(target.QualifiedItemId);
        return machine is { Enabled: true, Excluded: false }
            && machine.AllowsLocation(locationId)
            && (
                string.Equals(
                    operationId,
                    DarkHandHarassmentOperationIds.Harassment,
                    StringComparison.Ordinal
                )
                    ? machine.AllowDelay || machine.AllowEject
                    : machine.AllowThief
            );
    }

    private bool TryGetExactMachine(
        MachineMutationStateImage expected,
        out Entry? entry,
        out MachineInteractionSnapshot? snapshot
    )
    {
        snapshot = null;
        if (
            !entries.TryGetValue(expected.TargetId, out entry)
            || !entry.Location.Objects.TryGetValue(entry.Tile, out var current)
            || !ReferenceEquals(current, entry.Target)
        )
        {
            return false;
        }
        snapshot = CaptureMachine(entry, entry.Revision);
        return Equals(MachineMutationStateImage.Capture(snapshot), expected);
    }

    private MachineInteractionSnapshot CaptureMachine(Entry entry, long revision) =>
        machineReader.Capture(
            entry.Target,
            entry.TargetId,
            entry.Location.NameOrUniqueName,
            revision
        );

    private void AcceptMachineMutation(
        Entry entry,
        MachineInteractionSnapshot snapshot
    )
    {
        entry.Revision = snapshot.AuthorityRevision;
        entry.StateFingerprint = snapshot.StateFingerprint;
    }

    private void RefreshAfterMutation(Entry entry)
    {
        entry.Revision = NextRevision();
        entry.StateFingerprint = string.Concat(entry.QualifiedItemId, "|0");
        RefreshFire(entry, out _);
    }

    private bool TryGetExactEntry(
        string targetId,
        string operationId,
        string locationId,
        string qualifiedItemId,
        long revision,
        out Entry? entry
    )
    {
        return entries.TryGetValue(targetId, out entry)
            && string.Equals(entry.OperationId, operationId, StringComparison.Ordinal)
            && string.Equals(
                entry.Location.NameOrUniqueName,
                locationId,
                StringComparison.Ordinal
            )
            && string.Equals(
                entry.QualifiedItemId,
                qualifiedItemId,
                StringComparison.Ordinal
            )
            && entry.Revision == revision
            && entry.Location.Objects.TryGetValue(entry.Tile, out var current)
            && ReferenceEquals(current, entry.Target);
    }

    private static bool TryRestoreTorch(Entry entry, Torch torch)
    {
        if (
            !entry.Location.Objects.TryGetValue(entry.Tile, out var current)
            || !ReferenceEquals(current, torch)
        )
        {
            return false;
        }
        if (torch.isOn.Value)
            return true;
        var owner = Game1.GetPlayer(entry.OwnerPlayerId, onlyOnline: true);
        try
        {
            return owner is not null
                && torch.checkForAction(owner, justCheckingForActivity: false)
                && torch.isOn.Value;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsExactOrdinaryContent(
        Item? item,
        MachineItemFacts? facts
    )
    {
        if (item is null || facts is null)
            return item is null && facts is null;
        return item is StardewValley.Object objectItem
            && !objectItem.questItem.Value
            && !objectItem.IsRecipe
            && objectItem.canBeTrashed()
            && objectItem.QualifiedItemId.StartsWith("(O)", StringComparison.Ordinal)
            && string.Equals(
                objectItem.QualifiedItemId,
                facts.QualifiedItemId,
                StringComparison.Ordinal
            )
            && objectItem.Stack == facts.Stack
            && objectItem.Quality == facts.Quality
            && facts.Protection == MachineContentProtection.Ordinary;
    }

    private static bool TryResolveOperation(
        DarkHandProjectionMode mode,
        StardewValley.Object target,
        out string operationId
    )
    {
        operationId = mode switch
        {
            DarkHandProjectionMode.FireThief => DarkHandFireOperationIds.Extinguish,
            DarkHandProjectionMode.Harassment =>
                DarkHandHarassmentOperationIds.Harassment,
            DarkHandProjectionMode.Thief => DarkHandThiefOperationIds.Thief,
            _ => string.Empty,
        };
        return TryResolveOperation(operationId, target);
    }

    private static bool TryResolveOperation(
        string operationId,
        StardewValley.Object target
    )
    {
        if (
            string.Equals(
                operationId,
                DarkHandFireOperationIds.Extinguish,
                StringComparison.Ordinal
            )
        )
        {
            return target is Torch;
        }
        return string.Equals(
                operationId,
                DarkHandHarassmentOperationIds.Harassment,
                StringComparison.Ordinal
            )
            || string.Equals(
                operationId,
                DarkHandThiefOperationIds.Thief,
                StringComparison.Ordinal
            );
    }

    private long NextRevision()
    {
        if (nextRevision == long.MaxValue)
            throw new InvalidOperationException("dark-hand.runtime-revision-exhausted");
        return ++nextRevision;
    }

    private static string CreateTargetId(
        string locationId,
        Vector2 tile,
        string qualifiedItemId,
        string operationId
    )
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        Add(locationId);
        Add("|");
        Add(((int)tile.X).ToString(CultureInfo.InvariantCulture));
        Add(",");
        Add(((int)tile.Y).ToString(CultureInfo.InvariantCulture));
        Add("|");
        Add(qualifiedItemId);
        Add("|");
        Add(operationId);
        return string.Concat(
            "dark-hand.target.",
            hash.ToString("X16", CultureInfo.InvariantCulture)
        );

        void Add(string value)
        {
            foreach (var character in value)
            {
                hash ^= character;
                hash *= prime;
            }
        }
    }

    private static HarmlessProjectionWorldPoint WorldPoint(Vector2 tile) =>
        new(
            (tile.X + 0.5d) * Game1.tileSize,
            (tile.Y + 0.5d) * Game1.tileSize
        );

    private static double DistanceSquared(
        HarmlessProjectionWorldPoint left,
        HarmlessProjectionWorldPoint right
    )
    {
        var x = left.X - right.X;
        var y = left.Y - right.Y;
        return (x * x) + (y * y);
    }

    private static DarkHandFireAdapterResult FireRejected(string reason) =>
        new(
            DarkHandFireAdapterStatus.Rejected,
            reason,
            TargetStillPresent: false,
            IsFireOnAfter: true,
            LightRefreshRequested: false
        );

    private static DarkHandHarassmentAdapterResult DelayRejected(
        MachineMutationStateImage before,
        string reason
    ) =>
        new(
            DarkHandHarassmentAdapterStatus.Rejected,
            reason,
            SnapshotAfter: null,
            WorldMutationApplied: false,
            RollbackVerified: false,
            ItemLandedExactlyOnce: false
        );

    private static DarkHandThiefAdapterResult ThiefRejected(
        MachineInteractionSnapshot? snapshot,
        string reason
    ) =>
        new(
            DarkHandThiefAdapterStatus.Rejected,
            reason,
            snapshot,
            WorldMutationApplied: false,
            RollbackVerified: snapshot is not null,
            ContentPermanentlyDeleted: false,
            MachineObjectPreserved: true,
            ExternalStatePreserved: true
        );
}

internal sealed class SmapiDarkHandHarassmentRandomSource
    : IDarkHandHarassmentRandomSource
{
    public double NextUnitInterval() => Game1.random.NextDouble();
}
