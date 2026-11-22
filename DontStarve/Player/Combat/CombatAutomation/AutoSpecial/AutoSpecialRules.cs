#nullable enable

namespace DontStarve.Player.Combat.CombatAutomation.AutoSpecial;

/// <summary>
/// 自动特技模式（GMCM key=AutoSpecialMode，allowedValues: all/clubAndDagger/swordOnly/off）。
/// Off 必须是第一个成员：快照字段默认值 = default(enum) = Off，fail-closed。
/// </summary>
internal enum AutoSpecialMode
{
    Off,
    All,
    ClubAndDagger,
    SwordOnly,
}

/// <summary>
/// 自动特技触发纯规则（设计稿 5.3 / 决议 5；2026-11-14 实机修复后口径）：模式四选一
/// 裁决 + 剑格挡触发门与贴脸几何。不接触 Stardew 类型（矩形以分量 int 传入，无分配），
/// 可被测试工程链接编译。
/// 锤/匕首已按用户口径改为「不检测怪物/距离」的无条件动作（触发点迁移至
/// <see cref="SmapiAutoSpecialSwingPatch"/>），原锤盒/匕首 AoE 几何随之退役——特技
/// 范围由原版特技自身判定。
/// </summary>
internal static class AutoSpecialRules
{
    /// <summary>config-options.json 的 enum 字符串 → 模式；未知值 fail-closed 回落 Off。</summary>
    internal static bool TryParseMode(string? value, out AutoSpecialMode mode)
    {
        switch (value)
        {
            case "all":
                mode = AutoSpecialMode.All;
                return true;
            case "clubAndDagger":
                mode = AutoSpecialMode.ClubAndDagger;
                return true;
            case "swordOnly":
                mode = AutoSpecialMode.SwordOnly;
                return true;
            case "off":
                mode = AutoSpecialMode.Off;
                return true;
            default:
                mode = AutoSpecialMode.Off;
                return false;
        }
    }

    internal static bool ModeIncludesSword(AutoSpecialMode mode)
    {
        return mode is AutoSpecialMode.All or AutoSpecialMode.SwordOnly;
    }

    internal static bool ModeIncludesClubOrDagger(AutoSpecialMode mode)
    {
        return mode is AutoSpecialMode.All or AutoSpecialMode.ClubAndDagger;
    }

    /// <summary>
    /// 剑格挡触发门（决议 5：任何时刻生效，不受攻击驱动限制）。kind 由
    /// <see cref="CombatAutomation.CombatWeaponClassifier"/> 产出：镰刀分类为 Scythe，
    /// 天然不进本门（原版 animateSpecialMove 对 type 3 的镰刀排除同语义）。
    /// 冷却=原版静态 <c>MeleeWeapon.defenseCooldown</c>（毫秒，&gt;0 即格挡中）。
    /// </summary>
    internal static bool ShouldTriggerSwordBlock(
        AutoSpecialMode mode,
        CombatWeaponKind kind,
        int defenseCooldownMs
    )
    {
        return ModeIncludesSword(mode)
            && kind == CombatWeaponKind.Sword
            && defenseCooldownMs <= 0;
    }

    /// <summary>
    /// 剑格挡几何：怪物碰撞框与玩家碰撞框的矩形最近距离（像素）&lt;
    /// <see cref="CombatAutomationConstants.BlockTriggerDistancePx"/>（2026-11-15 平衡值
    /// 16）才触发。口径注记：坐标均为整数像素（原版 GetBoundingBox 为 int 矩形），因此
    /// 比较集合为 {…, 14, 15, 16, 17, …}——恰 16px 不触发（"&lt;16px"），15px 触发；
    /// 无 16.01px 实值。
    /// 碰撞框相交/贴合 = 距离 0，触发。平方判定、long 防溢出、零分配。
    /// </summary>
    internal static bool IsMonsterWithinBlockTriggerDistance(
        int playerLeft,
        int playerTop,
        int playerRight,
        int playerBottom,
        int monsterLeft,
        int monsterTop,
        int monsterRight,
        int monsterBottom
    )
    {
        long gapX = AxisGap(playerLeft, playerRight, monsterLeft, monsterRight);
        long gapY = AxisGap(playerTop, playerBottom, monsterTop, monsterBottom);
        long squaredDistance = gapX * gapX + gapY * gapY;
        return squaredDistance
            < (long)CombatAutomationConstants.BlockTriggerDistancePx
            * CombatAutomationConstants.BlockTriggerDistancePx;
    }

    /// <summary>两矩形在单轴上的间隔：区间重叠返回 0，否则返回非负间距。</summary>
    private static long AxisGap(int aMin, int aMax, int bMin, int bMax)
    {
        if (aMax <= bMin)
            return bMin - aMax;
        if (bMax <= aMin)
            return aMin - bMax;
        return 0;
    }

    /// <summary>
    /// 自动格挡的「非威胁」裁决（2026-11-18 用户需求）：贴脸扫描只看几何距离，会把
    /// 两种**当前完全不会攻击玩家**的怪物状态也当成威胁触发格挡，本规则把它们排除：
    /// - 木乃伊被普通武器击杀后进入绷带假死（<c>Mummy.reviveTimer &gt; 0</c>，1.6.15
    ///   Mummy.cs:81-96 设 10000ms 且攻击力清零、期间免疫非炸弹伤害、不主动攻击）；
    ///   复活瞬间 reviveTimer 归零恢复威胁。
    /// - 岩石蟹壳未破且处于缩壳帧（<c>!RockCrab.shellGone &amp;&amp; currentFrame % 4 == 0</c>，
    ///   逐字镜像 1.6.15 RockCrab.takeDamage:146 的弹开判定——原版打这帧也是 0 伤害
    ///   "叮"；壳被打掉出壳攻击、或帧离开缩壳位即恢复威胁；枯枝虫伪装形态与原版
    ///   同一判定天然对齐）。
    /// 只影响自动格挡的触发扫描：原版手动右键格挡、格挡中招架结算、武器伤害零改动。
    /// 岩石傀儡的蹲伏警戒态（seenPlayer=false）**刻意不排除**——它没有防御机制、任何
    /// 一次攻击都会立即激活追击（用户 2026-11-18 拍板：万一它突然站起来了呢）。
    /// </summary>
    internal static bool IsNonThreatForAutoBlock(
        bool isMummyAwaitingRevive,
        bool isCrabInShell
    )
    {
        return isMummyAwaitingRevive || isCrabInShell;
    }
}
