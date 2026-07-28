#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;

namespace DontStarve.Player.Stats.Sanity.WorldInteractions.DarkHand;

internal static class DarkHandFireOperationIds
{
    internal const string Extinguish = "dark-hand.fire-thief.extinguish.v1";
}

internal static class DarkHandFireModeIds
{
    internal const string FireThief = "FireThief";
}

internal static class DarkHandFireRuntimeTypeNames
{
    internal const string Torch = "StardewValley.Torch";
    internal const string Furniture = "StardewValley.Objects.Furniture";
}

internal static class DarkHandFireReasonIds
{
    internal const string CatalogLoaded = "dark-hand.fire-targets.loaded";
    internal const string CatalogJsonEmpty = "dark-hand.fire-targets.json-empty";
    internal const string CatalogJsonMalformed = "dark-hand.fire-targets.json-malformed";
    internal const string CatalogRootInvalid = "dark-hand.fire-targets.root-invalid";
    internal const string CatalogVersionUnsupported = "dark-hand.fire-targets.version-unsupported";
    internal const string TargetArrayInvalid = "dark-hand.fire-targets.array-invalid";
    internal const string TargetInvalid = "dark-hand.fire-target.invalid";
    internal const string TargetIdDuplicated = "dark-hand.fire-target.id-duplicated";
    internal const string TargetQualifiedIdDuplicated =
        "dark-hand.fire-target.qualified-id-duplicated";
    internal const string CatalogUnavailable = "dark-hand.fire.capability.catalog-unavailable";
    internal const string NoEnabledTargets = "dark-hand.fire.capability.no-enabled-targets";
    internal const string TargetIdentityUnavailable =
        "dark-hand.fire.capability.target-identity-unavailable";
    internal const string TargetRevisionUnavailable =
        "dark-hand.fire.capability.target-revision-unavailable";
    internal const string ExplainableLightBindingUnavailable =
        "dark-hand.fire.capability.explainable-light-binding-unavailable";
    internal const string AtomicAdapterUnavailable =
        "dark-hand.fire.capability.atomic-adapter-unavailable";
    internal const string PrivateLeaseUnavailable =
        "dark-hand.fire.capability.private-lease-unavailable";
    internal const string CapabilityAvailable = "dark-hand.fire.capability.available";
    internal const string RegistrySnapshotAccepted =
        "dark-hand.fire.registry.snapshot-accepted";
    internal const string RegistrySnapshotDuplicate =
        "dark-hand.fire.registry.snapshot-duplicate";
    internal const string RegistrySnapshotInvalid =
        "dark-hand.fire.registry.snapshot-invalid";
    internal const string RegistrySnapshotConflict =
        "dark-hand.fire.registry.snapshot-conflict";
    internal const string RegistrySnapshotStale =
        "dark-hand.fire.registry.snapshot-stale";
    internal const string RegistryFull = "dark-hand.fire.registry.full";
    internal const string TargetNotRegistered = "dark-hand.fire.target-not-registered";
    internal const string TargetDisabled = "dark-hand.fire.target-disabled";
    internal const string TargetNotAllowlisted = "dark-hand.fire.target-not-allowlisted";
    internal const string HostAuthorityRequired = "dark-hand.fire.host-authority-required";
    internal const string SenderOwnerMismatch = "dark-hand.fire.sender-owner-mismatch";
    internal const string FireThiefModeRequired = "dark-hand.fire.mode-fire-thief-required";
    internal const string TargetMissing = "dark-hand.fire.target-missing";
    internal const string FireAlreadyOff = "dark-hand.fire.target-already-off";
    internal const string TargetRevisionInvalid = "dark-hand.fire.target-revision-invalid";
    internal const string TargetScopeMismatch = "dark-hand.fire.target-scope-mismatch";
    internal const string LocationNotAllowlisted =
        "dark-hand.fire.location-not-allowlisted";
    internal const string OwnerLocationMismatch =
        "dark-hand.fire.owner-location-mismatch";
    internal const string OwnerOutOfRange = "dark-hand.fire.owner-out-of-range";
    internal const string ExplainableLightRequired =
        "dark-hand.fire.explainable-light-required";
    internal const string Eligible = "dark-hand.fire.eligible";
    internal const string CommitAdapterRejected =
        "dark-hand.fire.commit-adapter-rejected";
    internal const string CommitAdapterContractInvalid =
        "dark-hand.fire.commit-adapter-contract-invalid";
    internal const string CommitApplied = "dark-hand.fire.commit-applied";
    internal const string CommitRolledBack = "dark-hand.fire.commit-rolled-back";
    internal const string ReceiptWindowFull = "dark-hand.fire.receipt-window-full";
    internal const string ReceiptDuplicate = "dark-hand.fire.receipt-duplicate";
    internal const string ReceiptConflict = "dark-hand.fire.receipt-conflict";
}

