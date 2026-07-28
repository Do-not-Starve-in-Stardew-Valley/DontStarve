#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using DontStarve.Resource.Sanity;

namespace DontStarve.Player.Stats.Sanity.Illusions.Projection;

internal enum HarmlessProjectionBudgetLane
{
    OrdinaryPerOwnerPerSpecies,
}

internal enum HarmlessProjectionPlacementKind
{
    Ground,
    AnchorOnly,
}

internal enum HarmlessProjectionCleanupReason
{
    OwnerApproached,
    TierExited,
    LightRestored,
    ConfigDisabled,
    EventOverride,
    OwnerWarped,
    DayEnding,
    DayStartedRecovery,
    ReturnedTitle,
    OwnerInvalidated,
    ScreenInvalid,
    LocationInvalid,
    HardTtlExpired,
    ResourceInvalidated,
    DarkHandReturned,
    ConversionRequested,
    WorldCleanup,
}

internal static class HarmlessProjectionCleanupReasonIds
{
    internal const string OwnerApproached = "owner-approached";
    internal const string TierExited = "tier-exited";
    internal const string LightRestored = "light-restored";
    internal const string ConfigDisabled = "config-disabled";
    internal const string EventOverride = "event-override";
    internal const string OwnerWarped = "owner-warped";
    internal const string DayEnding = "day-ending";
    internal const string DayStartedRecovery = "day-started-recovery";
    internal const string ReturnedTitle = "returned-title";
    internal const string OwnerInvalidated = "owner-invalidated";
    internal const string ScreenInvalid = "screen-invalid";
    internal const string LocationInvalid = "location-invalid";
    internal const string HardTtlExpired = "hard-ttl-expired";
    internal const string ResourceInvalidated = "resource-invalidated";
    internal const string DarkHandReturned = "dark-hand-returned";
    internal const string ConversionRequested = "conversion-requested";
    internal const string WorldCleanup = "world-cleanup";

    private static readonly IReadOnlyList<string> FrozenIds = Array.AsReadOnly(
        new[]
        {
            OwnerApproached,
            TierExited,
            LightRestored,
            ConfigDisabled,
            EventOverride,
            OwnerWarped,
            DayEnding,
            DayStartedRecovery,
            ReturnedTitle,
            OwnerInvalidated,
            ScreenInvalid,
            LocationInvalid,
            HardTtlExpired,
            ResourceInvalidated,
            DarkHandReturned,
            ConversionRequested,
            WorldCleanup,
        }
    );

    internal static IReadOnlyList<string> All => FrozenIds;

    internal static string GetId(HarmlessProjectionCleanupReason reason)
    {
        return reason switch
        {
            HarmlessProjectionCleanupReason.OwnerApproached => OwnerApproached,
            HarmlessProjectionCleanupReason.TierExited => TierExited,
            HarmlessProjectionCleanupReason.LightRestored => LightRestored,
            HarmlessProjectionCleanupReason.ConfigDisabled => ConfigDisabled,
            HarmlessProjectionCleanupReason.EventOverride => EventOverride,
            HarmlessProjectionCleanupReason.OwnerWarped => OwnerWarped,
            HarmlessProjectionCleanupReason.DayEnding => DayEnding,
            HarmlessProjectionCleanupReason.DayStartedRecovery => DayStartedRecovery,
            HarmlessProjectionCleanupReason.ReturnedTitle => ReturnedTitle,
            HarmlessProjectionCleanupReason.OwnerInvalidated => OwnerInvalidated,
            HarmlessProjectionCleanupReason.ScreenInvalid => ScreenInvalid,
            HarmlessProjectionCleanupReason.LocationInvalid => LocationInvalid,
            HarmlessProjectionCleanupReason.HardTtlExpired => HardTtlExpired,
            HarmlessProjectionCleanupReason.ResourceInvalidated => ResourceInvalidated,
            HarmlessProjectionCleanupReason.DarkHandReturned => DarkHandReturned,
            HarmlessProjectionCleanupReason.ConversionRequested => ConversionRequested,
            HarmlessProjectionCleanupReason.WorldCleanup => WorldCleanup,
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
        };
    }
}

