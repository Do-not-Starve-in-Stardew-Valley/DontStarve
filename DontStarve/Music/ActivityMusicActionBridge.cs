#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Buildings;
using StardewValley.Inventories;
using StardewValley.Monsters;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using TileLocation = xTile.Dimensions.Location;
using TileRectangle = xTile.Dimensions.Rectangle;
using SObject = StardewValley.Object;

namespace DontStarve.Music;

/// <summary>
/// Observes Stardew's successful action boundaries and submits facts to the owner-local activity
/// service. This class deliberately contains no audio code: phase 10 owns resources and playback.
/// </summary>
internal static class ActivityMusicActionBridge
{
    private sealed class HoeDirtToolObservation
    {
        internal HoeDirtToolObservation(bool wasWatered, bool hadCrop)
        {
            WasWatered = wasWatered;
            HadCrop = hadCrop;
        }

        internal bool WasWatered { get; }

        internal bool HadCrop { get; }
    }

    private sealed class WateringCanObservation
    {
        internal WateringCanObservation(int waterLeft, bool canRefill)
        {
            WaterLeft = waterLeft;
            CanRefill = canRefill;
        }

        internal int WaterLeft { get; }

        internal bool CanRefill { get; }
    }

    private sealed class TreeToolObservation
    {
        internal TreeToolObservation(float health)
        {
            Health = health;
        }

        internal float Health { get; }
    }

    private sealed class ResourceClumpToolObservation
    {
        internal ResourceClumpToolObservation(float health)
        {
            Health = health;
        }

        internal float Health { get; }
    }

    private sealed class BushToolObservation
    {
        internal BushToolObservation(float health)
        {
            Health = health;
        }

        internal float Health { get; }
    }

    private sealed class HoeObservation
    {
        internal HoeObservation(GameLocation location)
        {
            ExistingHoeDirtTiles = new HashSet<Vector2>();
            foreach (var pair in location.terrainFeatures.Pairs)
            {
                if (pair.Value is HoeDirt)
                    ExistingHoeDirtTiles.Add(pair.Key);
            }
        }

        internal HashSet<Vector2> ExistingHoeDirtTiles { get; }
    }

    private sealed class PickaxeObjectObservation
    {
        internal PickaxeObjectObservation(Vector2 tile, SObject instance)
        {
            Tile = tile;
            Instance = instance;
            MinutesUntilReady = instance.MinutesUntilReady;
            ShakeTimer = instance.shakeTimer;
            ReadyForHarvest = instance.readyForHarvest.Value;
            HeldObject = instance.heldObject.Value;
        }

        internal Vector2 Tile { get; }

        internal SObject Instance { get; }

        internal int MinutesUntilReady { get; }

        internal int ShakeTimer { get; }

        internal bool ReadyForHarvest { get; }

        internal SObject? HeldObject { get; }

        internal bool IsBoulder => Instance.Name.Contains("Boulder", StringComparison.Ordinal);
    }

    private readonly struct PlayerHealthObservation
    {
        internal PlayerHealthObservation(Farmer player, GameLocation location, int health)
        {
            Player = player;
            Location = location;
            Health = health;
        }

        internal Farmer Player { get; }

        internal GameLocation Location { get; }

        internal int Health { get; }
    }

    private sealed class PickaxeObservation
    {
        internal PickaxeObservation(GameLocation location, int x, int y, Farmer? who)
        {
            Location = location;
            Objects = new List<PickaxeObjectObservation>();

            var tile = new Vector2(x / 64, y / 64);
            AddObject(location, tile);
            if (location.Objects.ContainsKey(tile))
                return;

            if (who is null)
                return;

            if (who.FacingDirection == 0 || who.FacingDirection == 2)
            {
                AddObject(location, new Vector2((x - 8) / 64, y / 64));
                AddObject(location, new Vector2((x + 8) / 64, y / 64));
            }
            else
            {
                AddObject(location, new Vector2(x / 64, (y + 8) / 64));
                AddObject(location, new Vector2(x / 64, (y - 8) / 64));
            }
        }

        internal GameLocation Location { get; }

        internal List<PickaxeObjectObservation> Objects { get; }

        internal bool HasEffectiveHit(Pickaxe tool)
        {
            foreach (var observation in Objects)
            {
                if (!Location.Objects.TryGetValue(observation.Tile, out var current)
                    || !ReferenceEquals(current, observation.Instance))
                {
                    return true;
                }

                if (current.MinutesUntilReady != observation.MinutesUntilReady
                    || current.shakeTimer != observation.ShakeTimer
                    || current.readyForHarvest.Value != observation.ReadyForHarvest
                    || !ReferenceEquals(current.heldObject.Value, observation.HeldObject))
                {
                    return true;
                }

                // Pickaxe.DoFunction handles boulders itself and only updates a private hit
                // counter plus the visual shake state. The upgrade gate mirrors Stardew's branch;
                // an underpowered pickaxe only shows the rejection dialogue and is not work.
                if (observation.IsBoulder && tool.UpgradeLevel >= 2)
                    return true;
            }

            return false;
        }