internal enum DarkHandFireTargetKind
{
    Campfire,
    Fireplace,
}

internal sealed class DarkHandFireTargetDefinition
{
    internal DarkHandFireTargetDefinition(
        string id,
        string qualifiedItemId,
        string runtimeTypeFullName,
        DarkHandFireTargetKind targetKind,
        string operationId,
        bool enabled,
        IReadOnlyList<string> locationAllowlist,
        IReadOnlyList<string> evidence,
        string reason
    )
    {
        Id = id;
        QualifiedItemId = qualifiedItemId;
        RuntimeTypeFullName = runtimeTypeFullName;
        TargetKind = targetKind;
        OperationId = operationId;
        Enabled = enabled;
        LocationAllowlist = locationAllowlist;
        Evidence = evidence;
        Reason = reason;
    }

    internal string Id { get; }
    internal string QualifiedItemId { get; }
    internal string RuntimeTypeFullName { get; }
    internal DarkHandFireTargetKind TargetKind { get; }
    internal string OperationId { get; }
    internal bool Enabled { get; }
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

internal readonly record struct DarkHandFireTargetCatalogLoadResult(
    bool IsAvailable,
    string Reason,
    DarkHandFireTargetCatalog Catalog
);

/// <summary>
/// Strict, load-once allowlist. Disabled rows preserve verified vanilla IDs without turning a
/// public IsOn flag or a nearby LightSource into world-mutation authority.
/// </summary>
internal sealed class DarkHandFireTargetCatalog
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumTargets = 32;
    internal const int MaximumAllowlistEntries = 32;
    internal const int MaximumEvidenceEntries = 8;
    internal const string ContractId = "sanity.dark-hand-fire-targets.v1";
    internal const string RelativePath = "Asset/Sanity/Data/dark-hand-targets.json";

    private readonly IReadOnlyList<DarkHandFireTargetDefinition> targets;
    private readonly IReadOnlyDictionary<string, DarkHandFireTargetDefinition> byQualifiedId;

    private DarkHandFireTargetCatalog(
        bool isAvailable,
        int schemaVersion,
        string reason,
        IReadOnlyList<DarkHandFireTargetDefinition> targets,
        IReadOnlyDictionary<string, DarkHandFireTargetDefinition> byQualifiedId
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
    internal IReadOnlyList<DarkHandFireTargetDefinition> Targets => targets;

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

    internal DarkHandFireTargetDefinition? FindByQualifiedItemId(string qualifiedItemId)
    {
        return byQualifiedId.TryGetValue(qualifiedItemId, out var target) ? target : null;
    }

    internal static DarkHandFireTargetCatalog Unavailable(string reason)
    {
        return new DarkHandFireTargetCatalog(
            false,
            0,
            reason,
            Array.Empty<DarkHandFireTargetDefinition>(),
            new ReadOnlyDictionary<string, DarkHandFireTargetDefinition>(
                new Dictionary<string, DarkHandFireTargetDefinition>(StringComparer.Ordinal)
            )
        );
    }

    internal static DarkHandFireTargetCatalogLoadResult Load(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Failed(DarkHandFireReasonIds.CatalogJsonEmpty);

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
                return Failed(DarkHandFireReasonIds.CatalogRootInvalid);
            }
            if (
                !root.TryGetProperty("SchemaVersion", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt32(out var version)
                || version != CurrentSchemaVersion
            )
            {
                return Failed(DarkHandFireReasonIds.CatalogVersionUnsupported);
            }
            if (
                !TryReadRequiredString(root, "ContractId", 128, out var contractId)
                || !string.Equals(contractId, ContractId, StringComparison.Ordinal)
            )
            {
                return Failed(DarkHandFireReasonIds.CatalogRootInvalid);
            }
            if (
                !root.TryGetProperty("Targets", out var targetsElement)
                || targetsElement.ValueKind != JsonValueKind.Array
                || targetsElement.GetArrayLength() > MaximumTargets
            )
            {
                return Failed(DarkHandFireReasonIds.TargetArrayInvalid);
            }

            var parsed = new List<DarkHandFireTargetDefinition>(targetsElement.GetArrayLength());
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var byQualifiedId = new Dictionary<string, DarkHandFireTargetDefinition>(
                StringComparer.Ordinal
            );
            foreach (var element in targetsElement.EnumerateArray())
            {
                if (!TryParseTarget(element, out var target))
                    return Failed(DarkHandFireReasonIds.TargetInvalid);
                if (!ids.Add(target.Id))
                    return Failed(DarkHandFireReasonIds.TargetIdDuplicated);
                if (!byQualifiedId.TryAdd(target.QualifiedItemId, target))
                {
                    return Failed(DarkHandFireReasonIds.TargetQualifiedIdDuplicated);
                }
                parsed.Add(target);
            }

            var catalog = new DarkHandFireTargetCatalog(
                true,
                version,
                DarkHandFireReasonIds.CatalogLoaded,
                parsed.AsReadOnly(),
                new ReadOnlyDictionary<string, DarkHandFireTargetDefinition>(byQualifiedId)
            );
            return new DarkHandFireTargetCatalogLoadResult(true, catalog.Reason, catalog);
        }
        catch (JsonException)
        {
            return Failed(DarkHandFireReasonIds.CatalogJsonMalformed);
        }
    }

