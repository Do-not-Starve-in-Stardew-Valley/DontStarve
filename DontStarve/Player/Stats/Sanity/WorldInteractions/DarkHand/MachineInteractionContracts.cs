#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DontStarve.Player.Stats.Sanity.WorldInteractions.DarkHand;

internal static class MachineInteractionReasonIds
{
    internal const string CatalogLoaded = "dark-hand.machine-targets.loaded";
    internal const string CatalogJsonEmpty = "dark-hand.machine-targets.json-empty";
    internal const string CatalogJsonMalformed = "dark-hand.machine-targets.json-malformed";
    internal const string CatalogRootInvalid = "dark-hand.machine-targets.root-invalid";
    internal const string CatalogVersionUnsupported =
        "dark-hand.machine-targets.version-unsupported";
    internal const string TargetArrayInvalid = "dark-hand.machine-targets.array-invalid";
    internal const string TargetInvalid = "dark-hand.machine-target.invalid";
    internal const string TargetIdDuplicated = "dark-hand.machine-target.id-duplicated";
    internal const string TargetQualifiedIdDuplicated =
        "dark-hand.machine-target.qualified-id-duplicated";
    internal const string CatalogUnavailable =
        "dark-hand.machine.capability.catalog-unavailable";
    internal const string NoEnabledTargets =
        "dark-hand.machine.capability.no-enabled-targets";
    internal const string ObservationInvalid = "dark-hand.machine.observation-invalid";
    internal const string DataUnavailable = "dark-hand.machine.data-unavailable";
    internal const string TargetNotAllowlisted = "dark-hand.machine.target-not-allowlisted";
    internal const string TargetDisabled = "dark-hand.machine.target-disabled";
    internal const string TargetExcluded = "dark-hand.machine.target-excluded";
    internal const string LocationNotAllowlisted =
        "dark-hand.machine.location-not-allowlisted";
    internal const string ActiveRuleUnavailable =
        "dark-hand.machine.active-rule-unavailable";
    internal const string ReadyOutputMissing = "dark-hand.machine.ready-output-missing";
    internal const string SpecializedStateUnsupported =
        "dark-hand.machine.specialized-state-unsupported";
    internal const string ClassificationMismatch =
        "dark-hand.machine.classification-mismatch";
    internal const string Empty = "dark-hand.machine.state-empty";
    internal const string Ready = "dark-hand.machine.state-ready";
    internal const string Running = "dark-hand.machine.state-running";
    internal const string AuthorityRevisionUnavailable =
        "dark-hand.machine.authority-revision-unavailable";
    internal const string SafeLandingUnavailable =
        "dark-hand.machine.safe-landing-unavailable";
    internal const string LastInputIncomplete =
        "dark-hand.machine.last-input-incomplete";
    internal const string Eligible = "dark-hand.machine.eligible";
    internal const string RegistrySnapshotAccepted =
        "dark-hand.machine.registry.snapshot-accepted";
    internal const string RegistrySnapshotDuplicate =
        "dark-hand.machine.registry.snapshot-duplicate";
    internal const string RegistrySnapshotInvalid =
        "dark-hand.machine.registry.snapshot-invalid";
    internal const string RegistrySnapshotConflict =
        "dark-hand.machine.registry.snapshot-conflict";
    internal const string RegistrySnapshotStale =
        "dark-hand.machine.registry.snapshot-stale";
    internal const string RegistryFull = "dark-hand.machine.registry.full";
    internal const string RegistryTargetMissing =
        "dark-hand.machine.registry.target-missing";
    internal const string SnapshotDrifted = "dark-hand.machine.snapshot-drifted";
    internal const string SnapshotCurrent = "dark-hand.machine.snapshot-current";
    internal const string SnapshotQuarantined =
        "dark-hand.machine.snapshot-quarantined";
}

internal enum MachineInteractionCategory
{
    OrdinarySingleInputFinite,
    PermanentInput,
    NoInputInfiniteOutput,
    MultiInput,
    Unsupported,
}

internal enum MachineLifecycleState
{
    Empty,
    Running,
    Ready,
}

internal enum MachineScheduleKind
{
    None,
    FiniteMinutes,
    DayBased,
    OvernightOnly,
    Unknown,
}

[Flags]
internal enum MachineTriggerKinds
{
    None = 0,
    ItemPlacedInMachine = 1,
    OutputCollected = 2,
    MachinePutDown = 4,
    DayUpdate = 8,
}

internal sealed record MachineItemFacts(
    string QualifiedItemId,
    int Stack,
    int Quality,
    bool IsRecipe,
    MachineContentProtection Protection = MachineContentProtection.Unknown
);