internal sealed class HarmlessProjectionOwnerContext
{
    internal HarmlessProjectionOwnerContext(
        string playerKey,
        int screenId,
        object locationReference,
        string locationNameOrUniqueName
    )
    {
        if (!SanityPlayerKey.IsCanonical(playerKey))
        {
            throw new ArgumentException(
                "Projection owners require a canonical Sanity player key.",
                nameof(playerKey)
            );
        }
        if (screenId < 0)
            throw new ArgumentOutOfRangeException(nameof(screenId));
        ArgumentNullException.ThrowIfNull(locationReference);
        if (string.IsNullOrWhiteSpace(locationNameOrUniqueName))
        {
            throw new ArgumentException(
                "A location identity snapshot is required.",
                nameof(locationNameOrUniqueName)
            );
        }

        PlayerKey = playerKey;
        ScreenId = screenId;
        LocationReference = locationReference;
        LocationNameOrUniqueName = locationNameOrUniqueName;
    }

    internal string PlayerKey { get; }

    internal int ScreenId { get; }

    internal object LocationReference { get; }

    internal string LocationNameOrUniqueName { get; }

    internal bool Matches(HarmlessProjectionOwnerContext? other)
    {
        return other is not null
            && string.Equals(PlayerKey, other.PlayerKey, StringComparison.Ordinal)
            && ScreenId == other.ScreenId
            && ReferenceEquals(LocationReference, other.LocationReference)
            && string.Equals(
                LocationNameOrUniqueName,
                other.LocationNameOrUniqueName,
                StringComparison.Ordinal
            );
    }
}

internal readonly record struct HarmlessProjectionWorldPoint(double X, double Y)
{
    internal bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
}

/// <summary>
/// A bounded animation-state contract consumed by the stage 03 resource facade. Species own the
/// timing and IDs; the host only validates and retains one loader-owned frame-zero result per state.
/// </summary>
internal sealed class HarmlessProjectionVisualStatePolicy
{
    internal HarmlessProjectionVisualStatePolicy(
        string stateId,
        string visualSlotId,
        string textureSlotId,
        int frameCount,
        int frameDurationMilliseconds,
        bool loop,
        SanityResourcePoint expectedPivotSourcePx,
        double expectedDrawScale
    )
    {
        StateId = RequireId(stateId, nameof(stateId));
        VisualSlotId = RequireId(visualSlotId, nameof(visualSlotId));
        TextureSlotId = RequireId(textureSlotId, nameof(textureSlotId));
        if (frameCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        if (frameDurationMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameDurationMilliseconds));
        if (expectedDrawScale <= 0d || !double.IsFinite(expectedDrawScale))
            throw new ArgumentOutOfRangeException(nameof(expectedDrawScale));

        FrameCount = frameCount;
        FrameDurationMilliseconds = frameDurationMilliseconds;
        Loop = loop;
        ExpectedPivotSourcePx = expectedPivotSourcePx;
        ExpectedDrawScale = expectedDrawScale;
    }

    internal string StateId { get; }

    internal string VisualSlotId { get; }

    internal string TextureSlotId { get; }

    internal int FrameCount { get; }

    internal int FrameDurationMilliseconds { get; }

    internal bool Loop { get; }

    internal SanityResourcePoint ExpectedPivotSourcePx { get; }

    internal double ExpectedDrawScale { get; }

    internal bool TryValidateResource(
        SanitySlotResourceResult? resource,
        out string reason
    )
    {
        if (resource is null)
        {
            reason = "spawn.visual-resource-result-null";
            return false;
        }
        if (!resource.Success)
        {
            reason = resource.Diagnostic.Code;
            return false;
        }

        var preview = resource.VisualPreview;
        if (
            preview is null
            || resource.PhysicalResource?.Kind != SanityPhysicalResourceKind.Texture
            || preview.Kind != SanityVisualPreviewKind.AnimationFrame
            || !preview.OwnerLocalOnly
            || preview.FrameIndex != 0
            || preview.FrameCount != FrameCount
            || preview.SourceRectangle.Width <= 0
            || preview.SourceRectangle.Height <= 0
            || preview.PivotSourcePx != ExpectedPivotSourcePx
            || Math.Abs(preview.DrawScale - ExpectedDrawScale) > 0.0001d
            || !string.Equals(
                preview.RequestedSlotId,
                VisualSlotId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                preview.TextureSlotId,
                TextureSlotId,
                StringComparison.Ordinal
            )
        )
        {
            reason = "spawn.visual-state-contract-invalid";
            return false;
        }

        reason = "spawn.visual-state-contract-valid";
        return true;
    }