    private static bool TryParseTarget(
        JsonElement element,
        out DarkHandFireTargetDefinition target
    )
    {
        target = null!;
        if (
            element.ValueKind != JsonValueKind.Object
            || !HasOnlyProperties(
                element,
                "Id",
                "QualifiedItemId",
                "RuntimeTypeFullName",
                "TargetKind",
                "OperationId",
                "Enabled",
                "LocationAllowlist",
                "Evidence",
                "Reason"
            )
            || !TryReadRequiredString(element, "Id", 128, out var id)
            || !DarkHandInteractionLeaseProtocol.IsIdentifier(id)
            || !TryReadRequiredString(element, "QualifiedItemId", 128, out var qualifiedId)
            || !TryReadRequiredString(
                element,
                "RuntimeTypeFullName",
                256,
                out var runtimeType
            )
            || !TryReadRequiredString(element, "TargetKind", 32, out var kindText)
            || !Enum.TryParse<DarkHandFireTargetKind>(kindText, ignoreCase: false, out var kind)
            || !TryReadRequiredString(element, "OperationId", 128, out var operationId)
            || !string.Equals(operationId, DarkHandFireOperationIds.Extinguish, StringComparison.Ordinal)
            || !element.TryGetProperty("Enabled", out var enabledElement)
            || (enabledElement.ValueKind != JsonValueKind.True && enabledElement.ValueKind != JsonValueKind.False)
            || !TryReadStringArray(
                element,
                "LocationAllowlist",
                MaximumAllowlistEntries,
                256,
                out var locations
            )
            || !TryReadStringArray(
                element,
                "Evidence",
                MaximumEvidenceEntries,
                256,
                out var evidence
            )
            || !TryReadRequiredString(element, "Reason", 256, out var reason)
            || !IsExactQualifiedItemId(qualifiedId, kind)
            || !IsExactRuntimeType(runtimeType, kind)
        )
        {
            return false;
        }

        var enabled = enabledElement.GetBoolean();
        if (enabled && (locations.Count == 0 || evidence.Count == 0))
            return false;

        target = new DarkHandFireTargetDefinition(
            id,
            qualifiedId,
            runtimeType,
            kind,
            operationId,
            enabled,
            locations,
            evidence,
            reason
        );
        return true;
    }

    private static bool IsExactQualifiedItemId(
        string value,
        DarkHandFireTargetKind kind
    )
    {
        var prefix = kind == DarkHandFireTargetKind.Campfire ? "(BC)" : "(F)";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length <= prefix.Length)
            return false;
        return value.IndexOf('*') < 0 && IsSafeString(value);
    }

    private static bool IsExactRuntimeType(
        string value,
        DarkHandFireTargetKind kind
    )
    {
        var expected = kind == DarkHandFireTargetKind.Campfire
            ? DarkHandFireRuntimeTypeNames.Torch
            : DarkHandFireRuntimeTypeNames.Furniture;
        return string.Equals(value, expected, StringComparison.Ordinal);
    }

    private static bool TryReadRequiredString(
        JsonElement parent,
        string propertyName,
        int maximumLength,
        out string value
    )
    {
        value = string.Empty;
        if (
            !parent.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String
        )
        {
            return false;
        }
        value = element.GetString() ?? string.Empty;
        return value.Length <= maximumLength && IsSafeString(value);
    }

    private static bool TryReadStringArray(
        JsonElement parent,
        string propertyName,
        int maximumCount,
        int maximumLength,
        out IReadOnlyList<string> values
    )
    {
        values = Array.Empty<string>();
        if (
            !parent.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() > maximumCount
        )
        {
            return false;
        }

        var parsed = new List<string>(element.GetArrayLength());
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
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

    private static bool HasOnlyProperties(JsonElement element, params string[] allowed)
    {
        var set = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!set.Remove(property.Name))
                return false;
        }
        return set.Count == 0;
    }

    private static DarkHandFireTargetCatalogLoadResult Failed(string reason)
    {
        return new DarkHandFireTargetCatalogLoadResult(
            false,
            reason,
            Unavailable(reason)
        );
    }
}

