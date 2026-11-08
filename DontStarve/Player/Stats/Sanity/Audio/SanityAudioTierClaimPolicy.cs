#nullable enable

using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity.Audio;

/// <summary>
/// Converts the tier state machine's authoritative nested IDs into audio and music requests.
/// The 50% music request is deliberately independent from the 15% danger one-shot tier.
/// </summary>
internal static class SanityAudioTierClaimPolicy
{
    internal static SanityAudioTierClaimFlags Evaluate(
        IReadOnlyList<string> activeTierIds
    )
    {
        var ambience = false;
        var whispers = false;
        var danger = false;
        var musicSuppression = false;

        foreach (var tierId in activeTierIds)
        {
            switch (tierId)
            {
                case SanityTierIds.ShadowCreatures:
                    ambience = true;
                    // <50% owns the continuous ambience lane and vanilla game-music suppression;
                    // an exact boundary is retained only when the authoritative tier was active.
                    musicSuppression = true;
                    break;
                case SanityTierIds.Whispers:
                    whispers = true;
                    break;
                case SanityTierIds.Danger:
                    // <15% enters this lane; the state machine exits only above 17.5%.
                    danger = true;
                    break;
            }
        }

        return new SanityAudioTierClaimFlags(
            ambience,
            whispers,
            danger,
            musicSuppression
        );
    }
}

internal readonly record struct SanityAudioTierClaimFlags(
    bool AmbienceActive,
    bool WhispersActive,
    bool DangerActive,
    bool MusicSuppressionRequested
);
