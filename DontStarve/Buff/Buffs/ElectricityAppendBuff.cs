using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Buff.Buffs;

/// <summary>
/// 给当前电击 Buff 追加攻击倍率；加成写回 active Buff 本体，交给 Stardew 原版聚合系统重算。
/// </summary>
internal class ElectricityAppendBuff : INonTimeRelatedBuff
{
    private const string ELECTRICITY_APPEND_BUFF_ID = "DS_Electric";
    private const string LEGACY_ELECTRICITY_APPEND_BUFF_ID = "DS_BUFF_ELECTRICITY_APPEND";
    private const float BASE_BONUS = 0.5f;
    private const float WEATHER_BONUS = 1.0f;
    private static readonly string[] ELECTRICITY_APPEND_BUFF_IDS =
    {
        ELECTRICITY_APPEND_BUFF_ID,
        LEGACY_ELECTRICITY_APPEND_BUFF_ID,
    };

    private StardewValley.Buff trackedBuff;
    private float trackedBuffBaseAttackMultiplier;

    // 记录本类已经追加的数值，电击 Buff 消失时要还原，避免污染同一个 Buff 对象的基础倍率。
    private float appliedBonus;

    public void Init(IModHelper helper)
    {
        helper.Events.GameLoop.OneSecondUpdateTicked += (_, _) => Update();
        helper.Events.GameLoop.SaveLoaded += (_, _) => ResetTracking();
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => ResetTracking();
    }

    private void Update()
    {
        if (!Context.IsWorldReady)
        {
            ResetTracking();
            return;
        }

        var player = Game1.player;
        if (player == null)
        {
            ResetTracking();
            return;
        }

        if (!TryGetElectricBuff(player, out var electricBuff))
        {
            ClearTrackedBonus(player);
            return;
        }

        var hasWeatherBuff =
            Game1.isRaining || Game1.isGreenRain || Game1.isLightning || Game1.isSnowing;

        var totalBonus = BASE_BONUS + (hasWeatherBuff ? WEATHER_BONUS : 0f);

        if (!ReferenceEquals(electricBuff, trackedBuff))
        {
            // 新 Buff 可能自带 CP/Data/Buffs 的基础倍率，先记住原值，再叠加 DS 电击额外倍率。
            ClearTrackedBonus(player);
            trackedBuff = electricBuff;
            trackedBuffBaseAttackMultiplier = electricBuff.effects.AttackMultiplier.Value;
        }

        var targetAttackMultiplier = trackedBuffBaseAttackMultiplier + totalBonus;
        if (electricBuff.effects.AttackMultiplier.Value != targetAttackMultiplier)
        {
            // 不直接写 player.buffs.GetValues() 的聚合结果；原版下一次 Dirty 重算会覆盖聚合值。
            electricBuff.effects.AttackMultiplier.Set(targetAttackMultiplier);
            appliedBonus = totalBonus;
            player.buffs.Dirty = true;
        }
    }

    private static bool TryGetElectricBuff(Farmer player, out StardewValley.Buff electricBuff)
    {
        foreach (var id in ELECTRICITY_APPEND_BUFF_IDS)
        {
            if (player.buffs.AppliedBuffs.TryGetValue(id, out electricBuff))
                return true;
        }

        electricBuff = null;
        return false;
    }

    private void ClearTrackedBonus(Farmer player)
    {
        if (trackedBuff != null && appliedBonus != 0f)
        {
            trackedBuff.effects.AttackMultiplier.Set(trackedBuffBaseAttackMultiplier);
            player.buffs.Dirty = true;
        }

        ResetTracking();
    }

    private void ResetTracking()
    {
        trackedBuff = null;
        trackedBuffBaseAttackMultiplier = 0f;
        appliedBonus = 0f;
    }
}
