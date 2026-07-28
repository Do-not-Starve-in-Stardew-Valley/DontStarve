#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace DontStarve.Player.Stats.Sanity.Events;

internal enum SanityEffectiveOverlayReason
{
    RegularEvent,
    Festival,
    SanityTwoAmSpecial,
}

internal readonly record struct SanityEffectiveOverlayKey(
    string PlayerKey,
    int ScreenId,
    string SessionId
);

internal readonly record struct SanityEffectiveOverlaySnapshot(
    SanityEffectiveOverlayKey Key,
    SanityEffectiveOverlayReason Reason,
    string EventId,
    long Revision,
    double EffectiveRatio
);

internal enum SanityEffectiveOverlayMutationStatus
{
    Applied,
    NoChange,
    Rejected,
    Stale,
}

internal readonly record struct SanityEffectiveOverlayMutation(
    SanityEffectiveOverlayMutationStatus Status,
    string Reason,
    SanityEffectiveOverlaySnapshot? Snapshot
);

internal interface ISanityEffectiveSanityProvider
{
    bool TryGetEffectiveRatio(
        SanityEffectiveOverlayKey key,
        out double effectiveRatio,
        out SanityEffectiveOverlayReason reason
    );
}

/// <summary>
/// Owner/screen/session-scoped effect-layer overlay. It never owns or mutates base Sanity;
/// revision ordering prevents a late ordinary-event end from clearing a newer special flow.
/// </summary>
internal sealed class SanityEffectiveSanityOverlay : ISanityEffectiveSanityProvider
{
    internal const int MaximumOwners = 16;
    internal const double FullSanityRatio = 1d;

    private readonly Dictionary<
        SanityEffectiveOverlayKey,
        SanityEffectiveOverlaySnapshot
    > overlays = new();

    internal int Count => overlays.Count;

    internal SanityEffectiveOverlayMutation Begin(
        SanityEffectiveOverlayKey key,
        SanityEffectiveOverlayReason reason,
        string eventId,
        long revision
    )
    {
        if (!IsValidKey(key))
            return Rejected("effective-overlay-key-is-invalid");
        if (string.IsNullOrWhiteSpace(eventId) || eventId.Length > 128)
            return Rejected("effective-overlay-event-id-is-invalid");
        if (revision <= 0)
            return Rejected("effective-overlay-revision-must-be-positive");

        if (overlays.TryGetValue(key, out var current))
        {
            if (revision < current.Revision)
                return Stale(current, "effective-overlay-revision-is-stale");
            if (revision == current.Revision)
            {
                return current.Reason == reason
                        && string.Equals(
                            current.EventId,
                            eventId,
                            StringComparison.Ordinal
                        )
                    ? NoChange(current, "effective-overlay-duplicate-start")
                    : Rejected(
                        "effective-overlay-revision-conflicts-with-current-state",
                        current
                    );
            }

            if (
                current.Reason == SanityEffectiveOverlayReason.SanityTwoAmSpecial
                && reason != SanityEffectiveOverlayReason.SanityTwoAmSpecial
            )
            {
                return NoChange(
                    current,
                    "effective-overlay-special-flow-preserved"
                );
            }
        }
        else if (overlays.Count >= MaximumOwners)
        {
            return Rejected("effective-overlay-owner-cap-exceeded");
        }

        var snapshot = new SanityEffectiveOverlaySnapshot(
            key,
            reason,
            eventId,
            revision,
            FullSanityRatio
        );
        overlays[key] = snapshot;
        return new SanityEffectiveOverlayMutation(
            SanityEffectiveOverlayMutationStatus.Applied,
            "effective-overlay-started",
            snapshot
        );
    }

    internal SanityEffectiveOverlayMutation EndOrdinary(
        SanityEffectiveOverlayKey key,
        string eventId,
        long revision
    )
    {
        if (!overlays.TryGetValue(key, out var current))
            return NoChange(null, "effective-overlay-owner-is-not-active");
        if (current.Reason == SanityEffectiveOverlayReason.SanityTwoAmSpecial)
        {
            return NoChange(
                current,
                "effective-overlay-special-flow-preserved"
            );
        }
        if (revision <= current.Revision)
            return Stale(current, "effective-overlay-end-revision-is-stale");
        if (!string.Equals(current.EventId, eventId, StringComparison.Ordinal))
        {
            return Rejected(
                "effective-overlay-end-event-does-not-match",
                current
            );
        }

        overlays.Remove(key);
        return new SanityEffectiveOverlayMutation(
            SanityEffectiveOverlayMutationStatus.Applied,
            "effective-overlay-ended",
            current
        );
    }

