#nullable enable

using System;
using StardewValley.Tools;

namespace DontStarve.Player.Combat.CombatAutomation.AutoSpecial;

/// <summary>
/// 原版特技冷却读取适配（设计稿 4.2）：<c>MeleeWeapon.defenseCooldown / clubCooldown /
/// daggerCooldown</c> 是 public static int（毫秒；MeleeWeapon.cs:87-93，MeleeWeapon.Update
/// 每帧减 <c>time.ElapsedGameTime.Milliseconds</c>，MeleeWeapon.cs:387-419）。直接读字段，
/// 无反射。委托槽为可测性设计：测试可注入假冷却；默认槽读原版静态字段。
/// 多人注记：这些字段是进程级静态全局（不按玩家区分，原版语义即如此）。本驱动只在
/// 本地玩家触发判定中读取——本地端读到的就是本地玩家实际可特技的时机；远程玩家的
/// 格挡/特技由其所在端各自结算与表现，互不影响，语义可接受。
/// </summary>
internal sealed class SmapiSpecialCooldownReader
{
    private readonly Func<int> defenseCooldownMs;
    private readonly Func<int> clubCooldownMs;
    private readonly Func<int> daggerCooldownMs;

    internal SmapiSpecialCooldownReader()
        : this(
            static () => MeleeWeapon.defenseCooldown,
            static () => MeleeWeapon.clubCooldown,
            static () => MeleeWeapon.daggerCooldown
        )
    {
    }

    internal SmapiSpecialCooldownReader(
        Func<int> defenseCooldownMs,
        Func<int> clubCooldownMs,
        Func<int> daggerCooldownMs
    )
    {
        this.defenseCooldownMs =
            defenseCooldownMs ?? throw new ArgumentNullException(nameof(defenseCooldownMs));
        this.clubCooldownMs =
            clubCooldownMs ?? throw new ArgumentNullException(nameof(clubCooldownMs));
        this.daggerCooldownMs =
            daggerCooldownMs ?? throw new ArgumentNullException(nameof(daggerCooldownMs));
    }

    /// <summary>剑格挡冷却剩余毫秒（type 3 specialCooldown 同源）；≤0 = 可格挡。</summary>
    internal int DefenseCooldownMs => defenseCooldownMs();

    /// <summary>锤砸地冷却剩余毫秒（type 2 specialCooldown 同源）；≤0 = 可特技。</summary>
    internal int ClubCooldownMs => clubCooldownMs();

    /// <summary>匕首连刺冷却剩余毫秒（type 1 specialCooldown 同源）；≤0 = 可特技。</summary>
    internal int DaggerCooldownMs => daggerCooldownMs();
}
