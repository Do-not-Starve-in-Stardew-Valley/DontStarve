#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;
using TileLocation = xTile.Dimensions.Location;
using TileRectangle = xTile.Dimensions.Rectangle;

namespace DontStarve.Player.Stats.Sanity;

/// <summary>Observes successful harvests; only the host settles their fixed Sanity reward.</summary>
internal sealed class SmapiFlowerCollectionSanityService : IDisposable
{
    private const string MessageType = "Sanity.FlowerCollection.v1";
    private const int MaximumPending = 512;
    private static SmapiFlowerCollectionSanityService? active;
    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly string modId;
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly Harmony harmony;
    private readonly List<MethodInfo> patches = new();
    private readonly List<Pending> pending = new();
    private readonly HashSet<(long Owner, long Nonce)> queued = new();
    private readonly HashSet<string> loggedReasons = new(StringComparer.Ordinal);
    private readonly FlowerCollectionSanitySettlement settlements = new();
    private long nonce;

    private sealed record Pending(FlowerCollectionRequest Request, long Sender, long Deadline);
    private sealed record CropObservation(Farmer Picker, GameLocation Location,
        string ItemId, int X, int Y, bool Dead, bool Ready, bool FullyGrown, int RegrowDays);
    private sealed record PickupObservation(Farmer Picker, SObject Source, Vector2 Tile);

    internal SmapiFlowerCollectionSanityService(IModHelper helper, IMonitor monitor,
        string modId, SanitySystemLifecycleCoordinator lifecycle)
    {
        this.helper = helper;
        this.monitor = monitor;
        this.modId = modId;
        this.lifecycle = lifecycle;
        harmony = new Harmony(modId + ".Sanity.FlowerCollection");
        active = this;
        try
        {
            Patch(typeof(Crop), nameof(Crop.harvest), new[] {
                typeof(int), typeof(int), typeof(HoeDirt), typeof(JunimoHarvester), typeof(bool)
            }, nameof(CropPrefix), nameof(CropPostfix));
            Patch(typeof(GameLocation), nameof(GameLocation.checkAction), new[] {
                typeof(TileLocation), typeof(TileRectangle), typeof(Farmer)
            }, nameof(PickupPrefix), nameof(PickupPostfix));
        }
        catch (Exception ex)
        {
            monitor.Log($"Flower collection Sanity hooks unavailable: {ex}", LogLevel.Error);
            Unpatch();
            active = null;
        }
        helper.Events.Multiplayer.ModMessageReceived += OnMessage;
        helper.Events.GameLoop.UpdateTicked += OnUpdate;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        lifecycle.SessionClearing += OnSessionClearing;
    }

    private void Patch(Type type, string method, Type[] parameters, string prefix, string postfix)
    {
        var target = AccessTools.Method(type, method, parameters)
            ?? throw new MissingMethodException(type.FullName, method);
        patches.Add(target);
        harmony.Patch(target,
            prefix: new HarmonyMethod(typeof(SmapiFlowerCollectionSanityService), prefix),
            postfix: new HarmonyMethod(typeof(SmapiFlowerCollectionSanityService), postfix));
    }

    private bool CanObserve(Farmer? who) => Context.IsWorldReady && lifecycle.IsEnabled
        && who is not null && who.UniqueMultiplayerID != 0
        && who.IsLocalPlayer && !Game1.eventUp;

    private static void CropPrefix(Crop __instance, int xTile, int yTile, HoeDirt soil,
        JunimoHarvester? junimoHarvester, out CropObservation? __state)
    {
        __state = null;
        var service = active;
        var picker = Game1.player; // Crop.harvest itself uses this player for inventory and XP.
        if (service is null || !service.CanObserve(picker) || junimoHarvester is not null
            || !ReferenceEquals(picker.currentLocation, soil?.Location))
            return;
        try
        {
            var data = ItemRegistry.GetData(__instance.indexOfHarvest.Value);
            if (data is null || data.IsErrorItem
                || FlowerCollectionSanityPolicy.GetRecovery(data.QualifiedItemId, data.Category) <= 0)
                return;
            __state = new(picker, picker.currentLocation, data.QualifiedItemId, xTile, yTile,
                __instance.dead.Value, IsReady(__instance), __instance.fullyGrown.Value,
                __instance.dayOfCurrentPhase.Value);
        }
        catch (Exception ex) { service.LogOnce("crop-observation-failed", ex.Message); }
    }

