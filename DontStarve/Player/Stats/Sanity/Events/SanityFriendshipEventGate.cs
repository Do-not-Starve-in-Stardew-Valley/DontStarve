#nullable enable

namespace DontStarve.Player.Stats.Sanity.Events;

internal enum SanityFriendshipEventGateDecision
{
    Allow,
    Block,
    FailOpen,
}

internal readonly record struct SanityFriendshipEventGateResult(
    SanityFriendshipEventGateDecision Decision,
    string Reason
);

internal static class SanityFriendshipEventGate
{
    internal static SanityFriendshipEventGateResult Evaluate(
        SanityEventClassification classification,
        double baseCurrent,
        double baseMaximum
    )
    {
        if (classification == SanityEventClassification.Unknown)
        {
            return new SanityFriendshipEventGateResult(
                SanityFriendshipEventGateDecision.FailOpen,
                "event-classification-is-unknown"
            );
        }
        if (classification == SanityEventClassification.NonFriendship)
        {
            return new SanityFriendshipEventGateResult(
                SanityFriendshipEventGateDecision.Allow,
                "event-is-not-a-friendship-event"
            );
        }
        if (
            !double.IsFinite(baseCurrent)
            || !double.IsFinite(baseMaximum)
            || baseMaximum <= 0
            || baseCurrent < 0
        )
        {
            return new SanityFriendshipEventGateResult(
                SanityFriendshipEventGateDecision.FailOpen,
                "base-sanity-is-unavailable"
            );
        }

        // Contract boundary aligned with the tier convention: the boundary belongs to the
        // more-insane side, so exactly 50% counts as low sanity and blocks friendship events
        // (ShadowCreatures tier is active at exactly 50% under the inclusive-entry contract).
        return baseCurrent / baseMaximum <= 0.5d
            ? new SanityFriendshipEventGateResult(
                SanityFriendshipEventGateDecision.Block,
                "base-sanity-is-below-fifty-percent"
            )
            : new SanityFriendshipEventGateResult(
                SanityFriendshipEventGateDecision.Allow,
                "base-sanity-is-above-fifty-percent"
            );
    }
}
