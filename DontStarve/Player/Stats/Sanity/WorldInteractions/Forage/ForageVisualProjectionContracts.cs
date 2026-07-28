#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity.Illusions.Projection;
using DontStarve.Resource.Sanity;

namespace DontStarve.Player.Stats.Sanity.WorldInteractions.Forage;

internal static class ForageVisualProjectionReasonIds
{
    internal const string SanityEligible = "forage.projection.sanity-eligible";
    internal const string SanityAboveThreshold = "forage.projection.sanity-above-threshold";
    internal const string SanitySnapshotInvalid = "forage.projection.sanity-snapshot-invalid";
    internal const string CatalogUnavailable = "forage.projection.catalog-unavailable";
    internal const string NoEnabledGroundMappings =
        "forage.projection.no-enabled-ground-mappings";
    internal const string EnabledGroundMappingsUnsupported =
        "forage.projection.enabled-ground-mappings-unsupported";
    internal const string GroundMappingsAvailable =
        "forage.projection.ground-mappings-available";
    internal const string RabbitVisualAvailable = "forage.projection.rabbit-visual-available";
    internal const string RabbitVisualPlaceholderAvailable =
        "forage.projection.rabbit-visual-placeholder-available";
    internal const string RabbitVisualUnavailable =
        "forage.projection.rabbit-visual-unavailable";
    internal const string RabbitVisualContractInvalid =
        "forage.projection.rabbit-visual-contract-invalid";
    internal const string CacheRefreshed = "forage.projection.cache-refreshed";
    internal const string CacheTierInactive = "forage.projection.cache-tier-inactive";
    internal const string CacheEventBlocked = "forage.projection.cache-event-blocked";
    internal const string CacheResourceUnavailable =
        "forage.projection.cache-resource-unavailable";
}

internal readonly record struct ForageSanityEligibility(
    bool IsEligible,
    string Reason,
    double? Ratio
);

/// <summary>
/// The forage interaction family has one exact 40% boundary. Projection and stage-04
/// picker-time adjudication both reuse it instead of defining a second threshold.
/// </summary>
internal static class ForageInteractionSanityGate
{
    internal const double MaximumRatio = 0.4d;

    internal static ForageSanityEligibility Evaluate(double current, double maximum)
    {
        if (
            !double.IsFinite(current)
            || !double.IsFinite(maximum)
            || maximum <= 0d
            || current < 0d
            || current > maximum
        )
        {
            return new ForageSanityEligibility(
                false,
                ForageVisualProjectionReasonIds.SanitySnapshotInvalid,
                null
            );
        }

        var ratio = current / maximum;
        return ratio <= MaximumRatio
            ? new ForageSanityEligibility(
                true,
                ForageVisualProjectionReasonIds.SanityEligible,
                ratio
            )
            : new ForageSanityEligibility(
                false,
                ForageVisualProjectionReasonIds.SanityAboveThreshold,
                ratio
            );
    }
}

internal enum ForageGroundProjectionCapabilityStatus
{
    CatalogUnavailable,
    DisabledNoEnabledMappings,
    UnavailableEnabledMappings,
    Available,
}

internal readonly record struct ForageGroundProjectionCapability(
    ForageGroundProjectionCapabilityStatus Status,
    string Reason,
    int EnabledMappingCount
)
{
    internal bool CanProjectGroundObjects =>
        Status == ForageGroundProjectionCapabilityStatus.Available;
}

