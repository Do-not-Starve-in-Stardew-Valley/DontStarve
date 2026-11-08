using Xunit;

namespace DontStarve.Tests.Music;

public sealed class ActivityMusicActionBridgeContractTests
{
    [Fact]
    public void FishingBridgeCoversTheSingleOwnerLifecycleAndCleanup()
    {
        var source = ReadContract("ActivityMusicActionBridge.cs");

        Assert.Contains("FishingRod.DoFunction", source, StringComparison.Ordinal);
        Assert.Contains("startMinigameEndFunction", source, StringComparison.Ordinal);
        Assert.Contains("pullFishFromWater", source, StringComparison.Ordinal);
        Assert.Contains("doneFishing", source, StringComparison.Ordinal);
        Assert.Contains("activeFishingRods", source, StringComparison.Ordinal);
        Assert.Contains("rod.isFishing || rod.isReeling || rod.pullingOutOfWater || rod.isNibbling", source, StringComparison.Ordinal);
        Assert.Contains("ForgetFishingOwner", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessfulWorkAndFarmingEntrancesExcludePreviewAndAutomaticPaths()
    {
        var source = ReadContract("ActivityMusicActionBridge.cs");

        Assert.Contains("__result", source, StringComparison.Ordinal);
        Assert.Contains("Pickaxe.DoFunction", source, StringComparison.Ordinal);
        Assert.Contains("PickaxeDoFunctionPrefix", source, StringComparison.Ordinal);
        Assert.Contains("tool.pickaxe.success", source, StringComparison.Ordinal);
        Assert.Contains("tool.pickaxe.hoe-dirt.success", source, StringComparison.Ordinal);
        Assert.Contains("MinutesUntilReady", source, StringComparison.Ordinal);
        Assert.Contains("shakeTimer", source, StringComparison.Ordinal);
        Assert.Contains("WateringCan.DoFunction", source, StringComparison.Ordinal);
        Assert.Contains("FruitTree.performToolAction", source, StringComparison.Ordinal);
        Assert.Contains("ResourceClump.performToolAction", source, StringComparison.Ordinal);
        Assert.Contains("GiantCrop.performToolAction", source, StringComparison.Ordinal);
        Assert.Contains("Bush.performToolAction", source, StringComparison.Ordinal);
        Assert.Contains("Flooring.performToolAction", source, StringComparison.Ordinal);
        Assert.Contains("__instance.health.Value - __state.Health", source, StringComparison.Ordinal);
        Assert.Contains("Hoe.DoFunction", source, StringComparison.Ordinal);
        Assert.Contains("HoeDirt.plant", source, StringComparison.Ordinal);
        Assert.Contains("farming.water.success", source, StringComparison.Ordinal);
        Assert.Contains("farming.harvest.success", source, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(who, Game1.player)", source, StringComparison.Ordinal);
        Assert.Contains("(O)805", source, StringComparison.Ordinal);
        Assert.Contains("__instance.isCookingRecipe", source, StringComparison.Ordinal);
        Assert.Contains("if (probe || who is null", source, StringComparison.Ordinal);
        Assert.Contains("SObject.autoLoadFrom is not null", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Crop.harvest", source, StringComparison.Ordinal);
        Assert.DoesNotContain("junimoHarvester", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessfulPlacementCoversEveryBigCraftablePlacementPathOnly()
    {
        var source = ReadContract("ActivityMusicActionBridge.cs");

        Assert.Contains("Patch(typeof(SObject), nameof(SObject.placementAction)", source, StringComparison.Ordinal);
        Assert.Contains("Patch(typeof(Torch), nameof(SObject.placementAction)", source, StringComparison.Ordinal);
        Assert.Contains("Patch(typeof(WoodChipper), nameof(SObject.placementAction)", source, StringComparison.Ordinal);
        Assert.Contains("__instance.QualifiedItemId.StartsWith(\"(BC)\", StringComparison.Ordinal)", source, StringComparison.Ordinal);
        Assert.Contains("__instance.IsTapper()", source, StringComparison.Ordinal);
        Assert.Contains("(O)805", source, StringComparison.Ordinal);
        Assert.Contains("__instance.GetMachineData() is not null", source, StringComparison.Ordinal);
        Assert.Contains("if (!__result || who is null)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("__instance.bigCraftable.Value", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CombatBridgeRequiresActualHealthChangeAndUsesTheActualFarmer()
    {
        var source = ReadContract("ActivityMusicActionBridge.cs");

        Assert.Contains("GameLocation.damageMonster", source, StringComparison.Ordinal);
        Assert.Contains("observation.Monster.Health < observation.Health", source, StringComparison.Ordinal);
        Assert.Contains("Farmer.takeDamage", source, StringComparison.Ordinal);
        Assert.Contains("__instance.health < __state", source, StringComparison.Ordinal);
        Assert.Contains("RecordCombat(who", source, StringComparison.Ordinal);
        Assert.Contains("RecordCombat(__instance", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShadowMonsterDamageDoesNotEnterTheActivityCombatMusicPath()
    {
        var source = ReadContract("ActivityMusicActionBridge.cs");
        var start = source.IndexOf(
            "private static void FarmerTakeDamagePostfix",
            StringComparison.Ordinal
        );
        var end = source.IndexOf("private static bool IsMill", start, StringComparison.Ordinal);

        Assert.True(start >= 0);
        Assert.True(end > start);
        var method = source[start..end];

        Assert.Contains("damager is not HostileShadowMonster", method, StringComparison.Ordinal);
        Assert.Contains("__instance.health < __state", method, StringComparison.Ordinal);
        Assert.Contains("RecordCombat(__instance", method, StringComparison.Ordinal);
    }

    [Fact]
    public void PlayerAttackingShadowMonsterDoesNotEnterTheActivityCombatMusicPath()
    {
        var source = ReadContract("ActivityMusicActionBridge.cs");
        var start = source.IndexOf(
            "private static void DamageMonsterPostfix",
            StringComparison.Ordinal
        );
        var end = source.IndexOf("private static void FarmerTakeDamagePrefix", start, StringComparison.Ordinal);

        Assert.True(start >= 0);
        Assert.True(end > start);
        var method = source[start..end];

        Assert.Contains("observation.Monster is not HostileShadowMonster", method, StringComparison.Ordinal);
        Assert.Contains("observation.Monster.Health < observation.Health", method, StringComparison.Ordinal);
        Assert.Contains("RecordCombat(who", method, StringComparison.Ordinal);
    }

    [Fact]
    public void BombAndTrainDamageAreExplicitCombatSourcesWhileOtherNullDamageStaysExcluded()
    {
        var source = ReadContract("ActivityMusicActionBridge.cs");
        var farmerDamageStart = source.IndexOf(
            "private static void FarmerTakeDamagePostfix",
            StringComparison.Ordinal
        );
        var bombBridgeStart = source.IndexOf("private static void BombExplosionPrefix", StringComparison.Ordinal);
        var trainBridgeStart = source.IndexOf("private static void TrainUpdatePrefix", StringComparison.Ordinal);
        var isMillStart = source.IndexOf("private static bool IsMill", trainBridgeStart, StringComparison.Ordinal);

        Assert.Contains("Patch(typeof(GameLocation), nameof(GameLocation.explode)", source, StringComparison.Ordinal);
        Assert.Contains("Patch(typeof(Train), nameof(Train.Update)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Patch(typeof(MineShaft)", source, StringComparison.Ordinal);
        Assert.True(farmerDamageStart >= 0);
        Assert.True(bombBridgeStart > farmerDamageStart);
        Assert.True(trainBridgeStart > bombBridgeStart);
        Assert.True(isMillStart > trainBridgeStart);
        Assert.Contains("damager is not null", source[farmerDamageStart..bombBridgeStart], StringComparison.Ordinal);
        Assert.Contains("damageFarmers && ReferenceEquals(player.currentLocation, __instance)", source[bombBridgeStart..trainBridgeStart], StringComparison.Ordinal);
        Assert.Contains("__state.Player.health < __state.Health", source[bombBridgeStart..trainBridgeStart], StringComparison.Ordinal);
        Assert.Contains("RecordCombat(__state.Player, \"combat.bomb.damage\")", source[bombBridgeStart..trainBridgeStart], StringComparison.Ordinal);
        Assert.Contains("location.Equals(Game1.currentLocation)", source[trainBridgeStart..isMillStart], StringComparison.Ordinal);
        Assert.Contains("__state.Player.health < __state.Health", source[trainBridgeStart..isMillStart], StringComparison.Ordinal);
        Assert.Contains("RecordCombat(__state.Player, \"combat.train.collision\")", source[trainBridgeStart..isMillStart], StringComparison.Ordinal);
    }

    [Fact]
    public void ActionBridgeDoesNotPutRuntimeLogicBackIntoMusicManager()
    {
        var source = ReadContract("ActivityMusicActionBridge.cs");

        Assert.DoesNotContain("MusicManager", source, StringComparison.Ordinal);
        Assert.Contains("phase 10 owns resources and playback", source, StringComparison.Ordinal);
    }

    private static string ReadContract(string fileName)
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", "ActivityMusic", fileName)
        );
    }
}
