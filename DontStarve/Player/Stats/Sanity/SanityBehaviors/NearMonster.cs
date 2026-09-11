using System;
using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

/// <summary>
/// 根据附近怪物降低理智：资源表给基础值，10 格内按距离平方反比衰减。
/// </summary>
internal class NearMonster : ITimeRelatedBehavior
{
    private const string SAVE_KEY = "DontStarve.Sanity.NearMonster";
    private const string EnabledModDataValue = "1";
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private Dictionary<string, double> monsterSanity = null!;
    private long wait;

    internal NearMonster(SanitySystemLifecycleCoordinator lifecycle)
    {
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    public void Init(IModHelper helper)
    {
        // key 使用 Stardew 怪物 Name；扩表前要先确认游戏内实际名称。
        monsterSanity = helper.ModContent.Load<Dictionary<string, double>>(
            "Asset/Sanity/monster.json"
        );
    }

    public void Update(long _)
    {
        if (wait > 0)
        {
            wait--;
            return;
        }

        var player = Game1.player;
        if (player == null)
            return;

        var location = player.currentLocation;
        if (location == null)
            return;

        var playerCenter = player.GetBoundingBox().Center;
        var value = 0.0;
        var dangerTierActive = IsDangerTierActive(player);

        // 每分钟只扫当前地点 characters，避免跨地点或全局 NPC 扫描进入高频路径。
        foreach (var npc in location.characters)
        {
            if (npc is not Monster monster)
                continue;

            var isHostileShadow = false;
            var isBindingHidden = false;
            var assetBindingId = string.Empty;
            if (monster is HostileShadowMonster hostileShadow)
            {
                isHostileShadow = true;
                hostileShadow.modData.TryGetValue(
                    HostileShadowMonster.AssetBindingModDataKey,
                    out assetBindingId
                );
                isBindingHidden =
                    hostileShadow.modData.TryGetValue(
                        HostileShadowMonster.BindingHiddenModDataKey,
                        out var bindingHidden
                    )
                    && string.Equals(
                        bindingHidden,
                        EnabledModDataValue,
                        StringComparison.Ordinal
                    );
            }

            if (
                !SanityBehaviorRules.ShouldApplyMonsterSanityLoss(
                    isHostileShadow,
                    assetBindingId,
                    isBindingHidden,
                    dangerTierActive
                )
            )
            {
                continue;
            }

            // DIAG-20260807: 用 GetBoundingBox().Center（世界像素中心）而非 monster.Tile
            // （左上角格坐标）做距离检测——影怪等自定义怪物的受击框中心已按贴图锚点
            // 校准（ActorOriginSourcePx），靠近掉 san 依赖的“怪物真正位置”以中心为准；
            // 对所有原版怪物同样更准确（中心对中心）。
            var monsterCenter = monster.GetBoundingBox().Center;
            var playerPosition = new Vector2(playerCenter.X, playerCenter.Y);
            var monsterPosition = new Vector2(monsterCenter.X, monsterCenter.Y);
            // GetBoundingBox().Center is expressed in world pixels; the shared
            // falloff rule expects tile distance (64 world pixels per tile).
            var distanceTiles = Util.Distance(playerPosition, monsterPosition)
                / Game1.tileSize;
            value += SanityBehaviorRules.CalculateMonsterLoss(
                monsterSanity.GetValueOrDefault(monster.Name, 0),
                distanceTiles
            );
        }

        if (value > 0)
            player.ChangeSanity(-value, SanityChangeSource.Monster);
    }

    private bool IsDangerTierActive(Farmer player)
    {
        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(
            player.UniqueMultiplayerID
        );
        if (
            !SanityPlayerKey.IsCanonical(playerKey)
            || !lifecycle.TryGetTierState(playerKey, out var tier)
            || tier is null
            || !tier.IsAvailable
        )
        {
            return false;
        }

        foreach (var tierId in tier.ActiveTierIds)
        {
            if (string.Equals(tierId, SanityTierIds.Danger, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public void Sync(long _, long delta)
    {
        if (delta < 0)
            wait += -delta;
    }

    public void Load(IModHelper helper)
    {
        var data = helper.Data.ReadSaveData<NearMonsterData>(SAVE_KEY);
        wait = data?.Wait ?? 0;
    }

    public void Save(IModHelper helper)
    {
        helper.Data.WriteSaveData(SAVE_KEY, new NearMonsterData { Wait = wait });
    }
}

internal class NearMonsterData
{
    public long Wait { get; init; }
}