        private void AddObject(GameLocation location, Vector2 tile)
        {
            foreach (var observation in Objects)
            {
                if (observation.Tile == tile)
                    return;
            }

            if (location.Objects.TryGetValue(tile, out var instance))
                Objects.Add(new PickaxeObjectObservation(tile, instance));
        }
    }

    private sealed class MachineDropInObservation
    {
        internal MachineDropInObservation(SObject machine)
        {
            IsMachine = machine.GetMachineData() is not null;
            MinutesUntilReady = machine.MinutesUntilReady;
            LastInputItem = machine.lastInputItem.Value;
            HeldObject = machine.heldObject.Value;
        }

        internal bool IsMachine { get; }

        internal int MinutesUntilReady { get; }

        internal Item? LastInputItem { get; }

        internal SObject? HeldObject { get; }
    }

    private sealed class MachineHarvestObservation
    {
        internal MachineHarvestObservation(SObject machine)
        {
            ReadyForHarvest = machine.readyForHarvest.Value;
        }

        internal bool ReadyForHarvest { get; }
    }

    private sealed class GroundPickupObservation
    {
        internal GroundPickupObservation(Vector2 tile, bool hasPickup, HoeDirt? hoeDirt)
        {
            Tile = tile;
            HasPickup = hasPickup;
            HoeDirt = hoeDirt;
        }

        internal Vector2 Tile { get; }

        internal bool HasPickup { get; }

        internal HoeDirt? HoeDirt { get; }
    }

    private sealed class AnimalToolObservation
    {
        internal AnimalToolObservation(FarmAnimal? animal)
        {
            Animal = animal;
            Produce = animal?.currentProduce.Value;
        }

        internal FarmAnimal? Animal { get; }

        internal string? Produce { get; }
    }

    private sealed class MillChestObservation
    {
        internal MillChestObservation(
            Building building,
            string type,
            Chest? chest,
            Farmer? farmer
        )
        {
            Building = building;
            Type = type;
            Chest = chest;
            ChestStack = CountStack(chest?.Items);
            ActiveItemStack = farmer?.ActiveObject?.Stack;
        }

        internal Building Building { get; }

        internal string Type { get; }

        internal Chest? Chest { get; }

        internal int ChestStack { get; }

        internal int? ActiveItemStack { get; }
    }

    private sealed class DamageObservation
    {
        internal List<(Monster Monster, int Health)> Monsters { get; } = new();
    }

    private static readonly List<MethodInfo> patchedMethods = new();
    private static readonly HashSet<FishingRod> activeFishingRods = new();
    private static Harmony? harmony;
    private static IMonitor? monitor;
    private static bool enabled;