internal enum MachineContentProtection
{
    Unknown,
    Ordinary,
    ProtectedOrQuest,
}

internal sealed record MachineRuleFacts(
    string Id,
    MachineTriggerKinds Triggers,
    int PrimaryInputRequiredCount,
    int MinutesUntilReady,
    int DaysUntilReady,
    bool RecalculateOnCollect,
    bool HasCustomOutputMethod
);

internal sealed record MachineDataFacts(
    bool IsIncubator,
    bool OnlyCompleteOvernight,
    bool HasAdditionalConsumedItems,
    bool HasCustomInteractMethod,
    bool HasClearContentsOvernightCondition,
    MachineRuleFacts? ActiveRule
);

/// <summary>
/// Immutable public-surface observation. AuthorityRevision must come from an external host
/// authority; StateFingerprint is only a drift detector and must never be promoted to a revision.
/// </summary>
internal sealed record MachineReadObservation(
    string TargetId,
    string LocationId,
    string QualifiedItemId,
    long AuthorityRevision,
    string LastOutputRuleId,
    int MinutesUntilReady,
    bool ReadyForHarvest,
    bool ShowNextIndex,
    MachineItemFacts? HeldOutput,
    MachineItemFacts? LastInputItem,
    MachineDataFacts? Data
);

internal sealed class MachineTargetDefinition
{
    internal MachineTargetDefinition(
        string id,
        string qualifiedItemId,
        MachineInteractionCategory expectedCategory,
        bool enabled,
        bool excluded,
        bool allowDelay,
        bool allowEject,
        bool allowThief,
        IReadOnlyList<string> locationAllowlist,
        IReadOnlyList<string> evidence,
        string reason
    )
    {
        Id = id;
        QualifiedItemId = qualifiedItemId;
        ExpectedCategory = expectedCategory;
        Enabled = enabled;
        Excluded = excluded;
        AllowDelay = allowDelay;
        AllowEject = allowEject;
        AllowThief = allowThief;
        LocationAllowlist = locationAllowlist;
        Evidence = evidence;
        Reason = reason;
    }

    internal string Id { get; }
    internal string QualifiedItemId { get; }
    internal MachineInteractionCategory ExpectedCategory { get; }
    internal bool Enabled { get; }
    internal bool Excluded { get; }
    internal bool AllowDelay { get; }
    internal bool AllowEject { get; }
    internal bool AllowThief { get; }
    internal IReadOnlyList<string> LocationAllowlist { get; }
    internal IReadOnlyList<string> Evidence { get; }
    internal string Reason { get; }

