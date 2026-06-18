using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace DontStarve.Display;

internal interface ITimeRelatedUIElement
{
    void Init(IModHelper helper) { }
    void Render(RenderingHudEventArgs e, UIRenderContext uiContext);

    // 预留给需要内部分钟节奏的 UI；普通 HUD 绘制不要为了显示而注册时间回调。
    void Update(long time);
    void Sync(long time, long delta);
    void Load(IModHelper helper);
    void Save(IModHelper helper);
}
