using System.Collections.Generic;
using DontStarve.Player.Stats.Sanity;
using StardewValley;

namespace DontStarve.Buff.Buffs;

/// <summary>理智恢复 Buff 适配层；理智上限和主机同步由 Sanity 服务负责。</summary>
internal sealed class SanityRestoreBuff : FixedRestoreBuff
{
    private static readonly string[] SANITY_RESTORE_BUFF_IDS =
    {
        "DS_Heal_Sanity",
        "DS_BUFF_SANITY_RESTORE",
    };

    internal SanityRestoreBuff(BuffMultiplayerCoordinator multiplayer)
        : base(multiplayer) { }

    protected override IReadOnlyList<string> BuffIds => SANITY_RESTORE_BUFF_IDS;
    protected override int AmountPerPulse => 1;

    protected override void ApplyRestore(Farmer player, int amount)
    {
        if (amount > 0)
            player.ChangeSanity(amount, SanityChangeSource.Buff);
    }
}