internal readonly record struct DarkHandFireRuntimeEvidence(
    bool StableTargetIdentityAndStateAvailable,
    bool StableTargetRevisionAvailable,
    bool ExplainableLightTargetBindingAvailable,
    bool AtomicExtinguishAdapterAvailable,
    bool ExistingPrivateLeaseAvailable
)
{
    internal static DarkHandFireRuntimeEvidence Current =>
        new(
            StableTargetIdentityAndStateAvailable: true,
            StableTargetRevisionAvailable: false,
            ExplainableLightTargetBindingAvailable: false,
            AtomicExtinguishAdapterAvailable: false,
            ExistingPrivateLeaseAvailable: true
        );

    internal static DarkHandFireRuntimeEvidence VerifiedStardew1615 =>
        new(
            StableTargetIdentityAndStateAvailable: true,
            StableTargetRevisionAvailable: true,
            ExplainableLightTargetBindingAvailable: true,
            AtomicExtinguishAdapterAvailable: true,
            ExistingPrivateLeaseAvailable: true
        );
}

internal enum DarkHandFireCapabilityStatus
{
    Available,
    UnavailableCatalog,
    DisabledNoEnabledTargets,
    UnavailableTargetIdentity,
    UnavailableTargetRevision,
    UnavailableExplainableLightBinding,
    UnavailableAtomicAdapter,
    UnavailablePrivateLease,
}

internal readonly record struct DarkHandFireCapability(
    DarkHandFireCapabilityStatus Status,
    string Reason,
    int EnabledTargetCount,
    DarkHandFireRuntimeEvidence Evidence
)
{
    internal bool CanExecute => Status == DarkHandFireCapabilityStatus.Available;
}

internal static class DarkHandFireCapabilityGate
{
    internal static DarkHandFireCapability Evaluate(
        DarkHandFireTargetCatalog catalog,
        DarkHandFireRuntimeEvidence evidence
    )
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!catalog.IsAvailable)
            return Result(DarkHandFireCapabilityStatus.UnavailableCatalog, DarkHandFireReasonIds.CatalogUnavailable);
        if (catalog.EnabledTargetCount == 0)
            return Result(DarkHandFireCapabilityStatus.DisabledNoEnabledTargets, DarkHandFireReasonIds.NoEnabledTargets);
        if (!evidence.StableTargetIdentityAndStateAvailable)
            return Result(DarkHandFireCapabilityStatus.UnavailableTargetIdentity, DarkHandFireReasonIds.TargetIdentityUnavailable);
        if (!evidence.StableTargetRevisionAvailable)
            return Result(DarkHandFireCapabilityStatus.UnavailableTargetRevision, DarkHandFireReasonIds.TargetRevisionUnavailable);
        if (!evidence.ExplainableLightTargetBindingAvailable)
            return Result(DarkHandFireCapabilityStatus.UnavailableExplainableLightBinding, DarkHandFireReasonIds.ExplainableLightBindingUnavailable);
        if (!evidence.AtomicExtinguishAdapterAvailable)
            return Result(DarkHandFireCapabilityStatus.UnavailableAtomicAdapter, DarkHandFireReasonIds.AtomicAdapterUnavailable);
        if (!evidence.ExistingPrivateLeaseAvailable)
            return Result(DarkHandFireCapabilityStatus.UnavailablePrivateLease, DarkHandFireReasonIds.PrivateLeaseUnavailable);
        return Result(DarkHandFireCapabilityStatus.Available, DarkHandFireReasonIds.CapabilityAvailable);

        DarkHandFireCapability Result(
            DarkHandFireCapabilityStatus status,
            string reason
        ) => new(status, reason, catalog.EnabledTargetCount, evidence);
    }
}

internal sealed record DarkHandFireTargetSnapshot(
    string DefinitionId,
    string TargetId,
    string LocationId,
    string QualifiedItemId,
    string RuntimeTypeFullName,
    DarkHandFireTargetKind TargetKind,
    string OperationId,
    long Revision,
    bool IsPresent,
    bool IsOn,
    bool HasExplainableLightSource,
    bool AtomicAdapterAvailable
);

internal readonly record struct DarkHandFireOperationScope(
    bool IsHostAuthority,
    string SenderPlayerKey,
    string OwnerPlayerKey,
    string ModeId,
    string OwnerLocationId,
    double OwnerDistanceTiles
);

internal readonly record struct DarkHandFireEligibilityResult(
    bool IsEligible,
    string Reason,
    DarkHandFireTargetDefinition? Definition
);

internal static class DarkHandFireEligibilityGate
{
    internal const double MaximumRangeTiles = 20d;