internal static class ForageGroundProjectionCapabilityGate
{
    internal static ForageGroundProjectionCapability Evaluate(
        ForageReplacementCatalog catalog
    )
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!catalog.IsAvailable)
        {
            return new ForageGroundProjectionCapability(
                ForageGroundProjectionCapabilityStatus.CatalogUnavailable,
                ForageVisualProjectionReasonIds.CatalogUnavailable,
                0
            );
        }

        var enabledCount = 0;
        foreach (var mapping in catalog.Mappings)
        {
            if (mapping.Enabled)
                enabledCount++;
        }

        if (enabledCount == 0)
        {
            return new ForageGroundProjectionCapability(
                ForageGroundProjectionCapabilityStatus.DisabledNoEnabledMappings,
                ForageVisualProjectionReasonIds.NoEnabledGroundMappings,
                0
            );
        }

        return new ForageGroundProjectionCapability(
            ForageGroundProjectionCapabilityStatus.Available,
            ForageVisualProjectionReasonIds.GroundMappingsAvailable,
            enabledCount
        );
    }
}

internal readonly record struct ForageRabbitVisualDescriptor(
    bool IsAvailable,
    string RequestedSlotId,
    string TextureSlotId,
    SanityVisualPreviewKind PreviewKind,
    SanityResourceRectangle SourceRectangle,
    SanityResourcePoint? PivotSourcePx,
    double DrawScale,
    bool OwnerLocalOnly,
    bool IsPlaceholder,
    bool IsProvisional,
    string DiagnosticReason
);

internal enum ForageRabbitVisualCapabilityStatus
{
    Unavailable,
    Available,
    AvailableDevelopmentPlaceholder,
}

internal readonly record struct ForageRabbitVisualCapability(
    ForageRabbitVisualCapabilityStatus Status,
    string Reason,
    bool IsPlaceholder,
    bool IsProvisional
)
{
    internal bool IsAvailable =>
        Status is ForageRabbitVisualCapabilityStatus.Available
            or ForageRabbitVisualCapabilityStatus.AvailableDevelopmentPlaceholder;
}

internal static class ForageRabbitVisualContract
{
    internal const string TextureSlotId = "sanity.asset.beard-rabbit.sprite";
    internal const int Width = 64;
    internal const int Height = 64;
    internal static readonly SanityResourcePoint PivotSourcePx = new(32, 60);
    internal const double DrawScale = 1d;

    internal static ForageRabbitVisualCapability Evaluate(
        ForageRabbitVisualDescriptor descriptor
    )
    {
        if (!descriptor.IsAvailable)
        {
            return new ForageRabbitVisualCapability(
                ForageRabbitVisualCapabilityStatus.Unavailable,
                string.IsNullOrWhiteSpace(descriptor.DiagnosticReason)
                    ? ForageVisualProjectionReasonIds.RabbitVisualUnavailable
                    : descriptor.DiagnosticReason,
                descriptor.IsPlaceholder,
                descriptor.IsProvisional
            );
        }

        if (
            !string.Equals(
                descriptor.RequestedSlotId,
                TextureSlotId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                descriptor.TextureSlotId,
                TextureSlotId,
                StringComparison.Ordinal
            )
            || descriptor.PreviewKind != SanityVisualPreviewKind.StaticSprite
            || descriptor.SourceRectangle.X != 0
            || descriptor.SourceRectangle.Y != 0
            || descriptor.SourceRectangle.Width != Width
            || descriptor.SourceRectangle.Height != Height
            || descriptor.PivotSourcePx != PivotSourcePx
            || Math.Abs(descriptor.DrawScale - DrawScale) > 0.0001d
            || !descriptor.OwnerLocalOnly
        )
        {
            return new ForageRabbitVisualCapability(
                ForageRabbitVisualCapabilityStatus.Unavailable,
                ForageVisualProjectionReasonIds.RabbitVisualContractInvalid,
                descriptor.IsPlaceholder,
                descriptor.IsProvisional
            );
        }

        var developmentOnly = descriptor.IsPlaceholder || descriptor.IsProvisional;
        return new ForageRabbitVisualCapability(
            developmentOnly
                ? ForageRabbitVisualCapabilityStatus.AvailableDevelopmentPlaceholder
                : ForageRabbitVisualCapabilityStatus.Available,
            developmentOnly
                ? ForageVisualProjectionReasonIds.RabbitVisualPlaceholderAvailable
                : ForageVisualProjectionReasonIds.RabbitVisualAvailable,
            descriptor.IsPlaceholder,
            descriptor.IsProvisional
        );
    }
}

