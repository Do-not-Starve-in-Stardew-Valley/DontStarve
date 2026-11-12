#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using DontStarve.Player.Stats.Food;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData.GarbageCans;
using StardewValley.Characters;
using StardewValley.Objects;
using StardewObject = StardewValley.Object;

namespace DontStarve.Player.Stats.Sanity;

/// <summary>
/// Captures the facts produced by vanilla GameLocation.CheckGarbage and submits exactly one
/// interaction for the farmer who performed it. It never changes Sanity on a client directly.
/// </summary>
internal sealed class SmapiTrashCanSanityService : IDisposable
{
    private sealed class SearchState
    {
        internal SearchState(
            GameLocation location,
            string id,
            Vector2 tile,
            Farmer who,
            bool reactNpcs
        )
        {
            Location = location;
            Id = id;
            Tile = tile;
            Who = who;
            ReactNpcs = reactNpcs;
            ResidentSaw = reactNpcs && IsResidentNearby(location, tile);
        }

        internal GameLocation Location { get; }
        internal string Id { get; }
        internal Vector2 Tile { get; }
        internal Farmer Who { get; }
        internal bool ReactNpcs { get; }
        internal bool ResidentSaw { get; }
        internal bool HasTryResult { get; set; }
        internal Item? Item { get; set; }
        internal GarbageCanItemData? Selected { get; set; }
    }

    private readonly IMonitor monitor;
    private readonly Harmony harmony;
    private MethodInfo? patchedCheckGarbage;
    private MethodInfo? patchedTryGetGarbageItem;
    private bool disposed;

    [ThreadStatic]
    private static Stack<SearchState>? searchStack;

    private static SmapiTrashCanSanityService? active;