    internal bool AllowsLocation(string locationId)
    {
        foreach (var allowed in LocationAllowlist)
        {
            if (string.Equals(allowed, locationId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}

internal readonly record struct MachineTargetCatalogLoadResult(
    bool IsAvailable,
    string Reason,
    MachineTargetCatalog Catalog
);

/// <summary>
/// Versioned exact-ID catalog. Unknown/modded IDs are never inferred from display names, tags,
/// runtime types, or reflection. Disabled rows retain evidence without granting mutation rights.
/// </summary>
internal sealed class MachineTargetCatalog
{
    internal const int CurrentSchemaVersion = 2;
    internal const int MaximumTargets = 64;
    internal const int MaximumAllowlistEntries = 32;
    internal const int MaximumEvidenceEntries = 8;
    internal const string ContractId = "sanity.dark-hand-machines.v2";
    internal const string RelativePath = "Asset/Sanity/Data/dark-hand-machines.json";
    internal const string IronAnvilQualifiedItemId = "(BC)Anvil";

    private readonly IReadOnlyList<MachineTargetDefinition> targets;
    private readonly IReadOnlyDictionary<string, MachineTargetDefinition> byQualifiedId;

    private MachineTargetCatalog(
        bool isAvailable,
        int schemaVersion,
        string reason,
        IReadOnlyList<MachineTargetDefinition> targets,
        IReadOnlyDictionary<string, MachineTargetDefinition> byQualifiedId
    )
    {
        IsAvailable = isAvailable;
        SchemaVersion = schemaVersion;
        Reason = reason;
        this.targets = targets;
        this.byQualifiedId = byQualifiedId;
    }

    internal bool IsAvailable { get; }
    internal int SchemaVersion { get; }
    internal string Reason { get; }
    internal IReadOnlyList<MachineTargetDefinition> Targets => targets;

    internal int EnabledTargetCount
    {
        get
        {
            var count = 0;
            foreach (var target in targets)
            {
                if (target.Enabled)
                    count++;
            }
            return count;
        }
    }

    internal MachineTargetDefinition? Find(string qualifiedItemId)
    {
        return byQualifiedId.TryGetValue(qualifiedItemId, out var target) ? target : null;
    }

    internal static MachineTargetCatalogLoadResult Load(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Failed(MachineInteractionReasonIds.CatalogJsonEmpty);

        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                }
            );
            var root = document.RootElement;
            if (
                root.ValueKind != JsonValueKind.Object
                || !HasOnlyProperties(root, "SchemaVersion", "ContractId", "Targets")
            )
            {
                return Failed(MachineInteractionReasonIds.CatalogRootInvalid);
            }
            if (
                !root.TryGetProperty("SchemaVersion", out var versionElement)
                || !versionElement.TryGetInt32(out var version)
                || version != CurrentSchemaVersion
            )
            {
                return Failed(MachineInteractionReasonIds.CatalogVersionUnsupported);
            }
            if (
                !TryRequiredString(root, "ContractId", 128, out var contractId)
                || !string.Equals(contractId, ContractId, StringComparison.Ordinal)
            )
            {
                return Failed(MachineInteractionReasonIds.CatalogRootInvalid);
            }
            if (
                !root.TryGetProperty("Targets", out var targetArray)
                || targetArray.ValueKind != JsonValueKind.Array
                || targetArray.GetArrayLength() > MaximumTargets
            )
            {
                return Failed(MachineInteractionReasonIds.TargetArrayInvalid);
            }

            var targets = new List<MachineTargetDefinition>();
            var byId = new HashSet<string>(StringComparer.Ordinal);
            var byQualifiedId = new Dictionary<string, MachineTargetDefinition>(
                StringComparer.Ordinal
            );
            foreach (var element in targetArray.EnumerateArray())
            {
                if (!TryParseTarget(element, out var target))
                    return Failed(MachineInteractionReasonIds.TargetInvalid);
                if (!byId.Add(target!.Id))
                    return Failed(MachineInteractionReasonIds.TargetIdDuplicated);
                if (!byQualifiedId.TryAdd(target.QualifiedItemId, target))
                    return Failed(MachineInteractionReasonIds.TargetQualifiedIdDuplicated);
                targets.Add(target);
            }

            var catalog = new MachineTargetCatalog(
                true,
                version,
                MachineInteractionReasonIds.CatalogLoaded,
                targets.AsReadOnly(),
                new ReadOnlyDictionary<string, MachineTargetDefinition>(byQualifiedId)
            );
            return new MachineTargetCatalogLoadResult(true, catalog.Reason, catalog);
        }
        catch (JsonException)
        {
            return Failed(MachineInteractionReasonIds.CatalogJsonMalformed);
        }
    }

    internal static MachineTargetCatalog Unavailable(string reason)
    {
        return new MachineTargetCatalog(
            false,
            0,
            reason,
            Array.Empty<MachineTargetDefinition>(),
            new ReadOnlyDictionary<string, MachineTargetDefinition>(
                new Dictionary<string, MachineTargetDefinition>(StringComparer.Ordinal)
            )
        );
    }

    private static bool TryParseTarget(
        JsonElement element,
        out MachineTargetDefinition? target
    )
    {
        target = null;
        if (
            element.ValueKind != JsonValueKind.Object
            || !HasOnlyProperties(
                element,
                "Id",
                "QualifiedItemId",
                "ExpectedCategory",
                "Enabled",
                "Excluded",
                "AllowDelay",
                "AllowEject",
                "AllowThief",
                "LocationAllowlist",
                "Evidence",
                "Reason"
            )
            || !TryRequiredString(element, "Id", 128, out var id)
            || !TryRequiredString(element, "QualifiedItemId", 128, out var qualifiedItemId)
            || !IsExactBigCraftableId(qualifiedItemId)
            || !TryRequiredString(element, "ExpectedCategory", 64, out var categoryText)
            || !Enum.TryParse<MachineInteractionCategory>(categoryText, false, out var category)
            || !TryBoolean(element, "Enabled", out var enabled)
            || !TryBoolean(element, "Excluded", out var excluded)
            || !TryBoolean(element, "AllowDelay", out var allowDelay)
            || !TryBoolean(element, "AllowEject", out var allowEject)
            || !TryBoolean(element, "AllowThief", out var allowThief)
            || !TryStringArray(
                element,
                "LocationAllowlist",
                MaximumAllowlistEntries,
                128,
                out var locations
            )
            || !TryStringArray(
                element,
                "Evidence",
                MaximumEvidenceEntries,
                256,
                out var evidence
            )
            || !TryRequiredString(element, "Reason", 256, out var reason)
        )
        {
            return false;
        }

        if (
            (enabled && (excluded || locations.Count == 0))
            || ((!enabled || excluded) && (allowDelay || allowEject || allowThief))
            || (excluded && category != MachineInteractionCategory.Unsupported)
            || (allowThief && category is MachineInteractionCategory.MultiInput
                or MachineInteractionCategory.Unsupported)
            || string.Equals(qualifiedItemId, IronAnvilQualifiedItemId, StringComparison.Ordinal)
                && !excluded
        )
        {
            return false;
        }

        target = new MachineTargetDefinition(
            id,
            qualifiedItemId,
            category,
            enabled,
            excluded,
            allowDelay,
            allowEject,
            allowThief,
            locations,
            evidence,
            reason
        );
        return true;
    }

    private static bool TryRequiredString(
        JsonElement element,
        string name,
        int maximumLength,
        out string value
    )
    {
        value = string.Empty;
        if (
            !element.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String
        )
        {
            return false;
        }
        value = property.GetString() ?? string.Empty;
        return IsSafeString(value) && value.Length <= maximumLength && value.IndexOf('*') < 0;
    }

    private static bool TryBoolean(JsonElement element, string name, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(name, out var property))
            return false;
        if (property.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }
        return property.ValueKind == JsonValueKind.False;
    }

