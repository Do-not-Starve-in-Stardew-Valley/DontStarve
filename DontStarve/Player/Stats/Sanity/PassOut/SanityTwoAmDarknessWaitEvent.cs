#nullable enable

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Events;

namespace DontStarve.Player.Stats.Sanity.PassOut;

/// <summary>
/// 修复4（2026-11-13）的等待画面承载：复用原版"夜间事件"（SoundInTheNightEvent 等 FarmEvent）
/// 的渲染/暂停语义——draw 全屏纯黑、HUD 隐藏、shouldTimePass 冻结本地时钟、工具与暂停菜单被
/// 原版门拦截、setUp 置 freezeControls 冻结输入。这是原版"夜里事件播放时所有人等"的既有机制，
/// 不引入自定义绘制热路径。
/// 契约红线：tickUpdate 必须恒返回 false。原版 farmEvent 结束路径（tickUpdate 返回 true）会
/// 硬编码执行 timeOfDay=600、床上起床 warp、移除明日邮件等换日收尾；等待画面只允许由
/// SmapiSanityTwoAmDarknessWaitService 在主机放行时置 Game1.farmEvent=null 结束，绝不能走
/// 原版结束路径。修改前必须复核 Game1.Update 的 farmEvent 分支。
/// </summary>
internal sealed class SanityTwoAmDarknessWaitEvent : BaseFarmEvent
{
    public override bool setUp()
    {
        // 原版夜间事件语义：冻结输入。注意本事件由等待服务直接赋给 Game1.farmEvent（不经过
        // 换日流程），setUp 不会被自动调用；服务侧在应用冻结时自行置 freezeControls，此处仅作
        // 兜底，保证任何路径进入本事件时输入都被冻结。
        Game1.freezeControls = true;
        return false;
    }

    public override bool tickUpdate(GameTime time)
    {
        // 红线：恒 false。等待画面的结束只能由等待服务显式移除 Game1.farmEvent 完成。
        // 返回 true 会触发原版"醒来=6:00、强制回家 warp"的换日收尾，直接破坏昏迷链。
        return false;
    }

    public override void draw(SpriteBatch b)
    {
        // 与 SoundInTheNightEvent.draw 相同的原版纯黑覆盖（world 视口尺寸，HUD 已被原版隐藏）。
        b.Draw(
            Game1.staminaRect,
            new Rectangle(
                0,
                0,
                Game1.graphics.GraphicsDevice.Viewport.Width,
                Game1.graphics.GraphicsDevice.Viewport.Height
            ),
            Color.Black
        );
    }

    public override void makeChangesToLocation()
    {
        // 等待画面不做任何世界改动；原版 makeChangesToLocation 只在换日结束时由主机调用，
        // 本事件若走到这里说明等待被异常结束（服务侧只置 null，不会调用本方法），保持 no-op。
    }
}