    internal SmapiTrashCanSanityService(
        IMonitor monitor,
        string modId
    )
    {
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        if (string.IsNullOrWhiteSpace(modId))
            throw new ArgumentException("Mod ID is required.", nameof(modId));

        harmony = new Harmony(string.Concat(modId, ".Sanity4.TrashCan"));
        active = this;
        if (!TryInstallPatches())
        {
            active = null;
            Unpatch();
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        if (ReferenceEquals(active, this))
            active = null;
        Unpatch();
        searchStack?.Clear();
    }

    private void Unpatch()
    {
        if (patchedCheckGarbage is not null)
            harmony.Unpatch(patchedCheckGarbage, HarmonyPatchType.All, harmony.Id);
        if (patchedTryGetGarbageItem is not null)
            harmony.Unpatch(
                patchedTryGetGarbageItem,
                HarmonyPatchType.All,
                harmony.Id
            );
    }

    internal static SanityRequestTruth ResolveHostRequest(
        SanityChangeRequest request,
        long senderPlayerId
    )
    {
        var facts = request.Garbage;
        if (facts is null)
            return SanityRequestTruth.Rejected("garbage-interaction-facts-are-missing");

        var player = Game1.GetPlayer(senderPlayerId, onlyOnline: true);
        if (
            player is null
            || player.UniqueMultiplayerID != senderPlayerId
            || !string.Equals(
                SanityPlayerKey.FromUniqueMultiplayerId(player.UniqueMultiplayerID),
                request.PlayerKey,
                StringComparison.Ordinal
            )
        )
        {
            return SanityRequestTruth.Rejected("request-player-context-does-not-match");
        }

        var normalizedId = TrashCanSanityPolicy.NormalizeGarbageCanId(
            facts.GarbageCanId
        );
        if (
            string.IsNullOrWhiteSpace(normalizedId)
            || !string.Equals(facts.GarbageCanId, normalizedId, StringComparison.Ordinal)
            || !string.Equals(
                request.InteractionId,
                string.Concat(
                    "garbage:",
                    normalizedId,
                    ":",
                    facts.TileX,
                    ":",
                    facts.TileY
                ),
                StringComparison.Ordinal
            )
        )
        {
            return SanityRequestTruth.Rejected("garbage-interaction-identity-is-invalid");
        }

        var location = player.currentLocation;
        if (
            location is null
            || string.IsNullOrWhiteSpace(facts.LocationNameOrUniqueName)
            || !string.Equals(
                location.NameOrUniqueName,
                facts.LocationNameOrUniqueName,
                StringComparison.Ordinal
            )
        )
        {
            return SanityRequestTruth.Rejected("garbage-interaction-location-is-invalid");
        }

        var residentSaw = facts.ReactNpcs
            && IsResidentNearby(location, new Vector2(facts.TileX, facts.TileY));
        var hasGarbageHat = IsGarbageHat(player);
        var hasTrashBook = player.stats.Get("Book_Trash") != 0;
        if (
            facts.ResidentSaw != residentSaw
            || facts.HasGarbageHat != hasGarbageHat
            || facts.HasTrashBook != hasTrashBook
        )
        {
            return SanityRequestTruth.Rejected("garbage-interaction-facts-do-not-match-host");
        }

        if (!TryValidateItemFacts(facts, out var item, out var reason))
            return SanityRequestTruth.Rejected(reason);

        var verifiedDoubleMegaSuccess = facts.HasItem
            && VerifyDoubleMegaSuccess(normalizedId, facts.SelectedEntryId);
        if (facts.HasItem && string.IsNullOrEmpty(facts.SelectedEntryId))
        {
            return SanityRequestTruth.Rejected("garbage-selected-entry-is-missing");
        }
        if (
            !string.IsNullOrEmpty(facts.SelectedEntryId)
            && !SelectedEntryExists(normalizedId, facts.SelectedEntryId)
        )
        {
            return SanityRequestTruth.Rejected(
                "garbage-selected-entry-is-not-in-host-data"
            );
        }
        if (facts.IsDoubleMegaSuccess != verifiedDoubleMegaSuccess)
        {
            return SanityRequestTruth.Rejected(
                "garbage-selected-entry-does-not-match-host-data"
            );
        }

        var authoritativeFacts = new TrashCanSanityFacts
        {
            LocationNameOrUniqueName = location.NameOrUniqueName,
            TileX = facts.TileX,
            TileY = facts.TileY,
            GarbageCanId = normalizedId,
            ReactNpcs = facts.ReactNpcs,
            ResidentSaw = residentSaw,
            HasGarbageHat = hasGarbageHat,
            HasTrashBook = hasTrashBook,
            HasItem = facts.HasItem,
            ItemQualifiedId = facts.ItemQualifiedId,
            ItemBasePrice = item is null ? 0 : GetBasePrice(item),
            ItemEdibility = item is null ? -300 : GetEffectiveEdibility(item),
            IsDoubleMegaSuccess = verifiedDoubleMegaSuccess,
            SelectedEntryId = facts.SelectedEntryId,
        };
        return SanityRequestTruth.AcceptedGarbage(
            TrashCanSanityPolicy.Evaluate(authoritativeFacts)
        );
    }

    internal static int GetBasePrice(Item item)
    {
        return item switch
        {
            StardewObject objectItem => Math.Max(0, objectItem.Price),
            Clothing clothing => Math.Max(0, clothing.Price),
            Boots boots => Math.Max(0, boots.price.Value),
            _ => Math.Max(0, item.salePrice(ignoreProfitMargins: true)),
        };
    }

    internal static int GetEffectiveEdibility(Item item)
    {
        return item is StardewObject objectItem
            ? SeedEdibilityRuntime.GetEffectiveTooltipEdibility(objectItem)
            : -300;
    }

    private bool TryInstallPatches()
    {
        var checkGarbage = AccessTools.Method(
            typeof(GameLocation),
            nameof(GameLocation.CheckGarbage),
            new[]
            {
                typeof(string),
                typeof(Vector2),
                typeof(Farmer),
                typeof(bool),
                typeof(bool),
                typeof(Action<string>),
            }
        );
        var tryGetGarbageItem = AccessTools.Method(
            typeof(GameLocation),
            nameof(GameLocation.TryGetGarbageItem),
            new[]
            {
                typeof(string),
                typeof(double),
                typeof(Item).MakeByRefType(),
                typeof(GarbageCanItemData).MakeByRefType(),
                typeof(Random).MakeByRefType(),
                typeof(Action<string>),
            }
        );
        var checkPrefix = AccessTools.Method(
            typeof(SmapiTrashCanSanityService),
            nameof(CheckGarbagePrefix)
        );
        var checkPostfix = AccessTools.Method(
            typeof(SmapiTrashCanSanityService),
            nameof(CheckGarbagePostfix)
        );
        var tryPostfix = AccessTools.Method(
            typeof(SmapiTrashCanSanityService),
            nameof(TryGetGarbageItemPostfix)
        );
        if (
            checkGarbage is null
            || tryGetGarbageItem is null
            || checkPrefix is null
            || checkPostfix is null
            || tryPostfix is null
        )
        {
            monitor.Log(
                "Trash-can Sanity is disabled because a vanilla garbage-can method target is unavailable.",
                LogLevel.Warn
            );
            return false;
        }

        try
        {
            patchedCheckGarbage = checkGarbage;
            patchedTryGetGarbageItem = tryGetGarbageItem;
            harmony.Patch(
                checkGarbage,
                prefix: new HarmonyMethod(checkPrefix),
                postfix: new HarmonyMethod(checkPostfix)
            );
            harmony.Patch(
                tryGetGarbageItem,
                postfix: new HarmonyMethod(tryPostfix)
            );
            return true;
        }
        catch (Exception exception)
        {
            Unpatch();
            monitor.Log(
                $"Trash-can Sanity is disabled ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Warn
            );
            return false;
        }
    }

    // Harmony 前缀只允许 void / bool 返回类型（SMAPI 自带 Harmony 在 MethodPatcher 里硬校验，
    // 其它返回类型会在 harmony.Patch 时直接抛异常并让整个垃圾桶功能 fail-closed 禁用）。
    // 状态必须走 ref __state 参数传给 postfix，不能用 return 携带。
    private static void CheckGarbagePrefix(
        GameLocation __instance,
        string id,
        Vector2 tile,
        Farmer who,
        bool reactNpcs,
        ref SearchState __state
    )
    {
        searchStack ??= new Stack<SearchState>();
        __state = new SearchState(__instance, id, tile, who, reactNpcs);
        searchStack.Push(__state);
    }

    private static void TryGetGarbageItemPostfix(
        GameLocation __instance,
        ref Item item,
        ref GarbageCanItemData selected
    )
    {
        if (searchStack is null || searchStack.Count == 0)
            return;

        var state = searchStack.Peek();
        if (!ReferenceEquals(state.Location, __instance))
            return;

        state.HasTryResult = true;
        state.Item = item;
        state.Selected = selected;
    }

    private static void CheckGarbagePostfix(SearchState __state)
    {
        try
        {
            // Vanilla returns false when no item was produced, but the fresh search still marks
            // the can checked and must still apply the interaction penalty.
            if (
                __state is not null
                && __state.HasTryResult
                && active is { disposed: false } service
            )
            {
                service.Settle(__state);
            }
        }
        finally
        {
            if (searchStack is not null && searchStack.Count > 0)
                searchStack.Pop();
        }
    }

    private void Settle(SearchState state)
    {
        var item = state.Item;
        var facts = new TrashCanSanityFacts
        {
            LocationNameOrUniqueName = state.Location.NameOrUniqueName,
            TileX = (int)state.Tile.X,
            TileY = (int)state.Tile.Y,
            GarbageCanId = TrashCanSanityPolicy.NormalizeGarbageCanId(state.Id),
            ReactNpcs = state.ReactNpcs,
            ResidentSaw = state.ResidentSaw,
            HasGarbageHat = IsGarbageHat(state.Who),
            HasTrashBook = state.Who.stats.Get("Book_Trash") != 0,
            HasItem = item is not null,
            ItemQualifiedId = item?.QualifiedItemId ?? string.Empty,
            ItemBasePrice = item is null ? 0 : GetBasePrice(item),
            ItemEdibility = item is null ? -300 : GetEffectiveEdibility(item),
            IsDoubleMegaSuccess = item is not null
                && (state.Selected?.IsDoubleMegaSuccess ?? false),
            SelectedEntryId = state.Selected?.Id ?? string.Empty,
        };

        _ = state.Who.ChangeGarbageSanity(facts);
    }

    private static bool IsGarbageHat(Farmer player)
    {
        return string.Equals(
            player.hat.Value?.QualifiedItemId,
            TrashCanSanityPolicy.GarbageHatQualifiedItemId,
            StringComparison.Ordinal
        );
    }

    private static bool IsResidentNearby(GameLocation location, Vector2 tile)
    {
        foreach (var npc in Utility.GetNpcsWithinDistance(tile, 7, location))
        {
            if (npc.IsVillager && !string.Equals(npc.Name, "Linus", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool TryValidateItemFacts(
        TrashCanSanityFacts facts,
        out Item? item,
        out string reason
    )
    {
        item = null;
        reason = string.Empty;
        if (!facts.HasItem)
        {
            if (
                !string.IsNullOrEmpty(facts.ItemQualifiedId)
                || facts.IsDoubleMegaSuccess
            )
            {
                reason = "garbage-empty-result-facts-are-invalid";
                return false;
            }

            return true;
        }

        if (
            string.IsNullOrWhiteSpace(facts.ItemQualifiedId)
            || facts.ItemQualifiedId.Length > 256
        )
        {
            reason = "garbage-item-id-is-invalid";
            return false;
        }

        item = ItemRegistry.Create(facts.ItemQualifiedId);
        if (item is null)
        {
            reason = "garbage-item-is-not-available-on-host";
            return false;
        }

        if (
            !string.Equals(
                item.QualifiedItemId,
                facts.ItemQualifiedId,
                StringComparison.Ordinal
            )
            || facts.ItemBasePrice != GetBasePrice(item)
            || facts.ItemEdibility != GetEffectiveEdibility(item)
        )
        {
            reason = "garbage-item-facts-do-not-match-host-item";
            return false;
        }

        return true;
    }

    private static bool VerifyDoubleMegaSuccess(
        string garbageCanId,
        string selectedEntryId
    )
    {
        if (string.IsNullOrEmpty(selectedEntryId))
            return false;

        var data = DataLoader.GarbageCans(Game1.content);
        if (data.BeforeAll is not null && ContainsDouble(data.BeforeAll, selectedEntryId))
            return true;
        if (data.AfterAll is not null && ContainsDouble(data.AfterAll, selectedEntryId))
            return true;
        if (
            data.GarbageCans is not null
            && data.GarbageCans.TryGetValue(garbageCanId, out var entry)
            && entry?.Items is not null
            && ContainsDouble(entry.Items, selectedEntryId)
        )
        {
            return true;
        }

        return false;
    }

    private static bool ContainsDouble(
        IEnumerable<GarbageCanItemData> entries,
        string selectedEntryId
    )
    {
        foreach (var entry in entries)
        {
            if (
                entry is not null
                && string.Equals(entry.Id, selectedEntryId, StringComparison.Ordinal)
                && entry.IsDoubleMegaSuccess
            )
            {
                return true;
            }
        }

        return false;
    }

    private static bool SelectedEntryExists(
        string garbageCanId,
        string selectedEntryId
    )
    {
        var data = DataLoader.GarbageCans(Game1.content);
        if (data.BeforeAll is not null && ContainsEntry(data.BeforeAll, selectedEntryId))
            return true;
        if (data.AfterAll is not null && ContainsEntry(data.AfterAll, selectedEntryId))
            return true;
        return data.GarbageCans is not null
            && data.GarbageCans.TryGetValue(garbageCanId, out var entry)
            && entry?.Items is not null
            && ContainsEntry(entry.Items, selectedEntryId);
    }

    private static bool ContainsEntry(
        IEnumerable<GarbageCanItemData> entries,
        string selectedEntryId
    )
    {
        foreach (var entry in entries)
        {
            if (
                entry is not null
                && string.Equals(entry.Id, selectedEntryId, StringComparison.Ordinal)
            )
            {
                return true;
            }
        }

        return false;
    }
}