internal readonly record struct ForageProjectionOwnerScreenKey(
    string PlayerKey,
    int ScreenId
);

internal enum ForageVisualProjectionRefreshStatus
{
    Refreshed,
    TierInactive,
    EventBlocked,
    ResourceUnavailable,
}

internal readonly record struct ForageVisualProjectionRefreshResult(
    ForageVisualProjectionRefreshStatus Status,
    string Reason,
    int InspectedCandidateCount,
    int RabbitProjectionCount,
    int GroundObjectProjectionCount
);

internal sealed class ForageGroundProjectionCandidate
{
    internal ForageGroundProjectionCandidate(
        object sourceReference,
        object replacementReference,
        int tileX,
        int tileY,
        int sourceStack,
        int sourceQuality,
        int catalogRevision,
        string mappingId
    )
    {
        SourceReference =
            sourceReference ?? throw new ArgumentNullException(nameof(sourceReference));
        ReplacementReference =
            replacementReference
            ?? throw new ArgumentNullException(nameof(replacementReference));
        TileX = tileX;
        TileY = tileY;
        SourceStack = sourceStack;
        SourceQuality = sourceQuality;
        CatalogRevision = catalogRevision;
        MappingId = mappingId ?? throw new ArgumentNullException(nameof(mappingId));
    }

    internal object SourceReference { get; }
    internal object ReplacementReference { get; }
    internal int TileX { get; }
    internal int TileY { get; }
    internal int SourceStack { get; }
    internal int SourceQuality { get; }
    internal int CatalogRevision { get; }
    internal string MappingId { get; }

    internal bool Matches(
        object sourceReference,
        int tileX,
        int tileY,
        int sourceStack,
        int sourceQuality,
        int catalogRevision
    )
    {
        return ReferenceEquals(SourceReference, sourceReference)
            && TileX == tileX
            && TileY == tileY
            && SourceStack == sourceStack
            && SourceQuality == sourceQuality
            && CatalogRevision == catalogRevision;
    }
}

/// <summary>
/// Mod-private render cache keyed by the task-family-04 owner/screen/location identity. It holds
/// only object references already present in the current location and never writes them back.
/// </summary>
internal sealed class ForageVisualProjectionCache
{
    private sealed class OwnerEntry
    {
        internal OwnerEntry(HarmlessProjectionOwnerContext owner)
        {
            Owner = owner;
            Rabbits = new HashSet<object>(ReferenceEqualityComparer.Instance);
            GroundObjects = new Dictionary<object, ForageGroundProjectionCandidate>(
                ReferenceEqualityComparer.Instance
            );
        }

        internal HarmlessProjectionOwnerContext Owner { get; }
        internal HashSet<object> Rabbits { get; set; }
        internal Dictionary<object, ForageGroundProjectionCandidate> GroundObjects { get; set; }
    }

    internal const int MaximumRabbitCandidatesPerOwnerScreen = 64;
    internal const int MaximumGroundObjectCandidatesPerOwnerScreen = 64;

    private readonly Dictionary<ForageProjectionOwnerScreenKey, OwnerEntry> entries =
        new();

    internal int OwnerScreenCount => entries.Count;

    internal int RabbitProjectionCount
    {
        get
        {
            var count = 0;
            foreach (var entry in entries.Values)
                count += entry.Rabbits.Count;
            return count;
        }
    }

    internal int GroundObjectProjectionCount
    {
        get
        {
            var count = 0;
            foreach (var entry in entries.Values)
                count += entry.GroundObjects.Count;
            return count;
        }
    }

