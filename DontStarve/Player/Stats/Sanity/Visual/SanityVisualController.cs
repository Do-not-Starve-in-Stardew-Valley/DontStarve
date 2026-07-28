#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DontStarve.Player.Stats.Sanity.Visual;

[Flags]
internal enum SanityVisualLayerMask
{
    None = 0,
    LowSaturation = 1 << 0,
    ViewShake = 1 << 1,
    DangerBorder = 1 << 2,
    Grayscale = 1 << 3,
    All = LowSaturation | ViewShake | DangerBorder | Grayscale,
}

internal enum SanityVisualCapabilityStatus
{
    AvailableProduction,
}

internal readonly record struct SanityVisualCapability(
    string Capability,
    SanityVisualCapabilityStatus Status,
    string Reason
);

/// <summary>
/// Stage-03 production capability matrix. World effects use only the exact instance-owned final
/// composition adapter; the controller never falls back to process-global camera or viewport
/// mutation.
/// </summary>
internal static class SanityVisualCapabilityCatalog
{
    internal static readonly SanityVisualCapability LowSaturation = new(
        "visual.world.low-saturation",
        SanityVisualCapabilityStatus.AvailableProduction,
        "available-owner-local-world-composition"
    );

    internal static readonly SanityVisualCapability ViewShake = new(
        "visual.world.shake",
        SanityVisualCapabilityStatus.AvailableProduction,
        "available-owner-local-transform"
    );

    internal static readonly SanityVisualCapability DangerBorder = new(
        "visual.hud.danger-border",
        SanityVisualCapabilityStatus.AvailableProduction,
        "available-owner-local-hud-nine-slice"
    );

    internal static readonly SanityVisualCapability Grayscale = new(
        "visual.world.grayscale",
        SanityVisualCapabilityStatus.AvailableProduction,
        "available-owner-local-world-composition"
    );

    internal static readonly SanityVisualCapability IdlePresentation = new(
        "visual.farmer.idle-presentation",
        SanityVisualCapabilityStatus.AvailableProduction,
        "available-owner-local-non-passout-token"
    );

    private static readonly IReadOnlyList<SanityVisualLayerMask> FrozenLayerOrder =
        new ReadOnlyCollection<SanityVisualLayerMask>(
            new[]
            {
                SanityVisualLayerMask.LowSaturation,
                SanityVisualLayerMask.ViewShake,
                SanityVisualLayerMask.DangerBorder,
                SanityVisualLayerMask.Grayscale,
            }
        );

    internal static IReadOnlyList<SanityVisualLayerMask> LayerOrder =>
        FrozenLayerOrder;
}

internal readonly record struct SanityVisualOwnerKey(
    string PlayerKey,
    int ScreenId,
    string SessionId
);

internal readonly record struct SanityVisualObservation(
    SanityVisualOwnerKey Key,
    long Revision,
    double Current,
    double Maximum,
    IReadOnlyCollection<string> ActiveTierIds,
    bool EffectiveSanityOverrideActive,
    int ViewportWidth,
    int ViewportHeight
);

internal enum SanityVisualMutationStatus
{
    Applied,
    NoChange,
    IgnoredDuplicate,
    IgnoredStale,
    Invalid,
    Disabled,
    CapacityExceeded,
}

internal readonly record struct SanityVisualMutation(
    SanityVisualMutationStatus Status,
    string Reason
);

internal readonly record struct SanityVisualRectangle(
    int X,
    int Y,
    int Width,
    int Height
);

internal readonly record struct SanityVisualSlice(
    SanityVisualRectangle Source,
    SanityVisualRectangle Destination
);