    private static void CropPostfix(Crop __instance, bool __result, CropObservation? __state)
    {
        if (__state is not { } state || active is not { } service)
            return;
        if (FlowerCollectionSanityPolicy.WasCropHarvested(state.Dead, state.Ready, false,
            __result, state.FullyGrown, state.RegrowDays, __instance.fullyGrown.Value,
            __instance.dayOfCurrentPhase.Value))
        {
            // Use the pre-harvest ID: sunflower harvest mutates indexOfHarvest into seed ID 431.
            service.Submit(state.Picker, state.Location, state.X, state.Y, state.ItemId, FlowerCollectionKind.Crop);
        }
    }

    private static void PickupPrefix(GameLocation __instance, TileLocation tileLocation,
        Farmer who, out PickupObservation? __state)
    {
        __state = null;
        var service = active;
        if (service is null || !service.CanObserve(who))
            return;
        var tile = new Vector2(tileLocation.X, tileLocation.Y);
        if (__instance.Objects.TryGetValue(tile, out var source) && source.isSpawnedObject.Value
            && source.isForage()
            && FlowerCollectionSanityPolicy.GetRecovery(source.QualifiedItemId, source.Category) > 0)
        {
            __state = new(who, source, tile);
        }
    }

    private static void PickupPostfix(GameLocation __instance, bool __result, PickupObservation? __state)
    {
        if (!__result || __state is not { } state || active is not { } service)
            return;
        if (__instance.Objects.TryGetValue(state.Tile, out var remaining)
            && ReferenceEquals(remaining, state.Source))
            return; // The full-inventory and failed-interaction paths keep the source in place.
        service.Submit(state.Picker, __instance, (int)state.Tile.X, (int)state.Tile.Y,
            state.Source.QualifiedItemId, FlowerCollectionKind.Forage);
    }

    private void Submit(Farmer picker, GameLocation location, int x, int y,
        string itemId, FlowerCollectionKind kind)
    {
        if (!CanObserve(picker) || !SanityProtocol.IsValidSessionId(lifecycle.SessionId))
            return;
        try
        {
            var request = new FlowerCollectionRequest {
                SessionId = lifecycle.SessionId, PlayerId = picker.UniqueMultiplayerID,
                Nonce = checked(++nonce), Day = Game1.stats.DaysPlayed,
                LocationName = location.NameOrUniqueName, TileX = x, TileY = y,
                QualifiedItemId = itemId, Kind = kind,
            };
            if (Context.IsMainPlayer)
                Enqueue(request, request.PlayerId);
            else if (Game1.MasterPlayer is { } host)
                helper.Multiplayer.SendMessage(request, MessageType, new[] { modId }, new[] { host.UniqueMultiplayerID });
        }
        catch (Exception ex) { LogOnce("collection-submission-failed", ex.Message); }
    }

    private void OnMessage(object? sender, ModMessageReceivedEventArgs e)
    {
        if (!Context.IsMainPlayer || !Context.IsWorldReady || e.FromModID != modId || e.Type != MessageType)
            return;
        try
        {
            var request = e.ReadAs<FlowerCollectionRequest>();
            if (request is not null && request.IsValid(lifecycle.SessionId, Game1.stats.DaysPlayed, e.FromPlayerID))
                Enqueue(request, e.FromPlayerID);
            else
                LogOnce("collection-request-invalid", "Owner, session, day or request shape does not match.");
        }
        catch (Exception ex) { LogOnce("collection-message-invalid", ex.Message); }
    }

    private void Enqueue(FlowerCollectionRequest request, long sender)
    {
        if (queued.Contains((sender, request.Nonce)))
            return;
        if (pending.Count >= MaximumPending)
        {
            LogOnce("collection-pending-limit", "The bounded collection queue is full.");
            return;
        }
        queued.Add((sender, request.Nonce));
        pending.Add(new(request, sender, Environment.TickCount64 + 3000));
    }