    private static string RequireId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A stable ID is required.", parameterName);
        return value;
    }
}

/// <summary>
/// 通用 policy 只描述 owner-local cadence/cap/TTL/位置与资源身份。普通投影没有
/// intensity、shadow permit 或共享预算字段；两影怪适配仍由 04-08 唯一建立。
/// </summary>
internal sealed class HarmlessProjectionPolicy : IHarmlessProjectionPlacementPolicy
{
    internal const int MaximumCandidateAttempts = 16;

    internal HarmlessProjectionPolicy(
        string speciesId,
        string tierId,
        string visualSlotId,
        string initialStateId,
        int minimumDistanceTiles,
        int maximumDistanceTiles,
        int attemptIntervalMinutes,
        int activeCap,
        int hardTtlMinutes,
        int candidateAttemptLimit,
        HarmlessProjectionPlacementKind placementKind,
        bool clearOnTierExit,
        HarmlessProjectionBudgetLane budgetLane =
            HarmlessProjectionBudgetLane.OrdinaryPerOwnerPerSpecies,
        string? visualProfileId = null,
        IReadOnlyList<HarmlessProjectionVisualStatePolicy>? visualStates = null
    )
    {
        SpeciesId = RequireId(speciesId, nameof(speciesId));
        TierId = RequireId(tierId, nameof(tierId));
        VisualSlotId = RequireId(visualSlotId, nameof(visualSlotId));
        InitialStateId = RequireId(initialStateId, nameof(initialStateId));
        if (minimumDistanceTiles < 0 || maximumDistanceTiles < minimumDistanceTiles)
            throw new ArgumentOutOfRangeException(nameof(minimumDistanceTiles));
        if (attemptIntervalMinutes <= 0)
            throw new ArgumentOutOfRangeException(nameof(attemptIntervalMinutes));
        // 阶段 02 的 active key 是 owner/screen/location/species；普通 lane 的冻结 cap 只能为 1。
        if (activeCap != 1)
            throw new ArgumentOutOfRangeException(nameof(activeCap));
        if (hardTtlMinutes <= 0)
            throw new ArgumentOutOfRangeException(nameof(hardTtlMinutes));
        if (candidateAttemptLimit <= 0 || candidateAttemptLimit > MaximumCandidateAttempts)
            throw new ArgumentOutOfRangeException(nameof(candidateAttemptLimit));

        MinimumDistanceTiles = minimumDistanceTiles;
        MaximumDistanceTiles = maximumDistanceTiles;
        AttemptIntervalMinutes = attemptIntervalMinutes;
        ActiveCap = activeCap;
        HardTtlMinutes = hardTtlMinutes;
        CandidateAttemptLimit = candidateAttemptLimit;
        PlacementKind = placementKind;
        ClearOnTierExit = clearOnTierExit;
        BudgetLane = budgetLane;

        var copiedStates = visualStates is null
            ? Array.Empty<HarmlessProjectionVisualStatePolicy>()
            : new HarmlessProjectionVisualStatePolicy[visualStates.Count];
        var sawInitialState = copiedStates.Length == 0;
        var stateIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < copiedStates.Length; index++)
        {
            var state = visualStates![index]
                ?? throw new ArgumentException(
                    "Visual states cannot contain null entries.",
                    nameof(visualStates)
                );
            if (!stateIds.Add(state.StateId))
            {
                throw new ArgumentException(
                    "Visual state IDs must be unique within a species policy.",
                    nameof(visualStates)
                );
            }
            if (string.Equals(state.StateId, InitialStateId, StringComparison.Ordinal))
            {
                sawInitialState = string.Equals(
                    state.VisualSlotId,
                    VisualSlotId,
                    StringComparison.Ordinal
                );
            }
            copiedStates[index] = state;
        }
        if (!sawInitialState)
        {
            throw new ArgumentException(
                "The initial state must map to the policy's initial visual slot.",
                nameof(visualStates)
            );
        }
        if (copiedStates.Length > 0 && string.IsNullOrWhiteSpace(visualProfileId))
        {
            throw new ArgumentException(
                "Animated projection policies require a stable profile ID.",
                nameof(visualProfileId)
            );
        }