/// <summary>
/// Immutable nine-slice geometry built only when an owner or UI viewport changes. Rendering can
/// consume the cached nine entries without allocating or recalculating rectangles per frame.
/// </summary>
internal readonly struct SanityNineSliceLayout
{
    private SanityNineSliceLayout(SanityVisualSlice[] slices)
    {
        Slices = slices;
    }

    internal IReadOnlyList<SanityVisualSlice> Slices { get; }

    internal SanityVisualSlice TopLeft => Slices[0];
    internal SanityVisualSlice TopCenter => Slices[1];
    internal SanityVisualSlice TopRight => Slices[2];
    internal SanityVisualSlice MiddleLeft => Slices[3];
    internal SanityVisualSlice MiddleCenter => Slices[4];
    internal SanityVisualSlice MiddleRight => Slices[5];
    internal SanityVisualSlice BottomLeft => Slices[6];
    internal SanityVisualSlice BottomCenter => Slices[7];
    internal SanityVisualSlice BottomRight => Slices[8];

    internal static bool TryCreate(
        int sourceWidth,
        int sourceHeight,
        int left,
        int top,
        int right,
        int bottom,
        int destinationWidth,
        int destinationHeight,
        out SanityNineSliceLayout layout,
        out string reason
    )
    {
        layout = default;
        if (
            sourceWidth <= 0
            || sourceHeight <= 0
            || destinationWidth <= 0
            || destinationHeight <= 0
            || left < 0
            || top < 0
            || right < 0
            || bottom < 0
            || left + right > sourceWidth
            || top + bottom > sourceHeight
        )
        {
            reason = "visual.nine-slice-contract-invalid";
            return false;
        }

        FitMargins(destinationWidth, left, right, out var destinationLeft, out var destinationRight);
        FitMargins(destinationHeight, top, bottom, out var destinationTop, out var destinationBottom);

        var sourceXs = new[] { 0, left, sourceWidth - right };
        var sourceYs = new[] { 0, top, sourceHeight - bottom };
        var sourceWidths = new[] { left, sourceWidth - left - right, right };
        var sourceHeights = new[] { top, sourceHeight - top - bottom, bottom };
        var destinationXs = new[] { 0, destinationLeft, destinationWidth - destinationRight };
        var destinationYs = new[] { 0, destinationTop, destinationHeight - destinationBottom };
        var destinationWidths = new[]
        {
            destinationLeft,
            destinationWidth - destinationLeft - destinationRight,
            destinationRight,
        };
        var destinationHeights = new[]
        {
            destinationTop,
            destinationHeight - destinationTop - destinationBottom,
            destinationBottom,
        };

        var slices = new SanityVisualSlice[9];
        var index = 0;
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                slices[index++] = new SanityVisualSlice(
                    new SanityVisualRectangle(
                        sourceXs[column],
                        sourceYs[row],
                        sourceWidths[column],
                        sourceHeights[row]
                    ),
                    new SanityVisualRectangle(
                        destinationXs[column],
                        destinationYs[row],
                        destinationWidths[column],
                        destinationHeights[row]
                    )
                );
            }
        }

        layout = new SanityNineSliceLayout(slices);
        reason = "visual.nine-slice-ready";
        return true;
    }

    private static void FitMargins(
        int total,
        int first,
        int second,
        out int fittedFirst,
        out int fittedSecond
    )
    {
        var sum = first + second;
        if (sum <= total)
        {
            fittedFirst = first;
            fittedSecond = second;
            return;
        }
        if (sum == 0)
        {
            fittedFirst = 0;
            fittedSecond = 0;
            return;
        }

        fittedFirst = (int)Math.Round(
            total * (first / (double)sum),
            MidpointRounding.AwayFromZero
        );
        fittedFirst = Math.Clamp(fittedFirst, 0, total);
        fittedSecond = total - fittedFirst;
    }
}

internal readonly record struct SanityVisualOwnerSnapshot(
    SanityVisualOwnerKey Key,
    long Revision,
    double Ratio,
    SanityVisualLayerMask RequestedLayers,
    SanityVisualLayerMask RenderableLayers,
    bool EffectiveSanityOverrideActive,
    int ViewportWidth,
    int ViewportHeight,
    SanityNineSliceLayout DangerBorderLayout,
    bool IdleEligible,
    SanityIdleSnapshot Idle
);

/// <summary>
/// Owner/screen/session/revision visual state. Tier IDs remain authoritative; this controller
/// maps them to presentation requests and exposes only capabilities proven safe for this stage.
/// </summary>
internal sealed class SanityVisualController
{
    internal const int MaximumOwners = 16;
    internal const int DangerBorderSourceWidth = 64;
    internal const int DangerBorderSourceHeight = 64;
    internal const int DangerBorderSliceLeft = 16;
    internal const int DangerBorderSliceTop = 16;
    internal const int DangerBorderSliceRight = 16;
    internal const int DangerBorderSliceBottom = 16;
    internal const float DangerBorderOpacity = 0.8f;