    internal static void Enable(IModHelper? helper, IMonitor? modMonitor, string? patchOwnerId)
    {
        if (enabled || helper is null || modMonitor is null || string.IsNullOrWhiteSpace(patchOwnerId))
            return;

        monitor = modMonitor;
        harmony = new Harmony(patchOwnerId + ".ActivityMusic");
        enabled = true;

        Patch(typeof(FishingRod), nameof(FishingRod.DoFunction), new[]
        {
            typeof(GameLocation), typeof(int), typeof(int), typeof(int), typeof(Farmer)
        }, postfix: nameof(FishingRodDoFunctionPostfix));
        Patch(typeof(FishingRod), nameof(FishingRod.startMinigameEndFunction), new[]
        {
            typeof(Item)
        }, postfix: nameof(FishingRodStagePostfix));
        Patch(typeof(FishingRod), nameof(FishingRod.pullFishFromWater), new[]
        {
            typeof(string), typeof(int), typeof(int), typeof(int), typeof(bool), typeof(bool),
            typeof(bool), typeof(string), typeof(bool), typeof(int)
        }, postfix: nameof(FishingRodStagePostfix));
        Patch(typeof(FishingRod), nameof(FishingRod.doneFishing), new[]
        {
            typeof(Farmer), typeof(bool)
        }, postfix: nameof(FishingRodDonePostfix));

        Patch(typeof(GameLocation), nameof(GameLocation.performToolAction), new[]
        {
            typeof(Tool), typeof(int), typeof(int)
        }, postfix: nameof(GameLocationToolActionPostfix));
        Patch(typeof(Pickaxe), nameof(Pickaxe.DoFunction), new[]
        {
            typeof(GameLocation), typeof(int), typeof(int), typeof(int), typeof(Farmer)
        }, prefix: nameof(PickaxeDoFunctionPrefix), postfix: nameof(PickaxeDoFunctionPostfix));
        Patch(typeof(SObject), nameof(SObject.performToolAction), new[]
        {
            typeof(Tool)
        }, postfix: nameof(ObjectToolActionPostfix));
        Patch(typeof(Tree), nameof(Tree.performToolAction), new[]
        {
            typeof(Tool), typeof(int), typeof(Vector2)
        }, prefix: nameof(TreeToolActionPrefix), postfix: nameof(TreeToolActionPostfix));
        Patch(typeof(FruitTree), nameof(FruitTree.performToolAction), new[]
        {
            typeof(Tool), typeof(int), typeof(Vector2)
        }, prefix: nameof(FruitTreeToolActionPrefix), postfix: nameof(FruitTreeToolActionPostfix));
        Patch(typeof(ResourceClump), nameof(ResourceClump.performToolAction), new[]
        {
            typeof(Tool), typeof(int), typeof(Vector2)
        }, prefix: nameof(ResourceClumpToolActionPrefix), postfix: nameof(ResourceClumpToolActionPostfix));
        Patch(typeof(GiantCrop), nameof(GiantCrop.performToolAction), new[]
        {
            typeof(Tool), typeof(int), typeof(Vector2)
        }, prefix: nameof(ResourceClumpToolActionPrefix), postfix: nameof(ResourceClumpToolActionPostfix));
        Patch(typeof(Bush), nameof(Bush.performToolAction), new[]
        {
            typeof(Tool), typeof(int), typeof(Vector2)
        }, prefix: nameof(BushToolActionPrefix), postfix: nameof(BushToolActionPostfix));
        Patch(typeof(Flooring), nameof(Flooring.performToolAction), new[]
        {
            typeof(Tool), typeof(int), typeof(Vector2)
        }, postfix: nameof(FlooringToolActionPostfix));
        Patch(typeof(Hoe), nameof(Hoe.DoFunction), new[]
        {
            typeof(GameLocation), typeof(int), typeof(int), typeof(int), typeof(Farmer)
        }, prefix: nameof(HoePrefix), postfix: nameof(HoePostfix));
        Patch(typeof(HoeDirt), nameof(HoeDirt.performToolAction), new[]
        {
            typeof(Tool), typeof(int), typeof(Vector2)
        }, prefix: nameof(HoeDirtToolActionPrefix), postfix: nameof(HoeDirtToolActionPostfix));
        Patch(typeof(WateringCan), nameof(WateringCan.DoFunction), new[]
        {
            typeof(GameLocation), typeof(int), typeof(int), typeof(int), typeof(Farmer)
        }, prefix: nameof(WateringCanPrefix), postfix: nameof(WateringCanPostfix));
        Patch(typeof(HoeDirt), nameof(HoeDirt.plant), new[]
        {
            typeof(string), typeof(Farmer), typeof(bool)
        }, postfix: nameof(HoeDirtPlantPostfix));
        Patch(typeof(SObject), nameof(SObject.placementAction), new[]
        {
            typeof(GameLocation), typeof(int), typeof(int), typeof(Farmer)
        }, postfix: nameof(ObjectPlacementPostfix));
        // These BC overrides place successfully without calling Object.placementAction.
        Patch(typeof(Torch), nameof(SObject.placementAction), new[]
        {
            typeof(GameLocation), typeof(int), typeof(int), typeof(Farmer)
        }, postfix: nameof(ObjectPlacementPostfix));
        Patch(typeof(WoodChipper), nameof(SObject.placementAction), new[]
        {
            typeof(GameLocation), typeof(int), typeof(int), typeof(Farmer)
        }, postfix: nameof(ObjectPlacementPostfix));
        Patch(typeof(SObject), nameof(SObject.performObjectDropInAction), new[]
        {
            typeof(Item), typeof(bool), typeof(Farmer), typeof(bool)
        }, prefix: nameof(ObjectDropInPrefix), postfix: nameof(ObjectDropInPostfix));
        Patch(typeof(SObject), "CheckForActionOnMachine", new[]
        {
            typeof(Farmer), typeof(bool)
        }, prefix: nameof(MachineHarvestPrefix), postfix: nameof(MachineHarvestPostfix));
        Patch(typeof(GameLocation), nameof(GameLocation.checkAction), new[]
        {
            typeof(TileLocation), typeof(TileRectangle), typeof(Farmer)
        }, prefix: nameof(GroundPickupPrefix), postfix: nameof(GroundPickupPostfix));
        Patch(typeof(MilkPail), nameof(MilkPail.DoFunction), new[]
        {
            typeof(GameLocation), typeof(int), typeof(int), typeof(int), typeof(Farmer)
        }, prefix: nameof(MilkPailPrefix), postfix: nameof(MilkPailPostfix));
        Patch(typeof(Shears), nameof(Shears.DoFunction), new[]
        {
            typeof(GameLocation), typeof(int), typeof(int), typeof(int), typeof(Farmer)
        }, prefix: nameof(ShearsPrefix), postfix: nameof(ShearsPostfix));
        Patch(typeof(Pan), nameof(Pan.DoFunction), new[]
        {
            typeof(GameLocation), typeof(int), typeof(int), typeof(int), typeof(Farmer)
        }, prefix: nameof(PanPrefix), postfix: nameof(PanPostfix));
        Patch(typeof(CraftingRecipe), nameof(CraftingRecipe.consumeIngredients), null,
            postfix: nameof(CraftingPostfix));
        Patch(typeof(Building), nameof(Building.PerformBuildingChestAction), new[]
        {
            typeof(string), typeof(Farmer)
        }, prefix: nameof(MillChestPrefix), postfix: nameof(MillChestPostfix));

        Patch(typeof(GameLocation), nameof(GameLocation.damageMonster), new[]
        {
            typeof(Rectangle), typeof(int), typeof(int), typeof(bool), typeof(float), typeof(int),
            typeof(float), typeof(float), typeof(bool), typeof(Farmer), typeof(bool)
        }, prefix: nameof(DamageMonsterPrefix), postfix: nameof(DamageMonsterPostfix));
        // These combat hazards pass a null Monster damager; observe their explicit source
        // boundaries instead of broadening the generic Farmer.takeDamage hook to all null damage.
        Patch(typeof(GameLocation), nameof(GameLocation.explode), new[]
        {
            typeof(Vector2), typeof(int), typeof(Farmer), typeof(bool), typeof(int), typeof(bool)
        }, prefix: nameof(BombExplosionPrefix), postfix: nameof(BombExplosionPostfix));
        Patch(typeof(Train), nameof(Train.Update), new[]
        {
            typeof(GameTime), typeof(GameLocation)
        }, prefix: nameof(TrainUpdatePrefix), postfix: nameof(TrainUpdatePostfix));
        Patch(typeof(Farmer), nameof(Farmer.takeDamage), new[]
        {
            typeof(int), typeof(bool), typeof(Monster)
        }, prefix: nameof(FarmerTakeDamagePrefix), postfix: nameof(FarmerTakeDamagePostfix));
    }

