using DontStarve.Player.Combat.CombatAutomation;
using DontStarve.Player.Combat.CombatAutomation.AutoSpecial;
using Xunit;

namespace DontStarve.Tests.Player.Combat.CombatAutomation;

public sealed class AutoSpecialRulesTests
{
    [Theory]
    [InlineData("all", (int)AutoSpecialMode.All, true)]
    [InlineData("clubAndDagger", (int)AutoSpecialMode.ClubAndDagger, true)]
    [InlineData("swordOnly", (int)AutoSpecialMode.SwordOnly, true)]
    [InlineData("off", (int)AutoSpecialMode.Off, true)]
    [InlineData("every", (int)AutoSpecialMode.Off, false)]
    [InlineData("", (int)AutoSpecialMode.Off, false)]
    public void Mode_text_parses_with_fail_closed_off_fallback(
        string text,
        int expectedModeValue,
        bool expectedParsed
    )
    {
        // 内部枚举不能进公共测试方法签名（CS0051），以 int 传递并在调用处还原。
        var parsed = AutoSpecialRules.TryParseMode(text, out var mode);
        Assert.Equal(expectedParsed, parsed);
        Assert.Equal((AutoSpecialMode)expectedModeValue, mode);
    }

    [Fact]
    public void Null_mode_text_fails_closed_to_off()
    {
        Assert.False(AutoSpecialRules.TryParseMode(null, out var mode));
        Assert.Equal(AutoSpecialMode.Off, mode);
    }

    [Fact]
    public void Sword_is_included_only_by_all_and_sword_only_modes()
    {
        Assert.True(AutoSpecialRules.ModeIncludesSword(AutoSpecialMode.All));
        Assert.True(AutoSpecialRules.ModeIncludesSword(AutoSpecialMode.SwordOnly));
        Assert.False(AutoSpecialRules.ModeIncludesSword(AutoSpecialMode.ClubAndDagger));
        Assert.False(AutoSpecialRules.ModeIncludesSword(AutoSpecialMode.Off));
    }

    [Fact]
    public void Club_and_dagger_are_included_only_by_all_and_club_and_dagger_modes()
    {
        Assert.True(AutoSpecialRules.ModeIncludesClubOrDagger(AutoSpecialMode.All));
        Assert.True(AutoSpecialRules.ModeIncludesClubOrDagger(AutoSpecialMode.ClubAndDagger));
        Assert.False(AutoSpecialRules.ModeIncludesClubOrDagger(AutoSpecialMode.SwordOnly));
        Assert.False(AutoSpecialRules.ModeIncludesClubOrDagger(AutoSpecialMode.Off));
    }

    [Fact]
    public void Sword_block_requires_mode_sword_kind_and_ready_cooldown()
    {
        // 决议 5：格挡不受攻击驱动限制，规则签名因此没有驱动参数。
        Assert.True(
            AutoSpecialRules.ShouldTriggerSwordBlock(
                AutoSpecialMode.All,
                CombatWeaponKind.Sword,
                defenseCooldownMs: 0
            )
        );
        Assert.True(
            AutoSpecialRules.ShouldTriggerSwordBlock(
                AutoSpecialMode.SwordOnly,
                CombatWeaponKind.Sword,
                defenseCooldownMs: 0
            )
        );
        Assert.False(
            AutoSpecialRules.ShouldTriggerSwordBlock(
                AutoSpecialMode.All,
                CombatWeaponKind.Sword,
                defenseCooldownMs: 1500
            )
        );
        Assert.False(
            AutoSpecialRules.ShouldTriggerSwordBlock(
                AutoSpecialMode.ClubAndDagger,
                CombatWeaponKind.Sword,
                defenseCooldownMs: 0
            )
        );
        // 镰刀分类为 Scythe，永远不进剑格挡门。
        Assert.False(
            AutoSpecialRules.ShouldTriggerSwordBlock(
                AutoSpecialMode.All,
                CombatWeaponKind.Scythe,
                defenseCooldownMs: 0
            )
        );
    }

    // ---------- 剑格挡几何（矩形最近距离 < 16px，恰 16px 不触发） ----------
    // 2026-11-14 实机修复口径：锤/匕首按用户拍板改为"不检测怪物/距离"的无条件动作
    // （触发点迁移至 SmapiAutoSpecialSwingPatch），原锤盒/匕首 AoE 几何随之退役，
    // 对应用例一并移除；剑格挡贴脸几何保留如下。

    /// <summary>玩家碰撞框 (0,0)-(64,64)。</summary>
    private const int PlayerLeft = 0;
    private const int PlayerTop = 0;
    private const int PlayerRight = 64;
    private const int PlayerBottom = 64;