    internal SanityEffectiveOverlayMutation EndSpecial(
        SanityEffectiveOverlayKey key,
        string eventId,
        long revision
    )
    {
        if (!overlays.TryGetValue(key, out var current))
            return NoChange(null, "effective-overlay-owner-is-not-active");
        if (current.Reason != SanityEffectiveOverlayReason.SanityTwoAmSpecial)
        {
            return Rejected(
                "effective-overlay-is-not-owned-by-special-flow",
                current
            );
        }
        if (revision <= current.Revision)
            return Stale(current, "effective-overlay-end-revision-is-stale");
        if (!string.Equals(current.EventId, eventId, StringComparison.Ordinal))
        {
            return Rejected(
                "effective-overlay-end-event-does-not-match",
                current
            );
        }

        overlays.Remove(key);
        return new SanityEffectiveOverlayMutation(
            SanityEffectiveOverlayMutationStatus.Applied,
            "effective-overlay-special-flow-ended",
            current
        );
    }

    internal IReadOnlyList<SanityEffectiveOverlaySnapshot> ClearScreen(int screenId)
    {
        return ClearWhere(snapshot => snapshot.Key.ScreenId == screenId);
    }

    internal IReadOnlyList<SanityEffectiveOverlaySnapshot> ClearSession(
        string sessionId
    )
    {
        return ClearWhere(snapshot =>
            string.Equals(snapshot.Key.SessionId, sessionId, StringComparison.Ordinal)
        );
    }

    internal IReadOnlyList<SanityEffectiveOverlaySnapshot> ClearAll()
    {
        return ClearWhere(_ => true);
    }

    internal IReadOnlyList<SanityEffectiveOverlaySnapshot> ClearInvalidScreens(
        Func<int, bool> isValidScreen
    )
    {
        ArgumentNullException.ThrowIfNull(isValidScreen);
        return ClearWhere(snapshot => !isValidScreen(snapshot.Key.ScreenId));
    }

    public bool TryGetEffectiveRatio(
        SanityEffectiveOverlayKey key,
        out double effectiveRatio,
        out SanityEffectiveOverlayReason reason
    )
    {
        if (overlays.TryGetValue(key, out var snapshot))
        {
            effectiveRatio = snapshot.EffectiveRatio;
            reason = snapshot.Reason;
            return true;
        }

        effectiveRatio = 0;
        reason = default;
        return false;
    }

    internal bool TryGetSnapshot(
        SanityEffectiveOverlayKey key,
        out SanityEffectiveOverlaySnapshot snapshot
    )
    {
        return overlays.TryGetValue(key, out snapshot);
    }

    private IReadOnlyList<SanityEffectiveOverlaySnapshot> ClearWhere(
        Func<SanityEffectiveOverlaySnapshot, bool> predicate
    )
    {
        var removed = overlays.Values.Where(predicate).ToArray();
        foreach (var snapshot in removed)
            overlays.Remove(snapshot.Key);
        return removed;
    }

    private static bool IsValidKey(SanityEffectiveOverlayKey key)
    {
        return SanityPlayerKey.IsCanonical(key.PlayerKey)
            && key.ScreenId >= 0
            && SanityProtocol.IsValidSessionId(key.SessionId);
    }

    private static SanityEffectiveOverlayMutation Rejected(
        string reason,
        SanityEffectiveOverlaySnapshot? snapshot = null
    )
    {
        return new SanityEffectiveOverlayMutation(
            SanityEffectiveOverlayMutationStatus.Rejected,
            reason,
            snapshot
        );
    }

    private static SanityEffectiveOverlayMutation Stale(
        SanityEffectiveOverlaySnapshot snapshot,
        string reason
    )
    {
        return new SanityEffectiveOverlayMutation(
            SanityEffectiveOverlayMutationStatus.Stale,
            reason,
            snapshot
        );
    }

    private static SanityEffectiveOverlayMutation NoChange(
        SanityEffectiveOverlaySnapshot? snapshot,
        string reason
    )
    {
        return new SanityEffectiveOverlayMutation(
            SanityEffectiveOverlayMutationStatus.NoChange,
            reason,
            snapshot
        );
    }
}