    private sealed class OwnerState
    {
        internal OwnerState(
            SanityVisualObservation observation,
            SanityVisualLayerMask requestedLayers,
            SanityNineSliceLayout layout
        )
        {
            Apply(observation, requestedLayers, layout);
        }

        internal SanityVisualOwnerKey Key { get; private set; }
        internal long Revision { get; private set; }
        internal double Current { get; private set; }
        internal double Maximum { get; private set; }
        internal SanityVisualLayerMask RequestedLayers { get; private set; }
        internal bool IdleEligible { get; private set; }
        internal bool EffectiveSanityOverrideActive { get; private set; }
        internal int ViewportWidth { get; private set; }
        internal int ViewportHeight { get; private set; }
        internal SanityNineSliceLayout DangerBorderLayout { get; private set; }
        internal SanityIdleDetector Idle { get; } = new();

        internal SanityVisualLayerMask RenderableLayers =>
            EffectiveSanityOverrideActive
                ? SanityVisualLayerMask.None
                : RequestedLayers;

        internal void Apply(
            SanityVisualObservation observation,
            SanityVisualLayerMask requestedLayers,
            SanityNineSliceLayout layout
        )
        {
            Key = observation.Key;
            Revision = observation.Revision;
            Current = observation.Current;
            Maximum = observation.Maximum;
            RequestedLayers = requestedLayers;
            IdleEligible = IsIdleEligible(observation);
            EffectiveSanityOverrideActive = observation.EffectiveSanityOverrideActive;
            ViewportWidth = observation.ViewportWidth;
            ViewportHeight = observation.ViewportHeight;
            DangerBorderLayout = layout;
            if (observation.EffectiveSanityOverrideActive)
                Idle.Reset("idle.effective-sanity-override");
            else if (!IdleEligible)
                Idle.Reset("idle.sanity-tier-ineligible");
        }

        internal void ApplyViewport(
            int width,
            int height,
            SanityNineSliceLayout layout
        )
        {
            ViewportWidth = width;
            ViewportHeight = height;
            DangerBorderLayout = layout;
        }

        internal void ApplyEffectiveSanityOverride(bool active)
        {
            EffectiveSanityOverrideActive = active;
            Idle.Reset(
                active
                    ? "idle.effective-sanity-override"
                    : "idle.effective-sanity-override-ended"
            );
        }

        internal bool Matches(
            SanityVisualObservation observation,
            SanityVisualLayerMask requestedLayers
        )
        {
            return Current.Equals(observation.Current)
                && Maximum.Equals(observation.Maximum)
                && RequestedLayers == requestedLayers
                && IdleEligible == IsIdleEligible(observation)
                && EffectiveSanityOverrideActive
                    == observation.EffectiveSanityOverrideActive
                && ViewportWidth == observation.ViewportWidth
                && ViewportHeight == observation.ViewportHeight;
        }

        internal SanityVisualOwnerSnapshot Snapshot()
        {
            return new SanityVisualOwnerSnapshot(
                Key,
                Revision,
                Current / Maximum,
                RequestedLayers,
                RenderableLayers,
                EffectiveSanityOverrideActive,
                ViewportWidth,
                ViewportHeight,
                DangerBorderLayout,
                IdleEligible,
                Idle.Snapshot
            );
        }

        private static bool IsIdleEligible(SanityVisualObservation observation)
        {
            if (observation.Current / observation.Maximum >= 0.5d)
                return false;
            foreach (var tierId in observation.ActiveTierIds)
            {
                if (
                    string.Equals(
                        tierId,
                        SanityTierIds.ShadowCreatures,
                        StringComparison.Ordinal
                    )
                )
                {
                    return true;
                }
            }
            return false;
        }
    }

    private readonly Dictionary<SanityVisualOwnerKey, OwnerState> owners = new();
    private bool enabled = true;

    internal int Count => owners.Count;
    internal bool IsEnabled => enabled;

    internal SanityVisualMutation SetEnabled(bool value)
    {
        if (enabled == value)
            return new SanityVisualMutation(SanityVisualMutationStatus.NoChange, "visual.enabled-unchanged");

        enabled = value;
        if (!enabled)
            owners.Clear();
        return new SanityVisualMutation(SanityVisualMutationStatus.Applied, enabled ? "visual.enabled" : "visual.disabled-cleared");
    }