        VisualProfileId = string.IsNullOrWhiteSpace(visualProfileId)
            ? null
            : visualProfileId;
        VisualStates = Array.AsReadOnly(copiedStates);
    }

    internal string SpeciesId { get; }

    internal string TierId { get; }

    internal string VisualSlotId { get; }

    internal string InitialStateId { get; }

    public int MinimumDistanceTiles { get; }

    public int MaximumDistanceTiles { get; }

    internal int AttemptIntervalMinutes { get; }

    internal int ActiveCap { get; }

    internal int HardTtlMinutes { get; }

    public int CandidateAttemptLimit { get; }

    public HarmlessProjectionPlacementKind PlacementKind { get; }

    internal bool ClearOnTierExit { get; }

    internal HarmlessProjectionBudgetLane BudgetLane { get; }

    internal string? VisualProfileId { get; }

    internal IReadOnlyList<HarmlessProjectionVisualStatePolicy> VisualStates { get; }

    internal bool TryGetVisualState(
        string stateId,
        out HarmlessProjectionVisualStatePolicy? state
    )
    {
        foreach (var candidate in VisualStates)
        {
            if (!string.Equals(candidate.StateId, stateId, StringComparison.Ordinal))
                continue;
            state = candidate;
            return true;
        }

        state = null;
        return false;
    }

    private static string RequireId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A stable ID is required.", parameterName);
        return value;
    }
}

internal sealed class HarmlessProjectionInstance
{
    private readonly SanitySlotResourceResult? fallbackVisualResource;
    private readonly IReadOnlyDictionary<string, SanitySlotResourceResult>
        visualResourcesByState;
    private long stateElapsedMilliseconds;

    internal HarmlessProjectionInstance(
        HarmlessProjectionOwnerContext owner,
        HarmlessProjectionPolicy policy,
        HarmlessProjectionWorldPoint spawnWorldPixel,
        long spawnedAtMinute,
        long expiresAtMinute,
        string stateId,
        SanitySlotResourceResult? visualResource = null,
        IReadOnlyDictionary<string, SanitySlotResourceResult>? visualStateResources = null
    )
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        if (!spawnWorldPixel.IsFinite)
            throw new ArgumentOutOfRangeException(nameof(spawnWorldPixel));
        if (expiresAtMinute <= spawnedAtMinute)
            throw new ArgumentOutOfRangeException(nameof(expiresAtMinute));
        if (string.IsNullOrWhiteSpace(stateId))
            throw new ArgumentException("A stable projection state ID is required.", nameof(stateId));

        SpawnWorldPixel = spawnWorldPixel;
        OriginWorldPixel = spawnWorldPixel;
        SpawnedAtMinute = spawnedAtMinute;
        ExpiresAtMinute = expiresAtMinute;
        StateId = stateId;
        fallbackVisualResource = visualResource;

