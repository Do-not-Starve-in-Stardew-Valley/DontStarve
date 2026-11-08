#nullable enable

namespace DontStarve.Player.Stats.Hunger;

internal static class HungerFoodCompletionPolicy
{
    internal static bool UsesHostSettlement(
        HungerAuthorityRole role,
        bool localPatchInstalled,
        bool hostAdvertisesCompletion
    )
    {
        return role switch
        {
            HungerAuthorityRole.Host => localPatchInstalled,
            HungerAuthorityRole.Client => hostAdvertisesCompletion,
            _ => false,
        };
    }

    internal static bool ShouldSettleCompletedFood(
        HungerAuthorityRole role,
        bool hungerEnabled,
        bool wasEatingBeforeCompletion,
        bool hasFood
    )
    {
        return role == HungerAuthorityRole.Host
            && hungerEnabled
            && wasEatingBeforeCompletion
            && hasFood;
    }

    internal static bool ShouldUseLegacyFoodEdge(
        HungerAuthorityRole role,
        bool localPatchInstalled,
        bool hostAdvertisesCompletion
    )
    {
        return role switch
        {
            HungerAuthorityRole.Host => !localPatchInstalled,
            HungerAuthorityRole.Client => !hostAdvertisesCompletion,
            _ => false,
        };
    }

    internal static bool ShouldSendLegacyRequest(
        HungerAuthorityRole role,
        bool hostAdvertisesCompletion
    )
    {
        return role == HungerAuthorityRole.Client && !hostAdvertisesCompletion;
    }

    internal static bool ShouldAcceptLegacyRequest(
        HungerAuthorityRole role,
        bool localPatchInstalled
    )
    {
        return role == HungerAuthorityRole.Host && !localPatchInstalled;
    }
}