    internal static DarkHandFireEligibilityResult Evaluate(
        DarkHandFireTargetCatalog catalog,
        DarkHandFireCapability capability,
        DarkHandFireTargetSnapshot? snapshot,
        DarkHandFireOperationScope scope,
        bool requireFireOn
    )
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!capability.CanExecute)
            return Rejected(capability.Reason);
        if (!scope.IsHostAuthority)
            return Rejected(DarkHandFireReasonIds.HostAuthorityRequired);
        if (
            !SanityPlayerKey.IsCanonical(scope.OwnerPlayerKey)
            || !string.Equals(
                scope.SenderPlayerKey,
                scope.OwnerPlayerKey,
                StringComparison.Ordinal
            )
        )
        {
            return Rejected(DarkHandFireReasonIds.SenderOwnerMismatch);
        }
        if (!string.Equals(scope.ModeId, DarkHandFireModeIds.FireThief, StringComparison.Ordinal))
            return Rejected(DarkHandFireReasonIds.FireThiefModeRequired);
        if (snapshot is null || !snapshot.IsPresent)
            return Rejected(DarkHandFireReasonIds.TargetMissing);
        if (snapshot.Revision <= 0)
            return Rejected(DarkHandFireReasonIds.TargetRevisionInvalid);
        if (
            !DarkHandInteractionLeaseProtocol.IsIdentifier(snapshot.TargetId)
            || !DarkHandInteractionLeaseProtocol.IsIdentifier(snapshot.LocationId)
            || !string.Equals(snapshot.OperationId, DarkHandFireOperationIds.Extinguish, StringComparison.Ordinal)
        )
        {
            return Rejected(DarkHandFireReasonIds.TargetScopeMismatch);
        }

        var definition = catalog.FindByQualifiedItemId(snapshot.QualifiedItemId);
        if (definition is null)
            return Rejected(DarkHandFireReasonIds.TargetNotAllowlisted);
        if (!definition.Enabled)
            return Rejected(DarkHandFireReasonIds.TargetDisabled, definition);
        if (
            !string.Equals(definition.Id, snapshot.DefinitionId, StringComparison.Ordinal)
            || !string.Equals(
                definition.RuntimeTypeFullName,
                snapshot.RuntimeTypeFullName,
                StringComparison.Ordinal
            )
            || definition.TargetKind != snapshot.TargetKind
            || !string.Equals(definition.OperationId, snapshot.OperationId, StringComparison.Ordinal)
        )
        {
            return Rejected(DarkHandFireReasonIds.TargetScopeMismatch, definition);
        }
        if (!definition.AllowsLocation(snapshot.LocationId))
            return Rejected(DarkHandFireReasonIds.LocationNotAllowlisted, definition);
        if (!string.Equals(scope.OwnerLocationId, snapshot.LocationId, StringComparison.Ordinal))
            return Rejected(DarkHandFireReasonIds.OwnerLocationMismatch, definition);
        if (
            !double.IsFinite(scope.OwnerDistanceTiles)
            || scope.OwnerDistanceTiles < 0d
            || scope.OwnerDistanceTiles > MaximumRangeTiles
        )
        {
            return Rejected(DarkHandFireReasonIds.OwnerOutOfRange, definition);
        }
        if (!snapshot.HasExplainableLightSource)
            return Rejected(DarkHandFireReasonIds.ExplainableLightRequired, definition);
        if (!snapshot.AtomicAdapterAvailable)
            return Rejected(DarkHandFireReasonIds.AtomicAdapterUnavailable, definition);
        if (requireFireOn && !snapshot.IsOn)
            return Rejected(DarkHandFireReasonIds.FireAlreadyOff, definition);

        return new DarkHandFireEligibilityResult(
            true,
            DarkHandFireReasonIds.Eligible,
            definition
        );
    }

    private static DarkHandFireEligibilityResult Rejected(
        string reason,
        DarkHandFireTargetDefinition? definition = null
    ) => new(false, reason, definition);
}

internal enum DarkHandFireRegistryUpdateStatus
{
    Accepted,
    IgnoredDuplicate,
    Rejected,
}

internal readonly record struct DarkHandFireRegistryUpdateResult(
    DarkHandFireRegistryUpdateStatus Status,
    string Reason
);

/// <summary>
/// Bounded exact-target registry. It accepts a revision only from an injected authority; it never
/// manufactures one from animation frames, local ticks, object references, or LightSource IDs.
/// </summary>
internal sealed class DarkHandFireTargetRegistry : IDarkHandLeaseTargetAuthority
{
    internal const int MaximumTargets = 256;

    private readonly DarkHandFireTargetCatalog catalog;
    private readonly Dictionary<string, DarkHandFireTargetSnapshot> snapshots =
        new(StringComparer.Ordinal);

    internal DarkHandFireTargetRegistry(DarkHandFireTargetCatalog catalog)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    internal int Count => snapshots.Count;

