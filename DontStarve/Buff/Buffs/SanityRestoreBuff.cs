using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity;
using StardewValley;

namespace DontStarve.Buff.Buffs;

/// <summary>
/// 理智恢复 Buff 适配层；理智上限由 SetSanity 统一裁剪。
/// </summary>
internal class SanityRestoreBuff : FixedRestoreBuff
{
    private static readonly string[] SANITY_RESTORE_BUFF_IDS =
    {
        "DS_Heal_Sanity",
        "DS_BUFF_SANITY_RESTORE",
    };

    protected override string SaveKey => "DontStarve.Buff.SanityRestore";
    protected override IReadOnlyList<string> BuffIds => SANITY_RESTORE_BUFF_IDS;
    protected override int AmountPerPulse => 1;

    protected override void ApplyRestore(Farmer player, int amount)
    {
        if (amount <= 0)
            return;

        player.SetSanity(player.GetSanity() + amount);
    }
}
