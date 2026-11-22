using DontStarve.Player.Combat.CombatAutomation;
using DontStarve.Player.Combat.CombatAutomation.AutoSpecial;
using DontStarve.Player.Combat.CombatAutomation.AutoSwing;
using Xunit;

namespace DontStarve.Tests.Player.Combat.CombatAutomation;

public sealed class CombatAutomationConfigSnapshotTests
{
    [Fact]
    public void Fresh_snapshot_fails_closed_with_every_feature_disabled()
    {
        var snapshot = new CombatAutomationConfigSnapshot();

        Assert.False(snapshot.AutoSwingMasterEnabled);
        Assert.False(snapshot.AutoSwingSwordEnabled);
        Assert.False(snapshot.AutoSwingClubEnabled);
        Assert.False(snapshot.AutoSwingDaggerEnabled);
        Assert.False(snapshot.SlingshotAutoReleaseEnabled);
        Assert.Equal(0, snapshot.SlingshotReleasePowerPercent);
        Assert.Equal("off", snapshot.AutoSpecialMode, ignoreCase: false);
        Assert.Equal(AutoSpecialMode.Off, snapshot.ParsedAutoSpecialMode);
        Assert.False(snapshot.MouseFacingEnabled);
    }

    [Fact]
    public void Tick_driver_is_required_only_while_the_slingshot_switch_is_on()
    {
        var snapshot = new CombatAutomationConfigSnapshot();
        Assert.False(snapshot.RequiresTickDriver);

        snapshot.SlingshotAutoReleaseEnabled = true;
        Assert.True(snapshot.RequiresTickDriver);

        snapshot.SlingshotAutoReleaseEnabled = false;
        snapshot.SlingshotReleasePowerPercent = 100;
        Assert.False(snapshot.RequiresTickDriver);
    }

    [Fact]
    public void Tick_driver_is_required_for_auto_special_and_mouse_facing()
    {
        var snapshot = new CombatAutomationConfigSnapshot();
        Assert.False(snapshot.RequiresTickDriver);

        // 自动特技（阶段 4）：模式非 off 需要 tick 扫描（剑格挡任何时刻生效）。
        snapshot.ParsedAutoSpecialMode = AutoSpecialMode.All;
        Assert.True(snapshot.RequiresTickDriver);
        snapshot.ParsedAutoSpecialMode = AutoSpecialMode.Off;
        Assert.False(snapshot.RequiresTickDriver);

        // 跟随鼠标转身：事件式驱动（Farmer.canMoveNow 回调），不订阅 UpdateTicked。
        snapshot.MouseFacingEnabled = true;
        Assert.False(snapshot.RequiresTickDriver);
    }
}
