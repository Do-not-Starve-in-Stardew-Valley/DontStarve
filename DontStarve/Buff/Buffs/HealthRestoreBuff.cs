using System;
using System.Collections.Generic;
using StardewValley;

namespace DontStarve.Buff.Buffs;

/// <summary>
/// 生命恢复 Buff 适配层；总量节奏由 FixedRestoreBuff 控制，这里只负责上限安全写回。
/// </summary>
internal class HealthRestoreBuff : FixedRestoreBuff
{
    private static readonly string[] HEALTH_RESTORE_BUFF_IDS =
    {
        "DS_Heal_Health",
        "DS_BUFF_HEALTH_RESTORE",
    };

    protected override string SaveKey => "DontStarve.Buff.HealthRestore";
    protected override IReadOnlyList<string> BuffIds => HEALTH_RESTORE_BUFF_IDS;
    protected override int AmountPerPulse => 2;

    protected override void ApplyRestore(Farmer player, int amount)
    {
        if (amount <= 0 || player.health >= player.maxHealth)
            return;

        player.health += Math.Min(amount, player.maxHealth - player.health);
    }
}