    internal DarkHandFireRegistryUpdateResult Upsert(DarkHandFireTargetSnapshot? snapshot)
    {
        if (!IsStructurallyValid(snapshot))
            return Rejected(DarkHandFireReasonIds.RegistrySnapshotInvalid);

        if (snapshots.TryGetValue(snapshot!.TargetId, out var existing))
        {
            if (!SameIdentity(existing, snapshot))
                return Rejected(DarkHandFireReasonIds.RegistrySnapshotConflict);
            if (snapshot.Revision < existing.Revision)
                return Rejected(DarkHandFireReasonIds.RegistrySnapshotStale);
            if (snapshot.Revision == existing.Revision)
            {
                return Equals(existing, snapshot)
                    ? new DarkHandFireRegistryUpdateResult(
                        DarkHandFireRegistryUpdateStatus.IgnoredDuplicate,
                        DarkHandFireReasonIds.RegistrySnapshotDuplicate
                    )
                    : Rejected(DarkHandFireReasonIds.RegistrySnapshotConflict);
            }
            snapshots[snapshot.TargetId] = snapshot;
            return Accepted();
        }

        if (snapshots.Count >= MaximumTargets)
            return Rejected(DarkHandFireReasonIds.RegistryFull);
        snapshots.Add(snapshot.TargetId, snapshot);
        return Accepted();
    }

    internal bool TryGet(string targetId, out DarkHandFireTargetSnapshot? snapshot)
    {
        if (snapshots.TryGetValue(targetId, out var found))
        {
            snapshot = found;
            return true;
        }
        snapshot = null;
        return false;
    }

    public bool TryResolve(
        string targetId,
        out DarkHandLeaseTargetSnapshot? target,
        out string reason
    )
    {
        if (!TryGet(targetId, out var snapshot) || snapshot is null)
        {
            target = null;
            reason = DarkHandFireReasonIds.TargetNotRegistered;
            return false;
        }
        target = new DarkHandLeaseTargetSnapshot(
            snapshot.TargetId,
            snapshot.LocationId,
            snapshot.OperationId,
            snapshot.Revision
        );
        reason = "dark-hand.fire.registry.target-resolved";
        return true;
    }

    internal void Clear()
    {
        snapshots.Clear();
    }

    private bool IsStructurallyValid(DarkHandFireTargetSnapshot? snapshot)
    {
        if (
            snapshot is null
            || !DarkHandInteractionLeaseProtocol.IsIdentifier(snapshot.TargetId)
            || !DarkHandInteractionLeaseProtocol.IsIdentifier(snapshot.LocationId)
            || snapshot.Revision <= 0
        )
        {
            return false;
        }
        var definition = catalog.FindByQualifiedItemId(snapshot.QualifiedItemId);
        return definition is not null
            && string.Equals(definition.Id, snapshot.DefinitionId, StringComparison.Ordinal)
            && string.Equals(
                definition.RuntimeTypeFullName,
                snapshot.RuntimeTypeFullName,
                StringComparison.Ordinal
            )
            && definition.TargetKind == snapshot.TargetKind
            && string.Equals(definition.OperationId, snapshot.OperationId, StringComparison.Ordinal);
    }

    private static bool SameIdentity(
        DarkHandFireTargetSnapshot left,
        DarkHandFireTargetSnapshot right
    )
    {
        return string.Equals(left.DefinitionId, right.DefinitionId, StringComparison.Ordinal)
            && string.Equals(left.TargetId, right.TargetId, StringComparison.Ordinal)
            && string.Equals(left.LocationId, right.LocationId, StringComparison.Ordinal)
            && string.Equals(left.QualifiedItemId, right.QualifiedItemId, StringComparison.Ordinal)
            && string.Equals(
                left.RuntimeTypeFullName,
                right.RuntimeTypeFullName,
                StringComparison.Ordinal
            )
            && left.TargetKind == right.TargetKind
            && string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal);
    }

    private static DarkHandFireRegistryUpdateResult Accepted() =>
        new(
            DarkHandFireRegistryUpdateStatus.Accepted,
            DarkHandFireReasonIds.RegistrySnapshotAccepted
        );

    private static DarkHandFireRegistryUpdateResult Rejected(string reason) =>
        new(DarkHandFireRegistryUpdateStatus.Rejected, reason);
}

internal sealed record DarkHandFireCommitPlan(
    string OwnerPlayerKey,
    string TargetId,
    string LocationId,
    string QualifiedItemId,
    string RuntimeTypeFullName,
    DarkHandFireTargetKind TargetKind,
    long TargetRevision,
    string LeaseId
);

internal enum DarkHandFireAdapterStatus
{
    Applied,
    Rejected,
    RolledBack,
}

internal readonly record struct DarkHandFireAdapterResult(
    DarkHandFireAdapterStatus Status,
    string Reason,
    bool TargetStillPresent,
    bool IsFireOnAfter,
    bool LightRefreshRequested
);

internal interface IDarkHandFireExtinguishAdapter
{
    DarkHandFireAdapterResult Commit(DarkHandFireCommitPlan plan);
}

internal enum DarkHandFireCommitStatus
{
    Applied,
    AlreadyExtinguished,
    RolledBack,
    Duplicate,
    Rejected,
}

