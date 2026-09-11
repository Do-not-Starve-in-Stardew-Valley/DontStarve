#nullable enable

namespace DontStarve.Player.Stats.Sanity.Visual;

internal enum SanityMinigameVisualContext
{
    None,
    Fishing,
    Other,
}

/// <summary>
/// Central visual boundary for minigames. Fishing is deliberately checked before the generic
/// minigame/menu branches because the ice-fishing event and festival FishingGame may own a
/// BobberBar at the same time. This class only classifies the current presentation; it never
/// changes game state.
/// </summary>
internal static class SanityMinigameVisualClassifier
{
    internal static SanityMinigameVisualContext Resolve(
        bool hasBobberBar,
        bool isExhibitionFishing,
        bool isIceFishing,
        bool hasOtherMinigame
    )
    {
        if (hasBobberBar || isExhibitionFishing || isIceFishing)
            return SanityMinigameVisualContext.Fishing;

        return hasOtherMinigame
            ? SanityMinigameVisualContext.Other
            : SanityMinigameVisualContext.None;
    }

    /// <summary>
    /// Only non-fishing minigames pause the owner's low-Sanity playback claim. Ordinary menus
    /// remain transparent to audio. The Fishing whitelist deliberately wins over BobberBar's
    /// menu status so fishing keeps the ambience and whispers requested by the existing contract.
    /// </summary>
    internal static bool ShouldPauseLocalAudio(
        SanityMinigameVisualContext context
    )
    {
        return context == SanityMinigameVisualContext.Other;
    }

}