    internal static void Disable()
    {
        if (harmony is not null)
        {
            foreach (var method in patchedMethods)
                harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
        }

        patchedMethods.Clear();
        harmony = null;
        enabled = false;
        monitor = null;
        activeFishingRods.Clear();
    }

    internal static void ClearActiveFishing()
    {
        activeFishingRods.Clear();
    }

    internal static void ResetForTests()
    {
        ClearActiveFishing();
    }

    internal static void ForgetFishingOwner(long ownerId)
    {
        activeFishingRods.RemoveWhere(
            rod => rod.lastUser?.UniqueMultiplayerID == ownerId
        );
    }

    internal static void TickActiveFishing()
    {
        if (!enabled || activeFishingRods.Count == 0)
            return;

        var finished = new List<FishingRod>();
        foreach (var rod in activeFishingRods)
        {
            if (rod.isFishing || rod.isReeling || rod.pullingOutOfWater || rod.isNibbling)
            {
                ActivityMusicService.RecordWork(rod.lastUser, "fishing.lifecycle");
            }
            else
            {
                finished.Add(rod);
            }
        }

        foreach (var rod in finished)
            activeFishingRods.Remove(rod);
    }

    private static void Patch(
        Type targetType,
        string methodName,
        Type[]? parameterTypes,
        string? prefix = null,
        string? postfix = null
    )
    {
        if (harmony is null)
            return;

        var target = parameterTypes is null
            ? AccessTools.Method(targetType, methodName)
            : AccessTools.Method(targetType, methodName, parameterTypes);
        if (target is null)
        {
            monitor?.Log(
                $"Activity music action bridge target unavailable: {targetType.FullName}.{methodName}.",
                LogLevel.Warn
            );
            return;
        }

        var prefixMethod = prefix is null
            ? null
            : AccessTools.Method(typeof(ActivityMusicActionBridge), prefix);
        var postfixMethod = postfix is null
            ? null
            : AccessTools.Method(typeof(ActivityMusicActionBridge), postfix);
        harmony.Patch(
            target,
            prefix: prefixMethod is null ? null : new HarmonyMethod(prefixMethod),
            postfix: postfixMethod is null ? null : new HarmonyMethod(postfixMethod)
        );
        patchedMethods.Add(target);
    }