        var copiedResources = new Dictionary<string, SanitySlotResourceResult>(
            StringComparer.Ordinal
        );
        if (visualStateResources is not null)
        {
            foreach (var pair in visualStateResources)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                {
                    throw new ArgumentException(
                        "Visual state resources require stable IDs and non-null results.",
                        nameof(visualStateResources)
                    );
                }
                copiedResources.Add(pair.Key, pair.Value);
            }
        }
        visualResourcesByState = new ReadOnlyDictionary<
            string,
            SanitySlotResourceResult
        >(copiedResources);
    }

    internal HarmlessProjectionOwnerContext Owner { get; }

    internal HarmlessProjectionPolicy Policy { get; }

    internal string SpeciesId => Policy.SpeciesId;

    internal HarmlessProjectionWorldPoint SpawnWorldPixel { get; private set; }

    internal HarmlessProjectionWorldPoint OriginWorldPixel { get; }

    internal long SpawnedAtMinute { get; private set; }

    internal long ExpiresAtMinute { get; private set; }

    internal string VisualSlotId => Policy.VisualSlotId;

    internal string StateId { get; private set; }

    internal SanitySlotResourceResult? VisualResource =>
        visualResourcesByState.TryGetValue(StateId, out var stateResource)
            ? stateResource
            : fallbackVisualResource;

    internal IReadOnlyDictionary<string, SanitySlotResourceResult>
        VisualResourcesByState => visualResourcesByState;

    internal int CurrentFrameIndex { get; private set; }

    /// <summary>
    /// Owner-local presentation hints only. They are never serialized, broadcast, or written to a
    /// Stardew world collection; moving projections retain their immutable spawn origin separately.
    /// </summary>
    internal int FacingX { get; private set; } = 1;

    internal string BehaviorLabel { get; private set; } = string.Empty;

    internal HarmlessProjectionCleanupReason? PendingExitReason { get; private set; }

    internal HarmlessProjectionCleanupReason? CleanupReason { get; private set; }

    internal string CleanupReasonId => CleanupReason.HasValue
        ? HarmlessProjectionCleanupReasonIds.GetId(CleanupReason.Value)
        : string.Empty;

    internal bool IsCleanedUp => CleanupReason.HasValue;

    internal bool TryMoveTo(HarmlessProjectionWorldPoint worldPixel)
    {
        if (IsCleanedUp || !worldPixel.IsFinite)
            return false;

        var deltaX = worldPixel.X - SpawnWorldPixel.X;
        if (deltaX < 0d)
            FacingX = -1;
        else if (deltaX > 0d)
            FacingX = 1;
        SpawnWorldPixel = worldPixel;
        return true;
    }

    /// <summary>
    /// Updates only the mod-private horizontal presentation hint. Static projections can face a
    /// confirmed target without moving their anchor or writing any Stardew world state.
    /// </summary>
    internal bool TryFaceToward(HarmlessProjectionWorldPoint worldPixel)
    {
        if (IsCleanedUp || !worldPixel.IsFinite)
            return false;

        var deltaX = worldPixel.X - SpawnWorldPixel.X;
        if (deltaX < 0d)
            FacingX = -1;
        else if (deltaX > 0d)
            FacingX = 1;
        return true;
    }

    internal void SetBehaviorLabel(string label)
    {
        BehaviorLabel = label ?? string.Empty;
    }

    internal void TransitionState(string stateId)
    {
        if (string.IsNullOrWhiteSpace(stateId))
            throw new ArgumentException("A stable projection state ID is required.", nameof(stateId));
        if (string.Equals(StateId, stateId, StringComparison.Ordinal))
            return;
        StateId = stateId;
        CurrentFrameIndex = 0;
        stateElapsedMilliseconds = 0;
    }

    internal bool TryBeginExit(
        string exitStateId,
        HarmlessProjectionCleanupReason reason
    )
    {
        if (IsCleanedUp || PendingExitReason.HasValue)
            return false;

        PendingExitReason = reason;
        TransitionState(exitStateId);
        return true;
    }

    /// <summary>
    /// Advances only integer counters against the policy metadata. It never loads a frame or
    /// allocates a texture, so UpdateTicked and RenderedWorld share the spawn-time resource set.
    /// </summary>
    internal bool AdvanceVisualState(int elapsedMilliseconds)
    {
        if (elapsedMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedMilliseconds));
        if (
            !Policy.TryGetVisualState(StateId, out var visualState)
            || visualState is null
        )
        {
            CurrentFrameIndex = 0;
            return false;
        }

        var cycleMilliseconds = checked(
            (long)visualState.FrameCount * visualState.FrameDurationMilliseconds
        );
        var elapsed = Math.Min(
            long.MaxValue - stateElapsedMilliseconds,
            (long)elapsedMilliseconds
        );
        stateElapsedMilliseconds += elapsed;
        if (visualState.Loop)
        {
            stateElapsedMilliseconds %= cycleMilliseconds;
            CurrentFrameIndex = (int)(
                stateElapsedMilliseconds / visualState.FrameDurationMilliseconds
            );
            return false;
        }

        if (stateElapsedMilliseconds >= cycleMilliseconds)
        {
            stateElapsedMilliseconds = cycleMilliseconds;
            CurrentFrameIndex = visualState.FrameCount - 1;
            return true;
        }

        CurrentFrameIndex = (int)(
            stateElapsedMilliseconds / visualState.FrameDurationMilliseconds
        );
        return false;
    }

    internal bool TryMarkCleaned(HarmlessProjectionCleanupReason reason)
    {
        if (CleanupReason.HasValue)
            return false;
        CleanupReason = reason;
        return true;
    }

    internal bool TryRebaseTime(long delta)
    {
        try
        {
            var rebasedSpawnedAt = checked(SpawnedAtMinute + delta);
            var rebasedExpiresAt = checked(ExpiresAtMinute + delta);
            SpawnedAtMinute = rebasedSpawnedAt;
            ExpiresAtMinute = rebasedExpiresAt;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}

internal sealed class HarmlessProjectionSpawnRequest
{
    internal HarmlessProjectionSpawnRequest(
        HarmlessProjectionOwnerContext owner,
        HarmlessProjectionPolicy policy,
        HarmlessProjectionWorldPoint ownerStandingWorldPixel,
        long gameMinute
    )
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        if (!ownerStandingWorldPixel.IsFinite)
            throw new ArgumentOutOfRangeException(nameof(ownerStandingWorldPixel));

        OwnerStandingWorldPixel = ownerStandingWorldPixel;
        GameMinute = gameMinute;
    }

    internal HarmlessProjectionOwnerContext Owner { get; }

    internal HarmlessProjectionPolicy Policy { get; }

    internal HarmlessProjectionWorldPoint OwnerStandingWorldPixel { get; }

    internal long GameMinute { get; }
}

