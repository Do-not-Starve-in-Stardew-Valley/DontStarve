#nullable enable

using System;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Minigames;

namespace DontStarve.Player.Stats.Sanity.Visual;

/// <summary>
/// Resolves the current Stardew presentation into the pure visual boundary. Keep the runtime type
/// checks here so the deterministic classifier can be linked into tests without game assemblies.
/// </summary>
internal static class SanityMinigameVisualRuntimeClassifier
{
    internal const string ExhibitionFishingFestivalId = "fall16";
    internal const string IceFishingSequenceId = "iceFishing";

    /// <summary>
    /// World composition is allowed only for the two non-Other presentation states. Keep this
    /// explicit so a future enum value cannot accidentally become a renderable fail-open path.
    /// </summary>
    internal static bool IsWorldCompositionAllowed(SanityMinigameVisualContext context)
    {
        return context is SanityMinigameVisualContext.None
            or SanityMinigameVisualContext.Fishing;
    }

    internal static SanityMinigameVisualContext ResolveCurrent()
    {
        var currentEvent = Game1.CurrentEvent;
        var currentMinigame = Game1.currentMinigame;
        var activeMenu = Game1.activeClickableMenu;
        var isExhibitionFishing =
            currentMinigame is FishingGame
            && currentEvent is not null
            && currentEvent.isSpecificFestival(ExhibitionFishingFestivalId);
        var isIceFishing = string.Equals(
            currentEvent?.playerControlSequenceID,
            IceFishingSequenceId,
            StringComparison.Ordinal
        );
        // Do not infer this from a generic festival/event check. The only FishingGame
        // whitelist entry is the fall16 exhibition flow; the only event-sequence entry is
        // iceFishing. Every other currentMinigame remains a visual Other context.
        var hasOtherMinigame =
            (currentMinigame is not null
                && !isExhibitionFishing
                && !isIceFishing)
            || activeMenu is StrengthGame
            || activeMenu is WheelSpinGame
            || string.Equals(
                currentEvent?.playerControlSequenceID,
                "eggHunt",
                StringComparison.Ordinal
            );

        return SanityMinigameVisualClassifier.Resolve(
            activeMenu is BobberBar,
            isExhibitionFishing,
            isIceFishing,
            hasOtherMinigame
        );
    }
}