    internal SanityVisualMutation Observe(SanityVisualObservation observation)
    {
        if (!enabled)
            return new SanityVisualMutation(SanityVisualMutationStatus.Disabled, "visual.system-disabled");
        if (!IsValid(observation))
            return new SanityVisualMutation(SanityVisualMutationStatus.Invalid, "visual.observation-invalid");

        var requestedLayers = BuildRequestedLayers(observation.ActiveTierIds);
        if (owners.TryGetValue(observation.Key, out var existing))
        {
            if (observation.Revision < existing.Revision)
                return new SanityVisualMutation(SanityVisualMutationStatus.IgnoredStale, "visual.revision-stale");
            if (observation.Revision == existing.Revision)
            {
                return existing.Matches(observation, requestedLayers)
                    ? new SanityVisualMutation(SanityVisualMutationStatus.IgnoredDuplicate, "visual.revision-duplicate")
                    : new SanityVisualMutation(SanityVisualMutationStatus.Invalid, "visual.revision-conflict");
            }

            if (!TryCreateDangerBorderLayout(observation.ViewportWidth, observation.ViewportHeight, out var updatedLayout))
                return new SanityVisualMutation(SanityVisualMutationStatus.Invalid, "visual.viewport-invalid");
            existing.Apply(observation, requestedLayers, updatedLayout);
            return new SanityVisualMutation(SanityVisualMutationStatus.Applied, "visual.observation-applied");
        }

        if (owners.Count >= MaximumOwners)
            return new SanityVisualMutation(SanityVisualMutationStatus.CapacityExceeded, "visual.owner-capacity-exceeded");
        if (!TryCreateDangerBorderLayout(observation.ViewportWidth, observation.ViewportHeight, out var layout))
            return new SanityVisualMutation(SanityVisualMutationStatus.Invalid, "visual.viewport-invalid");

        owners.Add(observation.Key, new OwnerState(observation, requestedLayers, layout));
        return new SanityVisualMutation(SanityVisualMutationStatus.Applied, "visual.observation-applied");
    }

    internal SanityVisualMutation UpdateViewport(
        SanityVisualOwnerKey key,
        int width,
        int height
    )
    {
        if (!enabled)
            return new SanityVisualMutation(SanityVisualMutationStatus.Disabled, "visual.system-disabled");
        if (!owners.TryGetValue(key, out var owner))
            return new SanityVisualMutation(SanityVisualMutationStatus.Invalid, "visual.owner-unavailable");
        if (owner.ViewportWidth == width && owner.ViewportHeight == height)
            return new SanityVisualMutation(SanityVisualMutationStatus.NoChange, "visual.viewport-unchanged");
        if (!TryCreateDangerBorderLayout(width, height, out var layout))
            return new SanityVisualMutation(SanityVisualMutationStatus.Invalid, "visual.viewport-invalid");

        owner.ApplyViewport(width, height, layout);
        return new SanityVisualMutation(SanityVisualMutationStatus.Applied, "visual.viewport-updated");
    }

    internal SanityIdleSnapshot ObserveIdle(
        SanityVisualOwnerKey key,
        TimeSpan elapsed,
        SanityIdleObservation observation
    )
    {
        if (!enabled)
            return new SanityIdleSnapshot(TimeSpan.Zero, false, "idle.visual-system-disabled");
        if (!owners.TryGetValue(key, out var owner))
            return new SanityIdleSnapshot(TimeSpan.Zero, false, "idle.owner-unavailable");
        if (owner.EffectiveSanityOverrideActive)
        {
            owner.Idle.Reset("idle.effective-sanity-override");
            return owner.Idle.Snapshot;
        }
        if (!owner.IdleEligible)
        {
            owner.Idle.Reset("idle.sanity-tier-ineligible");
            return owner.Idle.Snapshot;
        }
        return owner.Idle.Observe(elapsed, observation);
    }

    internal SanityIdleSnapshot ResetIdle(
        SanityVisualOwnerKey key,
        string reason
    )
    {
        if (!enabled)
            return new SanityIdleSnapshot(TimeSpan.Zero, false, "idle.visual-system-disabled");
        if (!owners.TryGetValue(key, out var owner))
            return new SanityIdleSnapshot(TimeSpan.Zero, false, "idle.owner-unavailable");

        owner.Idle.Reset(reason);
        return owner.Idle.Snapshot;
    }

