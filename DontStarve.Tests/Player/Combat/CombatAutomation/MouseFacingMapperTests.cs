using DontStarve.Player.Combat.CombatAutomation.MouseFacing;
using Xunit;

namespace DontStarve.Tests.Player.Combat.CombatAutomation;

/// <summary>
/// 跟随鼠标转身分区纯规则（设计稿 4.5 / 决议 9）：格中心原点、45° 对角线分区、
/// 边界值（|dx|==|dy|）落垂直区——与原版弹弓瞄准转身分区（Slingshot.cs:408-424）一致。
/// </summary>
public sealed class MouseFacingMapperTests
{
    [Theory]
    [InlineData(64f, 0f, 1)] // 正右
    [InlineData(-64f, 0f, 3)] // 正左
    [InlineData(0f, 64f, 2)] // 正下
    [InlineData(0f, -64f, 0)] // 正上
    [InlineData(64f, 32f, 1)] // 右下偏右
    [InlineData(-64f, 32f, 3)] // 左下偏左
    [InlineData(32f, 64f, 2)] // 右下偏下
    [InlineData(-32f, -64f, 0)] // 左上偏上
    public void Quadrants_map_to_vanilla_facing_direction_values(float dx, float dy, int expected)
    {
        Assert.Equal(expected, MouseFacingMapper.MapDirection(dx, dy));
    }

    [Theory]
    [InlineData(32f, 32f, 2)] // |dx|==|dy| → 垂直区：dy>0 → 下（口径锁定，与弹弓同源）
    [InlineData(-32f, 32f, 2)]
    [InlineData(64f, -64f, 0)] // dy<0 → 上
    [InlineData(-64f, -64f, 0)]
    [InlineData(0f, 0f, 0)] // 鼠标与格中心重合 → 上（与弹弓 dy<=0 分支一致）
    public void Diagonal_boundary_values_fall_into_vertical_zones(float dx, float dy, int expected)
    {
        Assert.Equal(expected, MouseFacingMapper.MapDirection(dx, dy));
    }

    [Fact]
    public void Tile_center_origin_keeps_partition_stable_inside_the_tile()
    {
        // 决议 9：原点是玩家格中心（世界坐标），不是玩家精灵中心也不是屏幕中心。
        // 鼠标横向离格中心 1px、纵向 0px → 右；离格中心纵向 1px → 下。分区内任意
        // 偏移不改变结论。
        Assert.Equal(MouseFacingMapper.FaceRight, MouseFacingMapper.MapDirection(1f, 0f));
        Assert.Equal(MouseFacingMapper.FaceDown, MouseFacingMapper.MapDirection(0f, 1f));
        Assert.Equal(MouseFacingMapper.FaceLeft, MouseFacingMapper.MapDirection(-1f, 0f));
        Assert.Equal(MouseFacingMapper.FaceUp, MouseFacingMapper.MapDirection(0f, -1f));
    }

    [Fact]
    public void Fractional_offsets_keep_the_same_partition()
    {
        Assert.Equal(MouseFacingMapper.FaceRight, MouseFacingMapper.MapDirection(32.5f, 32.4f));
        Assert.Equal(MouseFacingMapper.FaceDown, MouseFacingMapper.MapDirection(32.4f, 32.5f));
        // |dx| > |dy| → 左；|dy| > |dx| → 上。
        Assert.Equal(MouseFacingMapper.FaceLeft, MouseFacingMapper.MapDirection(-0.5f, -0.4f));
        Assert.Equal(MouseFacingMapper.FaceUp, MouseFacingMapper.MapDirection(-0.4f, -0.5f));
    }

    [Fact]
    public void Facing_constants_align_with_vanilla_facing_direction()
    {
        Assert.Equal(0, MouseFacingMapper.FaceUp);
        Assert.Equal(1, MouseFacingMapper.FaceRight);
        Assert.Equal(2, MouseFacingMapper.FaceDown);
        Assert.Equal(3, MouseFacingMapper.FaceLeft);
    }
}