    private static void FishingRodDoFunctionPostfix(FishingRod __instance, Farmer? who)
    {
        if (!__instance.isFishing)
            return;

        activeFishingRods.Add(__instance);
        ActivityMusicService.RecordWork(who ?? __instance.lastUser, "fishing.cast.success");
    }

    private static void FishingRodStagePostfix(FishingRod __instance)
    {
        if (__instance.lastUser is null)
            return;

        activeFishingRods.Add(__instance);
        ActivityMusicService.RecordWork(__instance.lastUser, "fishing.lifecycle");
    }

    private static void FishingRodDonePostfix(FishingRod __instance)
    {
        activeFishingRods.Remove(__instance);
    }

    private static void PickaxeDoFunctionPrefix(
        GameLocation location,
        int x,
        int y,
        Farmer? who,
        out PickaxeObservation __state
    )
    {
        __state = new PickaxeObservation(location, x, y, who);
    }

    private static void PickaxeDoFunctionPostfix(
        Pickaxe __instance,
        Farmer? who,
        PickaxeObservation __state
    )
    {
        if (who is null || !__state.HasEffectiveHit(__instance))
            return;

        ActivityMusicService.RecordWork(who, __state.Location, "tool.pickaxe.success");
    }

    private static void GameLocationToolActionPostfix(
        GameLocation __instance,
        Tool? t,
        bool __result
    )
    {
        if (!__result || t is null || t is MeleeWeapon)
            return;

        ActivityMusicService.RecordWork(
            t!.getLastFarmerToUse(),
            __instance,
            "tool.location.success"
        );
    }

    private static void ObjectToolActionPostfix(SObject __instance, Tool? t, bool __result)
    {
        if (!__result || t is null || t is MeleeWeapon)
            return;

        ActivityMusicService.RecordWork(
            t.getLastFarmerToUse(),
            __instance.Location,
            "tool.object.success"
        );
    }

    private static void TreeToolActionPrefix(Tree __instance, Tool? t, out TreeToolObservation __state)
    {
        __state = new TreeToolObservation(__instance.health.Value);
    }

    private static void TreeToolActionPostfix(
        Tree __instance,
        Tool? t,
        bool __result,
        TreeToolObservation __state
    )
    {
        if (t is null || t is MeleeWeapon)
            return;

        if (__result || Math.Abs(__instance.health.Value - __state.Health) > float.Epsilon)
        {
            ActivityMusicService.RecordWork(
                t.getLastFarmerToUse(),
                __instance.Location,
                "tool.tree.success"
            );
        }
    }

    private static void FruitTreeToolActionPrefix(
        FruitTree __instance,
        out TreeToolObservation __state
    )
    {
        __state = new TreeToolObservation(__instance.health.Value);
    }

    private static void FruitTreeToolActionPostfix(
        FruitTree __instance,
        Tool? t,
        bool __result,
        TreeToolObservation __state
    )
    {
        if (t is null || t is MeleeWeapon)
            return;

        if (__result || Math.Abs(__instance.health.Value - __state.Health) > float.Epsilon)
        {
            ActivityMusicService.RecordWork(
                t.getLastFarmerToUse(),
                __instance.Location,
                "tool.fruit-tree.success"
            );
        }
    }

    private static void ResourceClumpToolActionPrefix(
        ResourceClump __instance,
        out ResourceClumpToolObservation __state
    )
    {
        __state = new ResourceClumpToolObservation(__instance.health.Value);
    }

    private static void ResourceClumpToolActionPostfix(
        ResourceClump __instance,
        Tool? t,
        ResourceClumpToolObservation __state
    )
    {
        if (t is null
            || t is MeleeWeapon
            || Math.Abs(__instance.health.Value - __state.Health) <= float.Epsilon)
        {
            return;
        }

        ActivityMusicService.RecordWork(
            t.getLastFarmerToUse(),
            __instance.Location,
            "tool.resource-clump.success"
        );
    }

    private static void BushToolActionPrefix(
        Bush __instance,
        out BushToolObservation __state
    )
    {
        __state = new BushToolObservation(__instance.health);
    }