    internal ForageVisualProjectionRefreshResult RefreshRabbits(
        HarmlessProjectionOwnerContext owner,
        IEnumerable<object?> rabbitCandidates,
        bool tierEligible,
        bool eventBlocked,
        bool visualAvailable
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(rabbitCandidates);
        var key = Key(owner);

        if (!tierEligible)
        {
            entries.Remove(key);
            return Empty(
                ForageVisualProjectionRefreshStatus.TierInactive,
                ForageVisualProjectionReasonIds.CacheTierInactive
            );
        }
        if (eventBlocked)
        {
            entries.Remove(key);
            return Empty(
                ForageVisualProjectionRefreshStatus.EventBlocked,
                ForageVisualProjectionReasonIds.CacheEventBlocked
            );
        }
        if (!visualAvailable)
        {
            if (entries.TryGetValue(key, out var unavailableEntry))
            {
                unavailableEntry.Rabbits.Clear();
                RemoveIfEmpty(key, unavailableEntry);
            }
            return Empty(
                ForageVisualProjectionRefreshStatus.ResourceUnavailable,
                ForageVisualProjectionReasonIds.CacheResourceUnavailable
            );
        }

        var rabbits = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var inspected = 0;
        foreach (var candidate in rabbitCandidates)
        {
            if (inspected >= MaximumRabbitCandidatesPerOwnerScreen)
                break;
            inspected++;
            if (candidate is not null)
                rabbits.Add(candidate);
        }

        var entry = GetOrReplaceEntry(owner);
        entry.Rabbits = rabbits;
        return new ForageVisualProjectionRefreshResult(
            ForageVisualProjectionRefreshStatus.Refreshed,
            ForageVisualProjectionReasonIds.CacheRefreshed,
            inspected,
            rabbits.Count,
            entry.GroundObjects.Count
        );
    }

    internal bool ContainsRabbit(
        HarmlessProjectionOwnerContext owner,
        object rabbitReference
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(rabbitReference);
        return entries.TryGetValue(Key(owner), out var entry)
            && entry.Owner.Matches(owner)
            && entry.Rabbits.Contains(rabbitReference);
    }

    internal ForageVisualProjectionRefreshResult RefreshGroundObjects(
        HarmlessProjectionOwnerContext owner,
        IEnumerable<ForageGroundProjectionCandidate> candidates,
        bool tierEligible,
        bool eventBlocked,
        bool rendererAvailable
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(candidates);
        var key = Key(owner);
        if (!tierEligible)
        {
            entries.Remove(key);
            return Empty(
                ForageVisualProjectionRefreshStatus.TierInactive,
                ForageVisualProjectionReasonIds.CacheTierInactive
            );
        }
        if (eventBlocked)
        {
            entries.Remove(key);
            return Empty(
                ForageVisualProjectionRefreshStatus.EventBlocked,
                ForageVisualProjectionReasonIds.CacheEventBlocked
            );
        }
        if (!rendererAvailable)
        {
            if (entries.TryGetValue(key, out var unavailableEntry))
            {
                unavailableEntry.GroundObjects.Clear();
                RemoveIfEmpty(key, unavailableEntry);
            }
            return Empty(
                ForageVisualProjectionRefreshStatus.ResourceUnavailable,
                ForageVisualProjectionReasonIds.CacheResourceUnavailable
            );
        }

        var groundObjects =
            new Dictionary<object, ForageGroundProjectionCandidate>(
            ReferenceEqualityComparer.Instance
        );
        var inspected = 0;
        foreach (var candidate in candidates)
        {
            if (inspected >= MaximumGroundObjectCandidatesPerOwnerScreen)
                break;
            inspected++;
            if (
                !ReferenceEquals(
                    candidate.SourceReference,
                    candidate.ReplacementReference
                )
                && candidate.SourceStack > 0
                && candidate.CatalogRevision > 0
                && !string.IsNullOrWhiteSpace(candidate.MappingId)
            )
            {
                groundObjects.TryAdd(candidate.SourceReference, candidate);
            }
        }

        var entry = GetOrReplaceEntry(owner);
        entry.GroundObjects = groundObjects;
        return new ForageVisualProjectionRefreshResult(
            ForageVisualProjectionRefreshStatus.Refreshed,
            ForageVisualProjectionReasonIds.CacheRefreshed,
            inspected,
            entry.Rabbits.Count,
            groundObjects.Count
        );
    }