    private static bool TryStringArray(
        JsonElement element,
        string name,
        int maximumCount,
        int maximumLength,
        out IReadOnlyList<string> values
    )
    {
        values = Array.Empty<string>();
        if (
            !element.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Array
            || property.GetArrayLength() > maximumCount
        )
        {
            return false;
        }
        var parsed = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return false;
            var value = item.GetString() ?? string.Empty;
            if (
                value.Length > maximumLength
                || !IsSafeString(value)
                || value.IndexOf('*') >= 0
                || !unique.Add(value)
            )
            {
                return false;
            }
            parsed.Add(value);
        }
        values = parsed.AsReadOnly();
        return true;
    }

    private static bool HasOnlyProperties(JsonElement element, params string[] allowed)
    {
        var remaining = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!remaining.Remove(property.Name))
                return false;
        }
        return remaining.Count == 0;
    }

    private static bool IsSafeString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        foreach (var character in value)
        {
            if (char.IsControl(character))
                return false;
        }
        return true;
    }

    private static bool IsExactBigCraftableId(string value)
    {
        return value.StartsWith("(BC)", StringComparison.Ordinal)
            && value.Length > 4
            && value.IndexOf('*') < 0;
    }

    private static MachineTargetCatalogLoadResult Failed(string reason)
    {
        return new MachineTargetCatalogLoadResult(false, reason, Unavailable(reason));
    }
}

internal readonly record struct MachineRuntimeEvidence(
    bool StableAuthorityRevisionAvailable,
    bool SafeItemLandingAdapterAvailable,
    bool AtomicContentDeletionAdapterAvailable = false
)
{
    internal static MachineRuntimeEvidence Current =>
        new(
            StableAuthorityRevisionAvailable: false,
            SafeItemLandingAdapterAvailable: false,
            AtomicContentDeletionAdapterAvailable: false
        );

    internal static MachineRuntimeEvidence VerifiedStardew1615 =>
        new(
            StableAuthorityRevisionAvailable: true,
            SafeItemLandingAdapterAvailable: false,
            AtomicContentDeletionAdapterAvailable: true
        );
}

internal enum MachineInteractionCapabilityStatus
{
    Available,
    UnavailableCatalog,
    ReadOnlyNoEnabledTargets,
    ReadOnlyAuthorityRevisionUnavailable,
    ReadOnlySafeLandingUnavailable,
}

internal readonly record struct MachineInteractionCapability(
    MachineInteractionCapabilityStatus Status,
    string Reason,
    int CandidateTargetCount,
    int EnabledTargetCount,
    bool CanCaptureSnapshot,
    bool CanCommitDelay,
    bool CanCommitEject
);