internal enum DarkHandFireReceiptOutcome
{
    Applied,
    AlreadyExtinguished,
    RolledBack,
    Rejected,
}

internal sealed record DarkHandFireReceipt(
    int SchemaVersion,
    string LeaseId,
    string SessionId,
    string OwnerPlayerKey,
    DarkHandFireReceiptOutcome Outcome,
    string Reason,
    bool WorldMutationApplied,
    bool LightRefreshRequested
)
{
    internal const int CurrentSchemaVersion = 1;
}

internal readonly record struct DarkHandFireCommitResult(
    DarkHandFireCommitStatus Status,
    string Reason,
    bool WorldMutationApplied,
    bool LightRefreshRequested,
    DarkHandFireReceipt? Receipt = null
)
{
    internal bool Applied => Status == DarkHandFireCommitStatus.Applied;
}

/// <summary>
/// Pure host operation that consumes the existing task-07 one-shot lease. Production does not
/// construct it until target revision, exact light binding, and an atomic adapter are all proven.
/// </summary>
internal sealed class DarkHandFireThiefOperationService
{
    internal const int MaximumReceipts = 256;

    private sealed record ReceiptEntry(
        DarkHandInteractionLease Lease,
        DarkHandFireReceipt Receipt
    );

    private readonly DarkHandFireTargetCatalog catalog;
    private readonly DarkHandFireCapability capability;
    private readonly DarkHandInteractionLeaseAuthority leaseAuthority;
    private readonly DarkHandFireTargetRegistry registry;
    private readonly Dictionary<string, ReceiptEntry> receipts =
        new(StringComparer.Ordinal);

    internal DarkHandFireThiefOperationService(
        DarkHandFireTargetCatalog catalog,
        DarkHandFireCapability capability,
        DarkHandInteractionLeaseAuthority leaseAuthority,
        DarkHandFireTargetRegistry registry
    )
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.capability = capability;
        this.leaseAuthority = leaseAuthority
            ?? throw new ArgumentNullException(nameof(leaseAuthority));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    internal int SuccessfulCommitCount { get; private set; }
    internal int ReceiptCount => receipts.Count;

    internal DarkHandLeaseIssueResult TryIssue(
        DarkHandInteractionLeaseRequest? request,
        DarkHandFireOperationScope scope,
        long nowTick,
        bool requireExclusiveTargetOwner = false
    )
    {
        if (!capability.CanExecute)
        {
            return new DarkHandLeaseIssueResult(
                DarkHandLeaseIssueStatus.Rejected,
                capability.Reason,
                null
            );
        }

        return leaseAuthority.TryIssue(
            request,
            scope.SenderPlayerKey,
            nowTick,
            new ScopedTargetAuthority(catalog, capability, registry, scope),
            requireExclusiveTargetOwner
        );
    }

    internal DarkHandFireCommitResult Commit(
        DarkHandInteractionLease? lease,
        DarkHandFireOperationScope scope,
        long nowTick,
        IDarkHandFireExtinguishAdapter adapter
    )
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (!capability.CanExecute)
            return Rejected(capability.Reason);
        if (
            lease is not null
            && receipts.TryGetValue(lease.LeaseId, out var existing)
        )
        {
            return DarkHandInteractionLeaseProtocol.SameLease(existing.Lease, lease)
                ? new DarkHandFireCommitResult(
                    DarkHandFireCommitStatus.Duplicate,
                    DarkHandFireReasonIds.ReceiptDuplicate,
                    WorldMutationApplied: false,
                    LightRefreshRequested: false,
                    existing.Receipt
                )
                : Rejected(DarkHandFireReasonIds.ReceiptConflict);
        }
        if (receipts.Count >= MaximumReceipts)
            return Rejected(DarkHandFireReasonIds.ReceiptWindowFull);

        registry.TryGet(lease?.TargetId ?? string.Empty, out var snapshot);
        var currentTarget = snapshot is null
            ? null
            : new DarkHandLeaseTargetSnapshot(
                snapshot.TargetId,
                snapshot.LocationId,
                snapshot.OperationId,
                snapshot.Revision
            );
        var validation = leaseAuthority.ValidateAndConsume(
            lease,
            scope.SenderPlayerKey,
            nowTick,
            currentTarget
        );
        if (!validation.Accepted)
            return Rejected(validation.Reason);

        var eligibility = DarkHandFireEligibilityGate.Evaluate(
            catalog,
            capability,
            snapshot,
            scope,
            requireFireOn: false
        );
        if (!eligibility.IsEligible)
            return Complete(
                lease!,
                DarkHandFireCommitStatus.Rejected,
                DarkHandFireReceiptOutcome.Rejected,
                eligibility.Reason,
                WorldMutationApplied: false,
                LightRefreshRequested: false
            );
        if (!snapshot!.IsOn)
        {
            return Complete(
                lease!,
                DarkHandFireCommitStatus.AlreadyExtinguished,
                DarkHandFireReceiptOutcome.AlreadyExtinguished,
                DarkHandFireReasonIds.FireAlreadyOff,
                WorldMutationApplied: false,
                LightRefreshRequested: false
            );
        }

