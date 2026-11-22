#nullable enable

using System;

namespace DontStarve.Player.Combat.CombatAutomation.MouseFacing;

/// <summary>
/// 跟随鼠标转身分区纯规则（设计稿 4.5 / 决议 9）：以玩家所在格中心为原点，按恒 45°
/// 对角线把平面分四区——<c>|dx| &gt; |dy| → 左/右（按 dx 符号）；否则 → 上/下（按 dy 符号）</c>。
/// 与玩家所在格四角延伸的对角线分区等价：分区锚点是世界坐标的格中心，玩家不居中
/// （屏幕滚动）时分区稳定，不随视口变化。
/// 输出值对齐原版 FacingDirection：0=上 1=右 2=下 3=左。
/// 边界值（|dx| == |dy|）落上/下（垂直区）：与原版朝向判定的分区口径一致
/// （<c>Math.Abs(vector.X) &gt; Math.Abs(vector.Y)</c> 才判左右），玩家与鼠标同格
/// （dx=dy=0）落上，同样与原版一致。
/// 输入为 float（鼠标世界坐标 − 玩家格中心），无分配。
/// </summary>
internal static class MouseFacingMapper
{
    internal const int FaceUp = 0;
    internal const int FaceRight = 1;
    internal const int FaceDown = 2;
    internal const int FaceLeft = 3;

    /// <summary>鼠标相对玩家格中心的偏移 → 原版 FacingDirection 值。</summary>
    internal static int MapDirection(float dx, float dy)
    {
        if (Math.Abs(dx) > Math.Abs(dy))
        {
            return dx > 0f ? FaceRight : FaceLeft;
        }

        return dy > 0f ? FaceDown : FaceUp;
    }
}