internal static class MachineInteractionCapabilityGate
{
    internal static MachineInteractionCapability Evaluate(
        MachineTargetCatalog catalog,
        MachineRuntimeEvidence evidence
    )
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!catalog.IsAvailable)
        {
            return Result(
                MachineInteractionCapabilityStatus.UnavailableCatalog,
                MachineInteractionReasonIds.CatalogUnavailable,
                canCaptureSnapshot: false,
                canCommitDelay: false,
                canCommitEject: false
            );
        }
        if (catalog.EnabledTargetCount == 0)
        {
            return Result(
                MachineInteractionCapabilityStatus.ReadOnlyNoEnabledTargets,
                MachineInteractionReasonIds.NoEnabledTargets,
                canCaptureSnapshot: true,
                canCommitDelay: false,
                canCommitEject: false
            );
        }
        if (!evidence.StableAuthorityRevisionAvailable)
        {
            return Result(
                MachineInteractionCapabilityStatus.ReadOnlyAuthorityRevisionUnavailable,
                MachineInteractionReasonIds.AuthorityRevisionUnavailable,
                canCaptureSnapshot: true,
                canCommitDelay: false,
                canCommitEject: false
            );
        }

        var canDelay = false;
        var canEject = false;
        foreach (var target in catalog.Targets)
        {
            if (!target.Enabled)
                continue;
            canDelay |= target.AllowDelay;
            canEject |= target.AllowEject;
        }
        if (canEject && !evidence.SafeItemLandingAdapterAvailable)
        {
            return Result(
                MachineInteractionCapabilityStatus.ReadOnlySafeLandingUnavailable,
                MachineInteractionReasonIds.SafeLandingUnavailable,
                canCaptureSnapshot: true,
                canCommitDelay: canDelay,
                canCommitEject: false
            );
        }
        return Result(
            MachineInteractionCapabilityStatus.Available,
            MachineInteractionReasonIds.Eligible,
            canCaptureSnapshot: true,
            canCommitDelay: canDelay,
            canCommitEject: canEject
        );

        MachineInteractionCapability Result(
            MachineInteractionCapabilityStatus status,
            string reason,
            bool canCaptureSnapshot,
            bool canCommitDelay,
            bool canCommitEject
        ) =>
            new(
                status,
                reason,
                catalog.Targets.Count,
                catalog.EnabledTargetCount,
                canCaptureSnapshot,
                canCommitDelay,
                canCommitEject
            );
    }
}

internal sealed record MachineInteractionSnapshot(
    int SchemaVersion,
    string TargetId,
    string LocationId,
    string QualifiedItemId,
    long AuthorityRevision,
    string StateFingerprint,
    MachineLifecycleState State,
    MachineInteractionCategory Category,
    MachineScheduleKind Schedule,
    string ActiveRuleId,
    int MinutesUntilReady,
    bool ReadyForHarvest,
    bool ShowNextIndex,
    MachineItemFacts? HeldOutput,
    MachineItemFacts? LastInputItem,
    bool LastInputIsCompleteRecoveryImage,
    bool CanDelay,
    bool CanEject,
    bool CanLandHeldOutput,
    string Reason,
    bool CanDeleteContent = false
)
{
    internal const int CurrentSchemaVersion = 1;
}

internal static class MachineInteractionClassifier
{
    internal static MachineInteractionSnapshot Capture(
        MachineReadObservation observation,
        MachineTargetCatalog catalog,
        MachineRuntimeEvidence evidence
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(catalog);
        var fingerprint = MachineSnapshotFingerprint.Compute(observation);
        var definition = catalog.Find(observation.QualifiedItemId);
        var state = ResolveState(observation);
        var category = definition is null
            ? MachineInteractionCategory.Unsupported
            : ResolveCategory(observation, state);
        var schedule = ResolveSchedule(observation.Data);
        var reason = ResolveReason(observation, definition, state, category, evidence);
        var catalogEligible =
            definition is not null
            && definition.Enabled
            && !definition.Excluded
            && definition.AllowsLocation(observation.LocationId)
            && definition.ExpectedCategory == category;
        var revisionEligible =
            evidence.StableAuthorityRevisionAvailable && observation.AuthorityRevision > 0;
        var canDelay =
            catalogEligible
            && revisionEligible
            && definition!.AllowDelay
            && state == MachineLifecycleState.Running
            && schedule == MachineScheduleKind.FiniteMinutes
            && !HasSpecializedState(observation.Data);
        var canLandHeldOutput =
            catalogEligible
            && revisionEligible
            && evidence.SafeItemLandingAdapterAvailable
            && observation.HeldOutput is not null;
        var canEject =
            catalogEligible
            && revisionEligible
            && definition!.AllowEject
            && canLandHeldOutput
            && state is MachineLifecycleState.Running or MachineLifecycleState.Ready
            && !HasSpecializedState(observation.Data);
        var canDeleteContent =
            catalogEligible
            && revisionEligible
            && definition!.AllowThief
            && evidence.AtomicContentDeletionAdapterAvailable
            && state is MachineLifecycleState.Running or MachineLifecycleState.Ready
            && category is MachineInteractionCategory.OrdinarySingleInputFinite
                or MachineInteractionCategory.PermanentInput
                or MachineInteractionCategory.NoInputInfiniteOutput
            && !HasSpecializedState(observation.Data)
            && HasOnlyOrdinaryDeletableContent(observation);

        return new MachineInteractionSnapshot(
            MachineInteractionSnapshot.CurrentSchemaVersion,
            observation.TargetId,
            observation.LocationId,
            observation.QualifiedItemId,
            observation.AuthorityRevision,
            fingerprint,
            state,
            category,
            schedule,
            observation.Data?.ActiveRule?.Id ?? string.Empty,
            observation.MinutesUntilReady,
            observation.ReadyForHarvest,
            observation.ShowNextIndex,
            observation.HeldOutput,
            observation.LastInputItem,
            LastInputIsCompleteRecoveryImage: false,
            canDelay,
            canEject,
            canLandHeldOutput,
            reason,
            canDeleteContent
        );
    }

