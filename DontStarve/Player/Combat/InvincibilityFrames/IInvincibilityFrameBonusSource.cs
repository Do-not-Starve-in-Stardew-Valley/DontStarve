#nullable enable

using StardewValley;

namespace DontStarve.Player.Combat.InvincibilityFrames;

internal interface IInvincibilityFrameBonusSource
{
    string Id { get; }

    bool TryGetBonusMilliseconds(
        Farmer target,
        out InvincibilityFrameBonusContribution contribution,
        out string reason
    );
}