    private void OnUpdate(object? sender, UpdateTickedEventArgs e)
    {
        if (!Context.IsMainPlayer || !Context.IsWorldReady || pending.Count == 0 || !e.IsMultipleOf(6))
            return;
        for (var i = pending.Count - 1; i >= 0; i--)
        {
            var entry = pending[i];
            FlowerCollectionSettlementStatus status;
            try
            {
                status = settlements.TryApply(entry.Request, entry.Sender, lifecycle.SessionId,
                    Game1.stats.DaysPlayed, ObserveHost(entry.Request), ApplyReward);
            }
            catch (Exception ex)
            {
                LogOnce("collection-settlement-failed", ex.Message);
                status = FlowerCollectionSettlementStatus.Rejected;
            }
            // Crop.harvest's postfix runs before HoeDirt removes a non-regrowing crop. Remote
            // field replication can also lag the message; keep only these bounded pending facts.
            if (status == FlowerCollectionSettlementStatus.AwaitingWorld && Environment.TickCount64 < entry.Deadline)
                continue;
            if (status is FlowerCollectionSettlementStatus.Rejected or FlowerCollectionSettlementStatus.AwaitingWorld)
                LogOnce("collection-unconfirmed", "Collection facts could not be confirmed on the host.");
            queued.Remove((entry.Sender, entry.Request.Nonce));
            pending.RemoveAt(i);
        }
    }

    private static FlowerCollectionAuthorityObservation ObserveHost(FlowerCollectionRequest request)
    {
        var picker = Game1.GetPlayer(request.PlayerId, onlyOnline: true);
        var location = picker?.currentLocation;
        var data = ItemRegistry.GetData(request.QualifiedItemId);
        if (location is null || data is null || data.IsErrorItem)
            return default;
        var ownerAtLocation = location.NameOrUniqueName == request.LocationName;
        var reach = request.Kind == FlowerCollectionKind.Crop ? 6 : 2;
        var withinReach = Math.Abs(picker!.Tile.X - request.TileX) <= reach
            && Math.Abs(picker.Tile.Y - request.TileY) <= reach;
        var tile = new Vector2(request.TileX, request.TileY);
        bool collected;
        if (request.Kind == FlowerCollectionKind.Crop)
        {
            var dirt = location.terrainFeatures.TryGetValue(tile, out var feature) ? feature as HoeDirt : null;
            if (dirt is null && location.Objects.TryGetValue(tile, out var pot) && pot is IndoorPot indoor)
                dirt = indoor.hoeDirt.Value;
            collected = dirt is not null && (dirt.crop is null || !IsReady(dirt.crop));
        }
        else
        {
            collected = !location.Objects.TryGetValue(tile, out var source)
                || source.QualifiedItemId != request.QualifiedItemId;
        }
        return new(ownerAtLocation, withinReach, collected, data.Category);
    }

    private static SanityChangeResult ApplyReward(long ownerId, double amount)
    {
        var picker = Game1.GetPlayer(ownerId, onlyOnline: true);
        return picker is null ? SanityChangeResult.Rejected("flower-collector-is-offline")
            : picker.ChangeSanity(amount, SanityChangeSource.FlowerCollection);
    }

    private static bool IsReady(Crop crop) => !crop.dead.Value
        && crop.currentPhase.Value >= crop.phaseDays.Count - 1
        && (!crop.fullyGrown.Value || crop.dayOfCurrentPhase.Value <= 0);

    private void LogOnce(string reason, string detail)
    {
        if (loggedReasons.Add(reason))
            monitor.Log($"Flower collection Sanity: {reason}: {detail}", LogLevel.Warn);
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e) => Clear();
    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e) => Clear();
    private void OnSessionClearing(SanitySessionBoundary boundary) => Clear();
    private void Clear()
    {
        pending.Clear();
        queued.Clear();
        settlements.Clear();
        loggedReasons.Clear();
        nonce = 0;
    }

    private void Unpatch()
    {
        foreach (var target in patches)
            harmony.Unpatch(target, HarmonyPatchType.All, harmony.Id);
        patches.Clear();
    }

    public void Dispose()
    {
        helper.Events.Multiplayer.ModMessageReceived -= OnMessage;
        helper.Events.GameLoop.UpdateTicked -= OnUpdate;
        helper.Events.GameLoop.DayStarted -= OnDayStarted;
        helper.Events.GameLoop.ReturnedToTitle -= OnReturnedToTitle;
        lifecycle.SessionClearing -= OnSessionClearing;
        if (ReferenceEquals(active, this))
            active = null;
        Unpatch();
        Clear();
    }
}