    private static MachineLifecycleState ResolveState(MachineReadObservation observation)
    {
        if (observation.ReadyForHarvest)
            return MachineLifecycleState.Ready;
        if (
            observation.HeldOutput is not null
            || observation.MinutesUntilReady > 0
            || !string.IsNullOrWhiteSpace(observation.LastOutputRuleId)
        )
        {
            return MachineLifecycleState.Running;
        }
        return MachineLifecycleState.Empty;
    }

    private static MachineInteractionCategory ResolveCategory(
        MachineReadObservation observation,
        MachineLifecycleState state
    )
    {
        var data = observation.Data;
        var rule = data?.ActiveRule;
        if (data is null || rule is null || state == MachineLifecycleState.Empty)
            return MachineInteractionCategory.Unsupported;
        if (
            string.Equals(
                observation.QualifiedItemId,
                MachineTargetCatalog.IronAnvilQualifiedItemId,
                StringComparison.Ordinal
            )
            || data.IsIncubator
            || data.HasCustomInteractMethod
        )
            return MachineInteractionCategory.Unsupported;

        var hasPlacedInput =
            (rule.Triggers & MachineTriggerKinds.ItemPlacedInMachine) != 0;
        var repeatsAfterCollection =
            (rule.Triggers & MachineTriggerKinds.OutputCollected) != 0;
        var hasAmbientTrigger =
            (rule.Triggers & (MachineTriggerKinds.MachinePutDown | MachineTriggerKinds.DayUpdate))
            != 0;
        if (hasPlacedInput && repeatsAfterCollection)
            return MachineInteractionCategory.PermanentInput;
        if (!hasPlacedInput && (repeatsAfterCollection || hasAmbientTrigger))
            return MachineInteractionCategory.NoInputInfiniteOutput;
        if (
            data.HasAdditionalConsumedItems
            || rule.PrimaryInputRequiredCount != 1
        )
        {
            return MachineInteractionCategory.MultiInput;
        }
        if (
            hasPlacedInput
            && rule.MinutesUntilReady > 0
            && rule.DaysUntilReady < 0
        )
        {
            return MachineInteractionCategory.OrdinarySingleInputFinite;
        }
        return MachineInteractionCategory.Unsupported;
    }

    private static MachineScheduleKind ResolveSchedule(MachineDataFacts? data)
    {
        if (data is null || data.ActiveRule is null)
            return MachineScheduleKind.None;
        if (data.OnlyCompleteOvernight)
            return MachineScheduleKind.OvernightOnly;
        if (data.ActiveRule.DaysUntilReady >= 0)
            return MachineScheduleKind.DayBased;
        if (data.ActiveRule.MinutesUntilReady > 0)
            return MachineScheduleKind.FiniteMinutes;
        return MachineScheduleKind.Unknown;
    }

    private static bool HasSpecializedState(MachineDataFacts? data)
    {
        return data is not null
            && (
                data.IsIncubator
                || data.HasCustomInteractMethod
                || data.HasClearContentsOvernightCondition
                || data.ActiveRule?.HasCustomOutputMethod == true
                || data.ActiveRule?.RecalculateOnCollect == true
            );
    }

    private static bool HasOnlyOrdinaryDeletableContent(MachineReadObservation observation)
    {
        var found = false;
        foreach (var item in new[] { observation.HeldOutput, observation.LastInputItem })
        {
            if (item is null)
                continue;
            found = true;
            if (item.IsRecipe || item.Protection != MachineContentProtection.Ordinary)
                return false;
        }
        return found;
    }