    private static void BushToolActionPostfix(
        Bush __instance,
        Tool? t,
        BushToolObservation __state
    )
    {
        if (t is null
            || t is MeleeWeapon
            || Math.Abs(__instance.health - __state.Health) <= float.Epsilon)
        {
            return;
        }

        ActivityMusicService.RecordWork(
            t.getLastFarmerToUse(),
            __instance.Location,
            "tool.bush.success"
        );
    }

    private static void FlooringToolActionPostfix(
        Flooring __instance,
        Tool? t,
        bool __result
    )
    {
        if (!__result || t is null || t is MeleeWeapon)
            return;

        ActivityMusicService.RecordWork(
            t.getLastFarmerToUse(),
            __instance.Location,
            "tool.flooring.success"
        );
    }

    private static void HoePrefix(
        GameLocation location,
        out HoeObservation __state
    )
    {
        __state = new HoeObservation(location);
    }

    private static void HoePostfix(
        GameLocation location,
        Farmer? who,
        HoeObservation __state
    )
    {
        foreach (var pair in location.terrainFeatures.Pairs)
        {
            if (pair.Value is HoeDirt && !__state.ExistingHoeDirtTiles.Contains(pair.Key))
            {
                ActivityMusicService.RecordFarming(who, location, "farming.hoe.success");
                return;
            }
        }
    }

    private static void HoeDirtToolActionPrefix(
        HoeDirt __instance,
        Tool? t,
        out HoeDirtToolObservation __state
    )
    {
        __state = new HoeDirtToolObservation(
            __instance.isWatered(),
            __instance.crop is not null
        );
    }

    private static void HoeDirtToolActionPostfix(
        HoeDirt __instance,
        Tool? t,
        bool __result,
        HoeDirtToolObservation __state
    )
    {
        if (t is WateringCan
            && !__state.WasWatered
            && __instance.isWatered())
        {
            ActivityMusicService.RecordFarming(
                t.getLastFarmerToUse(),
                __instance.Location,
                "farming.water.success"
            );
            return;
        }

        if (t is Pickaxe && __result && !__state.HadCrop && __instance.crop is null)
        {
            ActivityMusicService.RecordWork(
                t.getLastFarmerToUse(),
                __instance.Location,
                "tool.pickaxe.hoe-dirt.success"
            );
            return;
        }

        if (t is null || t is MeleeWeapon || !__state.HadCrop || __instance.crop is not null)
        {
            return;
        }

        ActivityMusicService.RecordFarming(
            t!.getLastFarmerToUse(),
            __instance.Location,
            "farming.crop-tool-hit.success"
        );
    }

    private static void WateringCanPrefix(
        WateringCan __instance,
        GameLocation location,
        int x,
        int y,
        out WateringCanObservation __state
    )
    {
        __state = new WateringCanObservation(
            __instance.WaterLeft,
            location.CanRefillWateringCanOnTile(x / 64, y / 64)
        );
    }

    private static void WateringCanPostfix(
        WateringCan __instance,
        GameLocation location,
        Farmer? who,
        WateringCanObservation __state
    )
    {
        if (__state.CanRefill && __instance.WaterLeft > __state.WaterLeft)
        {
            ActivityMusicService.RecordWork(
                who,
                location,
                "tool.watering-can.refill.success"
            );
        }
    }

    private static void HoeDirtPlantPostfix(
        HoeDirt __instance,
        Farmer? who,
        bool __result
    )
    {
        if (!__result)
            return;

        ActivityMusicService.RecordFarming(who, __instance.Location, "farming.plant.success");
    }

    private static void ObjectPlacementPostfix(
        SObject __instance,
        GameLocation location,
        Farmer? who,
        bool __result
    )
    {
        if (!__result || who is null)
            return;

        if (__instance.QualifiedItemId.StartsWith("(BC)", StringComparison.Ordinal)
            || __instance.IsTapper()
            || string.Equals(__instance.QualifiedItemId, "(O)805", StringComparison.Ordinal)
            || __instance.GetMachineData() is not null)
        {
            ActivityMusicService.RecordWork(who, location, "machine.placement.success");
        }
    }

    private static void ObjectDropInPrefix(
        SObject __instance,
        out MachineDropInObservation __state
    )
    {
        __state = new MachineDropInObservation(__instance);
    }

    private static void ObjectDropInPostfix(
        SObject __instance,
        bool probe,
        Farmer? who,
        bool __result,
        MachineDropInObservation __state
    )
    {
        if (probe || who is null || !__state.IsMachine || SObject.autoLoadFrom is not null)
            return;

        var changed = __instance.MinutesUntilReady != __state.MinutesUntilReady
            || !ReferenceEquals(__instance.lastInputItem.Value, __state.LastInputItem)
            || !ReferenceEquals(__instance.heldObject.Value, __state.HeldObject);
        if (__result || changed)
        {
            ActivityMusicService.RecordWork(who, __instance.Location, "machine.drop-in.success");
        }
    }