        var adapterResult = adapter.Commit(
            new DarkHandFireCommitPlan(
                scope.OwnerPlayerKey,
                snapshot.TargetId,
                snapshot.LocationId,
                snapshot.QualifiedItemId,
                snapshot.RuntimeTypeFullName,
                snapshot.TargetKind,
                snapshot.Revision,
                lease!.LeaseId
            )
        );
        if (adapterResult.Status != DarkHandFireAdapterStatus.Applied)
        {
            var rolledBack = adapterResult.Status == DarkHandFireAdapterStatus.RolledBack;
            return Complete(
                lease!,
                rolledBack
                    ? DarkHandFireCommitStatus.RolledBack
                    : DarkHandFireCommitStatus.Rejected,
                rolledBack
                    ? DarkHandFireReceiptOutcome.RolledBack
                    : DarkHandFireReceiptOutcome.Rejected,
                DarkHandFireReasonIds.CommitAdapterRejected,
                WorldMutationApplied: false,
                LightRefreshRequested: false
            );
        }
        if (
            !adapterResult.TargetStillPresent
            || adapterResult.IsFireOnAfter
            || !adapterResult.LightRefreshRequested
        )
        {
            return Complete(
                lease!,
                DarkHandFireCommitStatus.Rejected,
                DarkHandFireReceiptOutcome.Rejected,
                DarkHandFireReasonIds.CommitAdapterContractInvalid,
                WorldMutationApplied: false,
                LightRefreshRequested: false
            );
        }

        SuccessfulCommitCount++;
        return Complete(
            lease!,
            DarkHandFireCommitStatus.Applied,
            DarkHandFireReceiptOutcome.Applied,
            DarkHandFireReasonIds.CommitApplied,
            WorldMutationApplied: true,
            LightRefreshRequested: true
        );
    }

    internal void ClearWindow()
    {
        receipts.Clear();
        SuccessfulCommitCount = 0;
    }

    private DarkHandFireCommitResult Complete(
        DarkHandInteractionLease lease,
        DarkHandFireCommitStatus status,
        DarkHandFireReceiptOutcome outcome,
        string reason,
        bool WorldMutationApplied,
        bool LightRefreshRequested
    )
    {
        var receipt = new DarkHandFireReceipt(
            DarkHandFireReceipt.CurrentSchemaVersion,
            lease.LeaseId,
            lease.SessionId,
            lease.OwnerPlayerKey,
            outcome,
            reason,
            WorldMutationApplied,
            LightRefreshRequested
        );
        receipts.Add(lease.LeaseId, new ReceiptEntry(lease.Clone(), receipt));
        return new DarkHandFireCommitResult(
            status,
            reason,
            WorldMutationApplied,
            LightRefreshRequested,
            receipt
        );
    }

    private static DarkHandFireCommitResult Rejected(string reason) =>
        new(
            DarkHandFireCommitStatus.Rejected,
            reason,
            WorldMutationApplied: false,
            LightRefreshRequested: false
        );

    private sealed class ScopedTargetAuthority : IDarkHandLeaseTargetAuthority
    {
        private readonly DarkHandFireTargetCatalog catalog;
        private readonly DarkHandFireCapability capability;
        private readonly DarkHandFireTargetRegistry registry;
        private readonly DarkHandFireOperationScope scope;

        internal ScopedTargetAuthority(
            DarkHandFireTargetCatalog catalog,
            DarkHandFireCapability capability,
            DarkHandFireTargetRegistry registry,
            DarkHandFireOperationScope scope
        )
        {
            this.catalog = catalog;
            this.capability = capability;
            this.registry = registry;
            this.scope = scope;
        }

        public bool TryResolve(
            string targetId,
            out DarkHandLeaseTargetSnapshot? target,
            out string reason
        )
        {
            if (!registry.TryGet(targetId, out var snapshot) || snapshot is null)
            {
                target = null;
                reason = DarkHandFireReasonIds.TargetNotRegistered;
                return false;
            }
            var eligibility = DarkHandFireEligibilityGate.Evaluate(
                catalog,
                capability,
                snapshot,
                scope,
                requireFireOn: true
            );
            if (!eligibility.IsEligible)
            {
                target = null;
                reason = eligibility.Reason;
                return false;
            }
            target = new DarkHandLeaseTargetSnapshot(
                snapshot.TargetId,
                snapshot.LocationId,
                snapshot.OperationId,
                snapshot.Revision
            );
            reason = DarkHandFireReasonIds.Eligible;
            return true;
        }
    }
}