    private static string ResolveReason(
        MachineReadObservation observation,
        MachineTargetDefinition? definition,
        MachineLifecycleState state,
        MachineInteractionCategory category,
        MachineRuntimeEvidence evidence
    )
    {
        if (!IsIdentifier(observation.TargetId) || !IsIdentifier(observation.LocationId))
            return MachineInteractionReasonIds.ObservationInvalid;
        if (observation.Data is null)
            return MachineInteractionReasonIds.DataUnavailable;
        if (definition is null)
            return MachineInteractionReasonIds.TargetNotAllowlisted;
        if (definition.Excluded)
            return MachineInteractionReasonIds.TargetExcluded;
        if (!definition.Enabled)
            return MachineInteractionReasonIds.TargetDisabled;
        if (!definition.AllowsLocation(observation.LocationId))
            return MachineInteractionReasonIds.LocationNotAllowlisted;
        if (state == MachineLifecycleState.Empty)
            return MachineInteractionReasonIds.Empty;
        if (observation.Data.ActiveRule is null)
            return MachineInteractionReasonIds.ActiveRuleUnavailable;
        if (state == MachineLifecycleState.Ready && observation.HeldOutput is null)
            return MachineInteractionReasonIds.ReadyOutputMissing;
        if (HasSpecializedState(observation.Data))
            return MachineInteractionReasonIds.SpecializedStateUnsupported;
        if (definition.ExpectedCategory != category)
            return MachineInteractionReasonIds.ClassificationMismatch;
        if (!evidence.StableAuthorityRevisionAvailable || observation.AuthorityRevision <= 0)
            return MachineInteractionReasonIds.AuthorityRevisionUnavailable;
        if (state == MachineLifecycleState.Ready && !evidence.SafeItemLandingAdapterAvailable)
            return MachineInteractionReasonIds.SafeLandingUnavailable;
        return state == MachineLifecycleState.Ready
            ? MachineInteractionReasonIds.Ready
            : MachineInteractionReasonIds.Eligible;
    }

    private static bool IsIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            return false;
        foreach (var character in value)
        {
            if (char.IsControl(character))
                return false;
        }
        return true;
    }
}

internal static class MachineSnapshotFingerprint
{
    /// <summary>
    /// SHA-256 over bounded explicit public fields detects save/load or competing-host drift. It is
    /// deliberately content-derived and therefore cannot provide monotonic authority ordering.
    /// </summary>
    internal static string Compute(MachineReadObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var builder = new StringBuilder(512);
        Append(builder, MachineInteractionSnapshot.CurrentSchemaVersion);
        Append(builder, observation.TargetId);
        Append(builder, observation.LocationId);
        Append(builder, observation.QualifiedItemId);
        Append(builder, observation.LastOutputRuleId);
        Append(builder, observation.MinutesUntilReady);
        Append(builder, observation.ReadyForHarvest);
        Append(builder, observation.ShowNextIndex);
        AppendItem(builder, observation.HeldOutput);
        AppendItem(builder, observation.LastInputItem);
        AppendData(builder, observation.Data);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendData(StringBuilder builder, MachineDataFacts? data)
    {
        if (data is null)
        {
            Append(builder, "<null-data>");
            return;
        }
        Append(builder, data.IsIncubator);
        Append(builder, data.OnlyCompleteOvernight);
        Append(builder, data.HasAdditionalConsumedItems);
        Append(builder, data.HasCustomInteractMethod);
        Append(builder, data.HasClearContentsOvernightCondition);
        var rule = data.ActiveRule;
        if (rule is null)
        {
            Append(builder, "<null-rule>");
            return;
        }
        Append(builder, rule.Id);
        Append(builder, (int)rule.Triggers);
        Append(builder, rule.PrimaryInputRequiredCount);
        Append(builder, rule.MinutesUntilReady);
        Append(builder, rule.DaysUntilReady);
        Append(builder, rule.RecalculateOnCollect);
        Append(builder, rule.HasCustomOutputMethod);
    }

    private static void AppendItem(StringBuilder builder, MachineItemFacts? item)
    {
        if (item is null)
        {
            Append(builder, "<null-item>");
            return;
        }
        Append(builder, item.QualifiedItemId);
        Append(builder, item.Stack);
        Append(builder, item.Quality);
        Append(builder, item.IsRecipe);
        Append(builder, (int)item.Protection);
    }

    private static void Append(StringBuilder builder, string value)
    {
        builder
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('|');
    }

    private static void Append(StringBuilder builder, int value) =>
        Append(builder, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder builder, bool value) =>
        Append(builder, value ? "1" : "0");
}

internal enum MachineSnapshotRegistryUpdateStatus
{
    Accepted,
    IgnoredDuplicate,
    Rejected,
}

internal readonly record struct MachineSnapshotRegistryUpdateResult(
    MachineSnapshotRegistryUpdateStatus Status,
    string Reason
);

internal sealed class MachineSnapshotRegistry
{
    internal const int MaximumTargets = 256;