    private static void MachineHarvestPrefix(
        SObject __instance,
        out MachineHarvestObservation __state
    )
    {
        __state = new MachineHarvestObservation(__instance);
    }

    private static void MachineHarvestPostfix(
        SObject __instance,
        Farmer? who,
        bool justCheckingForActivity,
        bool __result,
        MachineHarvestObservation __state
    )
    {
        if (!__result
            || justCheckingForActivity
            || who is null
            || !__state.ReadyForHarvest
            || __instance.readyForHarvest.Value)
        {
            return;
        }

        ActivityMusicService.RecordWork(who, __instance.Location, "machine.harvest.success");
    }

    private static void GroundPickupPrefix(
        GameLocation __instance,
        TileLocation tileLocation,
        out GroundPickupObservation? __state
    )
    {
        var tile = new Vector2(tileLocation.X, tileLocation.Y);
        __state = null;
        var hasPickup = __instance.Objects.TryGetValue(tile, out var item)
            && (item.isSpawnedObject.Value || item.isForage());
        var hoeDirt = __instance.terrainFeatures.TryGetValue(tile, out var feature)
            && feature is HoeDirt dirt
            && dirt.crop is not null
            ? dirt
            : null;
        if (!hasPickup && hoeDirt is null)
        {
            return;
        }

        __state = new GroundPickupObservation(tile, hasPickup, hoeDirt);
    }

    private static void GroundPickupPostfix(
        GameLocation __instance,
        Farmer? who,
        bool __result,
        GroundPickupObservation? __state
    )
    {
        if (!__result || who is null || __state is null)
        {
            return;
        }

        if (__state.HasPickup
            && __instance.Objects.TryGetValue(__state.Tile, out var item)
            && (item.isSpawnedObject.Value || item.isForage()))
        {
            return;
        }

        if (__state.HoeDirt is not null && __state.HoeDirt.crop is null)
        {
            // HoeDirt.performUseAction in Stardew 1.6.15 reads Game1.player while harvesting.
            // A remote Farmer can still be the checkAction argument, so do not turn that global
            // read into a remote owner claim unless the action is the local player's action.
            if (!ReferenceEquals(who, Game1.player))
                return;

            ActivityMusicService.RecordFarming(who, __instance, "farming.harvest.success");
            return;
        }

        if (__state.HasPickup)
            ActivityMusicService.RecordWork(who, __instance, "pickup.success");
    }

    private static void MilkPailPrefix(MilkPail __instance, out AnimalToolObservation __state)
    {
        __state = new AnimalToolObservation(__instance.animal);
    }

    private static void MilkPailPostfix(
        MilkPail __instance,
        Farmer? who,
        AnimalToolObservation __state
    )
    {
        if (__state.Produce is not null
            && __state.Animal is not null
            && __state.Animal.currentProduce.Value is null)
        {
            ActivityMusicService.RecordWork(who, "animal.milk.success");
        }
    }

    private static void ShearsPrefix(Shears __instance, out AnimalToolObservation __state)
    {
        __state = new AnimalToolObservation(__instance.animal);
    }

    private static void ShearsPostfix(
        Shears __instance,
        Farmer? who,
        AnimalToolObservation __state
    )
    {
        if (__state.Produce is not null
            && __state.Animal is not null
            && __state.Animal.currentProduce.Value is null)
        {
            ActivityMusicService.RecordWork(who, "animal.shears.success");
        }
    }

    private static void PanPrefix(Pan __instance, GameLocation location, out Point __state)
    {
        __state = location.orePanPoint.Value;
    }

    private static void PanPostfix(Pan __instance, GameLocation location, Farmer? who, Point __state)
    {
        if (__state != Point.Zero && location.orePanPoint.Value == Point.Zero)
            ActivityMusicService.RecordWork(who, location, "pan.success");
    }

    private static void CraftingPostfix(CraftingRecipe __instance)
    {
        // consumeIngredients is reached only after the crafting page accepted the recipe. Its
        // cooking flag is the reliable boundary that excludes all cooking recipes.
        if (!__instance.isCookingRecipe && Game1.player is not null)
            ActivityMusicService.RecordWork(Game1.player, "crafting.success");
    }

    private static void MillChestPrefix(
        Building __instance,
        string name,
        Farmer? who,
        out MillChestObservation? __state
    )
    {
        __state = null;
        if (!IsMill(__instance))
            return;

        var data = __instance.GetBuildingChestData(name);
        if (data is null)
            return;

        __state = new MillChestObservation(
            __instance,
            data.Type.ToString(),
            __instance.GetBuildingChest(name),
            who
        );
    }