    [Fact]
    public void Overlapping_or_touching_monster_box_counts_as_zero_distance()
    {
        Assert.True(
            AutoSpecialRules.IsMonsterWithinBlockTriggerDistance(
                PlayerLeft,
                PlayerTop,
                PlayerRight,
                PlayerBottom,
                monsterLeft: 32,
                monsterTop: 32,
                monsterRight: 40,
                monsterBottom: 40
            )
        );
        // 边贴边 = 距离 0，仍在 16px 内。
        Assert.True(
            AutoSpecialRules.IsMonsterWithinBlockTriggerDistance(
                PlayerLeft,
                PlayerTop,
                PlayerRight,
                PlayerBottom,
                monsterLeft: 64,
                monsterTop: 20,
                monsterRight: 96,
                monsterBottom: 52
            )
        );
    }

    [Fact]
    public void Exactly_sixteen_pixel_gap_does_not_trigger_but_fifteen_does()
    {
        // 口径：坐标为整数像素，比较集合是 {..., 14, 15, 16, 17, ...}；"恰好 16px" 不
        // 触发（规则为 < 16px），没有 16.01px 实值（16 与 17 之间不存在整数像素间隔）。
        Assert.False(
            AutoSpecialRules.IsMonsterWithinBlockTriggerDistance(
                PlayerLeft,
                PlayerTop,
                PlayerRight,
                PlayerBottom,
                monsterLeft: 80,
                monsterTop: 20,
                monsterRight: 88,
                monsterBottom: 52
            )
        );
        Assert.True(
            AutoSpecialRules.IsMonsterWithinBlockTriggerDistance(
                PlayerLeft,
                PlayerTop,
                PlayerRight,
                PlayerBottom,
                monsterLeft: 79,
                monsterTop: 20,
                monsterRight: 87,
                monsterBottom: 52
            )
        );
    }

    [Fact]
    public void Diagonal_gap_uses_squared_distance_short_circuit()
    {
        // dx=12, dy=12 → 144+144=288 ≥ 256 → 不触发。
        Assert.False(
            AutoSpecialRules.IsMonsterWithinBlockTriggerDistance(
                PlayerLeft,
                PlayerTop,
                PlayerRight,
                PlayerBottom,
                monsterLeft: 76,
                monsterTop: 76,
                monsterRight: 84,
                monsterBottom: 84
            )
        );
        // dx=11, dy=11 → 121+121=242 < 256 → 触发。
        Assert.True(
            AutoSpecialRules.IsMonsterWithinBlockTriggerDistance(
                PlayerLeft,
                PlayerTop,
                PlayerRight,
                PlayerBottom,
                monsterLeft: 75,
                monsterTop: 75,
                monsterRight: 83,
                monsterBottom: 83
            )
        );
    }

    // ---------- 自动格挡非威胁裁决（2026-11-18：木乃伊假死 / 岩石蟹缩壳） ----------

    [Fact]
    public void Mummy_awaiting_revive_is_non_threat_but_living_mummy_is_not()
    {
        // 假死（reviveTimer > 0，原版 10000ms）：不会攻击玩家 → 不触发自动格挡。
        Assert.True(AutoSpecialRules.IsNonThreatForAutoBlock(
            isMummyAwaitingRevive: true,
            isCrabInShell: false
        ));
        // 活体木乃伊（reviveTimer == 0）：恢复威胁，照常触发格挡。
        Assert.False(AutoSpecialRules.IsNonThreatForAutoBlock(
            isMummyAwaitingRevive: false,
            isCrabInShell: false
        ));
    }

    [Fact]
    public void Crab_in_shell_frame_is_non_threat_but_out_of_shell_crab_is_not()
    {
        // 缩壳帧（镜像原版 takeDamage 的 currentFrame % 4 == 0 && !shellGone 弹开口径）：
        // 不触发自动格挡。
        Assert.True(AutoSpecialRules.IsNonThreatForAutoBlock(
            isMummyAwaitingRevive: false,
            isCrabInShell: true
        ));
        // 壳被打掉出壳攻击（shellGone == true → isCrabInShell=false）：恢复威胁。
        Assert.False(AutoSpecialRules.IsNonThreatForAutoBlock(
            isMummyAwaitingRevive: false,
            isCrabInShell: false
        ));
        // 两种非威胁叠加仍然只是跳过（与单条件语义一致）。
        Assert.True(AutoSpecialRules.IsNonThreatForAutoBlock(
            isMummyAwaitingRevive: true,
            isCrabInShell: true
        ));
    }
}