    private readonly Dictionary<string, MachineInteractionSnapshot> snapshots =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> quarantinedTargets = new(StringComparer.Ordinal);

    internal int Count => snapshots.Count;

    internal MachineSnapshotRegistryUpdateResult Upsert(MachineInteractionSnapshot? snapshot)
    {
        if (!IsValid(snapshot))
            return Rejected(MachineInteractionReasonIds.RegistrySnapshotInvalid);
        if (snapshots.TryGetValue(snapshot!.TargetId, out var current))
        {
            if (!SameIdentity(current, snapshot))
            {
                quarantinedTargets.Add(snapshot.TargetId);
                return Rejected(MachineInteractionReasonIds.RegistrySnapshotConflict);
            }
            if (snapshot.AuthorityRevision < current.AuthorityRevision)
                return Rejected(MachineInteractionReasonIds.RegistrySnapshotStale);
            if (snapshot.AuthorityRevision == current.AuthorityRevision)
            {
                if (Equals(current, snapshot))
                {
                    return new MachineSnapshotRegistryUpdateResult(
                        MachineSnapshotRegistryUpdateStatus.IgnoredDuplicate,
                        MachineInteractionReasonIds.RegistrySnapshotDuplicate
                    );
                }
                // Once the same authority revision describes different public state, the target
                // can no longer safely back a lease. Keep it quarantined until a higher revision
                // re-establishes an ordered snapshot.
                quarantinedTargets.Add(snapshot.TargetId);
                return Rejected(MachineInteractionReasonIds.RegistrySnapshotConflict);
            }
            snapshots[snapshot.TargetId] = snapshot;
            quarantinedTargets.Remove(snapshot.TargetId);
            return Accepted();
        }
        if (snapshots.Count >= MaximumTargets)
            return Rejected(MachineInteractionReasonIds.RegistryFull);
        snapshots.Add(snapshot.TargetId, snapshot);
        return Accepted();
    }

    internal bool TryGet(string targetId, out MachineInteractionSnapshot? snapshot)
    {
        if (quarantinedTargets.Contains(targetId))
        {
            snapshot = null;
            return false;
        }
        if (snapshots.TryGetValue(targetId, out var found))
        {
            snapshot = found;
            return true;
        }
        snapshot = null;
        return false;
    }

    internal string ValidateCurrent(
        string targetId,
        long expectedAuthorityRevision,
        string expectedFingerprint
    )
    {
        if (!snapshots.TryGetValue(targetId, out var current))
            return MachineInteractionReasonIds.RegistryTargetMissing;
        if (quarantinedTargets.Contains(targetId))
            return MachineInteractionReasonIds.SnapshotQuarantined;
        return current.AuthorityRevision == expectedAuthorityRevision
            && string.Equals(
                current.StateFingerprint,
                expectedFingerprint,
                StringComparison.Ordinal
            )
            ? MachineInteractionReasonIds.SnapshotCurrent
            : MachineInteractionReasonIds.SnapshotDrifted;
    }

    internal void Clear()
    {
        snapshots.Clear();
        quarantinedTargets.Clear();
    }

    private static bool IsValid(MachineInteractionSnapshot? snapshot)
    {
        return snapshot is not null
            && snapshot.SchemaVersion == MachineInteractionSnapshot.CurrentSchemaVersion
            && snapshot.AuthorityRevision > 0
            && !string.IsNullOrWhiteSpace(snapshot.TargetId)
            && !string.IsNullOrWhiteSpace(snapshot.LocationId)
            && !string.IsNullOrWhiteSpace(snapshot.QualifiedItemId)
            && snapshot.StateFingerprint.Length == 64;
    }

    private static bool SameIdentity(
        MachineInteractionSnapshot left,
        MachineInteractionSnapshot right
    )
    {
        return string.Equals(left.TargetId, right.TargetId, StringComparison.Ordinal)
            && string.Equals(left.LocationId, right.LocationId, StringComparison.Ordinal)
            && string.Equals(left.QualifiedItemId, right.QualifiedItemId, StringComparison.Ordinal);
    }

    private static MachineSnapshotRegistryUpdateResult Accepted() =>
        new(
            MachineSnapshotRegistryUpdateStatus.Accepted,
            MachineInteractionReasonIds.RegistrySnapshotAccepted
        );

    private static MachineSnapshotRegistryUpdateResult Rejected(string reason) =>
        new(MachineSnapshotRegistryUpdateStatus.Rejected, reason);
}