    private static void MillChestPostfix(
        string name,
        Farmer? who,
        bool __result,
        MillChestObservation? __state
    )
    {
        _ = name;
        if (!__result || who is null || __state is null || __state.Chest is null)
            return;

        var changedChest = CountStack(__state.Chest.Items) < __state.ChestStack;
        var changedActiveItem = __state.ActiveItemStack.HasValue
            && (who.ActiveObject is null || who.ActiveObject.Stack < __state.ActiveItemStack.Value);
        if (!changedChest && !changedActiveItem)
            return;

        if (string.Equals(__state.Type, "Load", StringComparison.Ordinal)
            || (string.Equals(__state.Type, "Collect", StringComparison.Ordinal)
                && ReferenceEquals(who, Game1.player)))
        {
            ActivityMusicService.RecordWork(who, "mill.player-operation.success");
        }
    }

    private static void DamageMonsterPrefix(
        GameLocation __instance,
        Rectangle areaOfEffect,
        Farmer? who,
        out DamageObservation __state
    )
    {
        __state = new DamageObservation();
        if (who is null)
            return;

        foreach (var character in __instance.characters)
        {
            if (character is Monster monster
                && monster.Health > 0
                && monster.GetBoundingBox().Intersects(areaOfEffect))
            {
                __state.Monsters.Add((monster, monster.Health));
            }
        }
    }

    private static void DamageMonsterPostfix(
        Farmer? who,
        DamageObservation __state
    )
    {
        if (who is null)
            return;

        foreach (var observation in __state.Monsters)
        {
            // Hostile shadow attacks and shadow hits belong to the Sanity audio lane; neither
            // direction may start, refresh, or preempt ordinary activity combat music.
            if (
                observation.Monster is not HostileShadowMonster
                && observation.Monster.Health < observation.Health
            )
            {
                ActivityMusicService.RecordCombat(who, "combat.monster.damage");
                return;
            }
        }
    }

    private static void FarmerTakeDamagePrefix(Farmer __instance, out int __state)
    {
        __state = __instance.health;
    }

    private static void FarmerTakeDamagePostfix(
        Farmer __instance,
        Monster? damager,
        int __state
    )
    {
        // Hostile shadow attacks already belong to the Sanity audio lane. They must not become
        // ordinary activity-combat facts, otherwise they can start, refresh, or preempt Work/
        // Farming/Combat music through the generic Farmer.takeDamage seam.
        if (
            damager is not null
            && damager is not HostileShadowMonster
            && __instance.health < __state
        )
        {
            ActivityMusicService.RecordCombat(__instance, "combat.farmer.damage");
        }
    }

    private static void BombExplosionPrefix(
        GameLocation __instance,
        bool damageFarmers,
        out PlayerHealthObservation __state
    )
    {
        var player = Game1.player;
        __state = damageFarmers && ReferenceEquals(player.currentLocation, __instance)
            ? new PlayerHealthObservation(player, __instance, player.health)
            : default;
    }

    private static void BombExplosionPostfix(PlayerHealthObservation __state)
    {
        if (__state.Player is not null
            && ReferenceEquals(__state.Player.currentLocation, __state.Location)
            && __state.Player.health < __state.Health)
        {
            ActivityMusicService.RecordCombat(__state.Player, "combat.bomb.damage");
        }
    }

    private static void TrainUpdatePrefix(
        GameLocation location,
        out PlayerHealthObservation __state
    )
    {
        var player = Game1.player;
        __state = !Game1.eventUp && location.Equals(Game1.currentLocation)
            ? new PlayerHealthObservation(player, location, player.health)
            : default;
    }

    private static void TrainUpdatePostfix(PlayerHealthObservation __state)
    {
        if (__state.Player is not null
            && ReferenceEquals(__state.Player.currentLocation, __state.Location)
            && __state.Player.health < __state.Health)
        {
            ActivityMusicService.RecordCombat(__state.Player, "combat.train.collision");
        }
    }

    private static bool IsMill(Building building)
    {
        // In 1.6.15 mills are data-driven plain Building instances; Mill only remains as a
        // legacy save-compatibility type. Use the live building identity so this bridge does not
        // make the obsolete type part of the runtime contract.
        return string.Equals(building.buildingType.Value, "Mill", StringComparison.Ordinal);
    }

    private static int CountStack(IInventory? inventory)
    {
        if (inventory is null)
            return 0;

        var total = 0;
        foreach (var item in inventory)
        {
            if (item is not null)
                total += Math.Max(1, item.Stack);
        }

        return total;
    }
}