internal enum HarmlessProjectionSpawnStatus
{
    Spawned,
    Failed,
}

internal sealed class HarmlessProjectionSpawnResult
{
    private HarmlessProjectionSpawnResult(
        HarmlessProjectionSpawnStatus status,
        string reason,
        HarmlessProjectionInstance? instance
    )
    {
        Status = status;
        Reason = string.IsNullOrWhiteSpace(reason)
            ? throw new ArgumentException("A stable spawn reason is required.", nameof(reason))
            : reason;
        Instance = instance;
    }

    internal HarmlessProjectionSpawnStatus Status { get; }

    internal string Reason { get; }

    internal HarmlessProjectionInstance? Instance { get; }

    internal bool Success => Status == HarmlessProjectionSpawnStatus.Spawned;

    internal static HarmlessProjectionSpawnResult Spawned(
        HarmlessProjectionInstance instance,
        string reason = "spawn.succeeded"
    )
    {
        ArgumentNullException.ThrowIfNull(instance);
        return new HarmlessProjectionSpawnResult(
            HarmlessProjectionSpawnStatus.Spawned,
            reason,
            instance
        );
    }

    internal static HarmlessProjectionSpawnResult Failed(string reason)
    {
        return new HarmlessProjectionSpawnResult(
            HarmlessProjectionSpawnStatus.Failed,
            reason,
            null
        );
    }
}

internal interface IHarmlessProjectionSpawnFactory
{
    HarmlessProjectionSpawnResult TrySpawn(HarmlessProjectionSpawnRequest request);
}

/// <summary>
/// Optional species-local preflight. It runs before resource loading and point selection and can
/// only allow or reject an owner-local spawn; it has no world mutation or hostile-budget surface.
/// </summary>
internal interface IHarmlessProjectionSpawnGate
{
    bool CanSpawn(HarmlessProjectionSpawnRequest request, out string reason);
}

/// <summary>
/// 玩法 consumer 只借用阶段 03 loader 所有的结果，不创建第二 loader/cache，也不 dispose。
/// </summary>
internal interface IHarmlessProjectionResourceProvider
{
    SanitySlotResourceResult LoadVisualSlot(string slotId, int frameIndex = 0);
}

internal enum HarmlessProjectionExitResolution
{
    Cleanup,
    RetainForSpeciesTransition,
}

internal interface IHarmlessProjectionExitHook
{
    HarmlessProjectionExitResolution Resolve(
        HarmlessProjectionInstance instance,
        HarmlessProjectionCleanupReason requestedReason
    );
}

internal enum HarmlessProjectionSpeciesUpdateStatus
{
    IgnoredObserver,
    Active,
    Transitioning,
    CleanupRequested,
    Unavailable,
}

internal readonly record struct HarmlessProjectionSpeciesUpdateResult(
    HarmlessProjectionSpeciesUpdateStatus Status,
    HarmlessProjectionCleanupReason? CleanupReason,
    string Reason
)
{
    internal bool ShouldCleanup =>
        Status == HarmlessProjectionSpeciesUpdateStatus.CleanupRequested
        && CleanupReason.HasValue;
}

/// <summary>
/// Species logic receives only one exact observer context and a point. There is intentionally no
/// shared farmer list, attack input, world collection, or hostile budget surface here.
/// </summary>
internal interface IHarmlessProjectionSpeciesBehavior : IHarmlessProjectionExitHook
{
    HarmlessProjectionSpeciesUpdateResult Update(
        HarmlessProjectionInstance instance,
        HarmlessProjectionOwnerContext observer,
        HarmlessProjectionWorldPoint observerStandingWorldPixel,
        int elapsedMilliseconds
    );
}