    internal bool TryGetGroundReplacement(
        HarmlessProjectionOwnerContext owner,
        object sourceReference,
        int tileX,
        int tileY,
        int sourceStack,
        int sourceQuality,
        int catalogRevision,
        out object? replacementReference
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(sourceReference);
        replacementReference = null;
        if (
            entries.TryGetValue(Key(owner), out var entry)
            && entry.Owner.Matches(owner)
            && entry.GroundObjects.TryGetValue(sourceReference, out var candidate)
            && candidate.Matches(
                sourceReference,
                tileX,
                tileY,
                sourceStack,
                sourceQuality,
                catalogRevision
            )
        )
        {
            replacementReference = candidate.ReplacementReference;
            return true;
        }
        return false;
    }

    internal bool IsCurrentContext(HarmlessProjectionOwnerContext owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return entries.TryGetValue(Key(owner), out var entry)
            && entry.Owner.Matches(owner);
    }

    internal int CleanupOwner(string playerKey)
    {
        var keys = new List<ForageProjectionOwnerScreenKey>();
        foreach (var key in entries.Keys)
        {
            if (string.Equals(key.PlayerKey, playerKey, StringComparison.Ordinal))
                keys.Add(key);
        }
        foreach (var key in keys)
            entries.Remove(key);
        return keys.Count;
    }

    internal int CleanupOwnerScreen(string playerKey, int screenId)
    {
        return entries.Remove(new ForageProjectionOwnerScreenKey(playerKey, screenId))
            ? 1
            : 0;
    }

    internal int CleanupScreen(int screenId)
    {
        var keys = new List<ForageProjectionOwnerScreenKey>();
        foreach (var key in entries.Keys)
        {
            if (key.ScreenId == screenId)
                keys.Add(key);
        }
        foreach (var key in keys)
            entries.Remove(key);
        return keys.Count;
    }

    internal int CleanupInvalidScreens(Func<int, bool> isScreenValid)
    {
        ArgumentNullException.ThrowIfNull(isScreenValid);
        var keys = new List<ForageProjectionOwnerScreenKey>();
        foreach (var key in entries.Keys)
        {
            if (!isScreenValid(key.ScreenId))
                keys.Add(key);
        }
        foreach (var key in keys)
            entries.Remove(key);
        return keys.Count;
    }

    internal int CleanupAll()
    {
        var count = entries.Count;
        entries.Clear();
        return count;
    }

    private static ForageProjectionOwnerScreenKey Key(
        HarmlessProjectionOwnerContext owner
    )
    {
        return new ForageProjectionOwnerScreenKey(owner.PlayerKey, owner.ScreenId);
    }

    private OwnerEntry GetOrReplaceEntry(HarmlessProjectionOwnerContext owner)
    {
        var key = Key(owner);
        if (
            entries.TryGetValue(key, out var existing)
            && existing.Owner.Matches(owner)
        )
        {
            return existing;
        }

        var created = new OwnerEntry(owner);
        entries[key] = created;
        return created;
    }

    private void RemoveIfEmpty(
        ForageProjectionOwnerScreenKey key,
        OwnerEntry entry
    )
    {
        if (entry.Rabbits.Count == 0 && entry.GroundObjects.Count == 0)
            entries.Remove(key);
    }

    private static ForageVisualProjectionRefreshResult Empty(
        ForageVisualProjectionRefreshStatus status,
        string reason
    )
    {
        return new ForageVisualProjectionRefreshResult(status, reason, 0, 0, 0);
    }
}
