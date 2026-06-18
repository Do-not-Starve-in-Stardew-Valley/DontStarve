using System;
using System.Collections.Generic;
using StardewValley;

namespace DontStarve.Buff.Buffs;

/// <summary>
/// 体力恢复 Buff 适配层；总量节奏由 FixedRestoreBuff 控制，这里只负责上限安全写回。
/// </summary>
internal class StaminaRestoreBuff : FixedRestoreBuff
{
    private static readonly string[] STAMINA_RESTORE_BUFF_IDS =
    {
        "DS_Heal_Stamina",
        "DS_BUFF_STAMINA_RESTORE",
    };

    protected override string SaveKey => "DontStarve.Buff.StaminaRestore";
    protected override IReadOnlyList<string> BuffIds => STAMINA_RESTORE_BUFF_IDS;
    protected override int AmountPerPulse => 1;

    protected override void ApplyRestore(Farmer player, int amount)
    {
        if (amount <= 0 || player.Stamina >= player.MaxStamina)
            return;

        player.Stamina += Math.Min(amount, player.MaxStamina - player.Stamina);
    }
}