    internal SanityVisualMutation UpdateEffectiveSanityOverride(
        SanityVisualOwnerKey key,
        bool active
    )
    {
        if (!enabled)
            return new SanityVisualMutation(SanityVisualMutationStatus.Disabled, "visual.system-disabled");
        if (!owners.TryGetValue(key, out var owner))
            return new SanityVisualMutation(SanityVisualMutationStatus.Invalid, "visual.owner-unavailable");
        if (owner.EffectiveSanityOverrideActive == active)
            return new SanityVisualMutation(SanityVisualMutationStatus.NoChange, "visual.effective-sanity-unchanged");

        owner.ApplyEffectiveSanityOverride(active);
        return new SanityVisualMutation(SanityVisualMutationStatus.Applied, active ? "visual.effective-sanity-suppressed" : "visual.effective-sanity-restored");
    }

    internal bool TryGetSnapshot(
        SanityVisualOwnerKey key,
        out SanityVisualOwnerSnapshot snapshot
    )
    {
        if (enabled && owners.TryGetValue(key, out var owner))
        {
            snapshot = owner.Snapshot();
            return true;
        }

        snapshot = default;
        return false;
    }

    internal int ClearOwner(string playerKey)
    {
        return RemoveWhere(key => string.Equals(key.PlayerKey, playerKey, StringComparison.Ordinal));
    }

    internal int ClearScreen(int screenId)
    {
        return RemoveWhere(key => key.ScreenId == screenId);
    }

    internal int ClearSession(string sessionId)
    {
        return RemoveWhere(key => string.Equals(key.SessionId, sessionId, StringComparison.Ordinal));
    }

    internal int ClearInvalidScreens(Func<int, bool> isScreenValid)
    {
        if (isScreenValid is null)
            throw new ArgumentNullException(nameof(isScreenValid));
        return RemoveWhere(key => !isScreenValid(key.ScreenId));
    }

    internal int ClearAll()
    {
        var count = owners.Count;
        owners.Clear();
        return count;
    }

    private int RemoveWhere(Func<SanityVisualOwnerKey, bool> predicate)
    {
        List<SanityVisualOwnerKey>? removals = null;
        foreach (var key in owners.Keys)
        {
            if (!predicate(key))
                continue;
            removals ??= new List<SanityVisualOwnerKey>();
            removals.Add(key);
        }

        if (removals is null)
            return 0;
        foreach (var key in removals)
            owners.Remove(key);
        return removals.Count;
    }

    private static bool IsValid(SanityVisualObservation observation)
    {
        return SanityPlayerKey.IsCanonical(observation.Key.PlayerKey)
            && observation.Key.ScreenId >= 0
            && SanityProtocol.IsValidSessionId(observation.Key.SessionId)
            && observation.Revision >= 0
            && double.IsFinite(observation.Current)
            && double.IsFinite(observation.Maximum)
            && observation.Current >= 0d
            && observation.Maximum > 0d
            && observation.Current <= observation.Maximum
            && observation.ActiveTierIds is not null
            && observation.ViewportWidth > 0
            && observation.ViewportHeight > 0;
    }

    private static SanityVisualLayerMask BuildRequestedLayers(
        IReadOnlyCollection<string> activeTierIds
    )
    {
        var result = SanityVisualLayerMask.None;
        foreach (var tierId in activeTierIds)
        {
            switch (tierId)
            {
                case SanityTierIds.DarkHand:
                    result |= SanityVisualLayerMask.LowSaturation;
                    break;
                case SanityTierIds.Eyes:
                    result |= SanityVisualLayerMask.ViewShake;
                    break;
                case SanityTierIds.Danger:
                    result |= SanityVisualLayerMask.DangerBorder;
                    break;
                case SanityTierIds.Terrorbeak:
                    result |= SanityVisualLayerMask.Grayscale;
                    break;
            }
        }
        return result;
    }

    private static bool TryCreateDangerBorderLayout(
        int width,
        int height,
        out SanityNineSliceLayout layout
    )
    {
        return SanityNineSliceLayout.TryCreate(
            DangerBorderSourceWidth,
            DangerBorderSourceHeight,
            DangerBorderSliceLeft,
            DangerBorderSliceTop,
            DangerBorderSliceRight,
            DangerBorderSliceBottom,
            width,
            height,
            out layout,
            out _
        );
    }
}
