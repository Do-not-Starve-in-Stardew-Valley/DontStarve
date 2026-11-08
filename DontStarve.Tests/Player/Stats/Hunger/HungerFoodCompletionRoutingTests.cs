using DontStarve.Player.Stats.Hunger;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Hunger;

public sealed class HungerFoodCompletionRoutingTests
{
    [Fact]
    public void Only_a_host_observing_a_completed_active_food_animation_may_settle()
    {
        Assert.True(
            HungerFoodCompletionPolicy.ShouldSettleCompletedFood(
                HungerAuthorityRole.Host,
                hungerEnabled: true,
                wasEatingBeforeCompletion: true,
                hasFood: true
            )
        );
        Assert.False(
            HungerFoodCompletionPolicy.ShouldSettleCompletedFood(
                HungerAuthorityRole.Client,
                hungerEnabled: true,
                wasEatingBeforeCompletion: true,
                hasFood: true
            )
        );
        Assert.False(
            HungerFoodCompletionPolicy.ShouldSettleCompletedFood(
                HungerAuthorityRole.Host,
                hungerEnabled: true,
                wasEatingBeforeCompletion: false,
                hasFood: true
            )
        );
        Assert.False(
            HungerFoodCompletionPolicy.ShouldSettleCompletedFood(
                HungerAuthorityRole.Host,
                hungerEnabled: false,
                wasEatingBeforeCompletion: true,
                hasFood: true
            )
        );
        Assert.False(
            HungerFoodCompletionPolicy.ShouldSettleCompletedFood(
                HungerAuthorityRole.Host,
                hungerEnabled: true,
                wasEatingBeforeCompletion: true,
                hasFood: false
            )
        );
    }

    [Fact]
    public void Missing_host_capability_keeps_legacy_requests_for_old_peers()
    {
        Assert.True(
            HungerFoodCompletionPolicy.ShouldUseLegacyFoodEdge(
                HungerAuthorityRole.Client,
                localPatchInstalled: true,
                hostAdvertisesCompletion: false
            )
        );
        Assert.False(
            HungerFoodCompletionPolicy.ShouldUseLegacyFoodEdge(
                HungerAuthorityRole.Client,
                localPatchInstalled: false,
                hostAdvertisesCompletion: true
            )
        );
        Assert.True(
            HungerFoodCompletionPolicy.ShouldAcceptLegacyRequest(
                HungerAuthorityRole.Host,
                localPatchInstalled: false
            )
        );
        Assert.False(
            HungerFoodCompletionPolicy.ShouldAcceptLegacyRequest(
                HungerAuthorityRole.Host,
                localPatchInstalled: true
            )
        );
        Assert.True(
            HungerFoodCompletionPolicy.ShouldSendLegacyRequest(
                HungerAuthorityRole.Client,
                hostAdvertisesCompletion: false
            )
        );
        Assert.False(
            HungerFoodCompletionPolicy.ShouldSendLegacyRequest(
                HungerAuthorityRole.Client,
                hostAdvertisesCompletion: true
            )
        );
        Assert.True(
            HungerFoodCompletionPolicy.UsesHostSettlement(
                HungerAuthorityRole.Host,
                localPatchInstalled: true,
                hostAdvertisesCompletion: false
            )
        );
        Assert.True(
            HungerFoodCompletionPolicy.UsesHostSettlement(
                HungerAuthorityRole.Client,
                localPatchInstalled: false,
                hostAdvertisesCompletion: true
            )
        );
        Assert.False(
            HungerFoodCompletionPolicy.UsesHostSettlement(
                HungerAuthorityRole.Client,
                localPatchInstalled: true,
                hostAdvertisesCompletion: false
            )
        );
        Assert.False(new HungerSnapshotMessage().HostFoodCompletionSupported);
    }

    [Fact]
    public void Host_completion_patch_replaces_host_food_polling_and_advertises_legacy_fallback()
    {
        var coordinator = Contract("SmapiHungerMultiplayerCoordinator.cs");
        var behavior = Contract("EatFood.cs");
        var protocol = Contract("HungerMultiplayerProtocol.cs");
        var patch = Contract("HungerFoodCompletionPatch.cs");
        var runtime = Contract("HungerRuntimeState.cs");

        Assert.Contains("HungerFoodCompletionPatch.TryInstall", coordinator, StringComparison.Ordinal);
        Assert.Contains("HostFoodCompletionSupported", coordinator, StringComparison.Ordinal);
        Assert.Contains("HostFoodCompletionSupported", protocol, StringComparison.Ordinal);
        Assert.Contains("nameof(Farmer.doneEating)", patch, StringComparison.Ordinal);
        Assert.Contains("__instance.isEating", patch, StringComparison.Ordinal);
        Assert.Contains("HungerExtensions.ApplyFood(__instance", patch, StringComparison.Ordinal);
        Assert.Contains("HungerFoodCompletionPolicy.ShouldUseLegacyFoodEdge", behavior, StringComparison.Ordinal);
        Assert.Contains("HungerFoodCompletionPatch.IsInstalled", behavior, StringComparison.Ordinal);
        Assert.Contains("message.HostFoodCompletionSupported", runtime, StringComparison.Ordinal);
    }

    private static string Contract(string fileName)
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", "OnlinePersistence", fileName)
        );
    }
}
