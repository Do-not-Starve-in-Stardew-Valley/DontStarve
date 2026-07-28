using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace DontStarve.Display;

internal interface ITimeRelatedUIElement
{
    void Init(IModHelper helper) { }
    void Render(RenderingHudEventArgs e, UIRenderContext uiContext);

    // 预留给需要内部分钟节奏的 UI；普通 HUD 绘制不要为了显示而注册时间回调。
    // TimeApi 已独占正向补跑，Sync 只能处理回退、边界或游标重建，不能再次调用 Update。
    void Update(long time);
    void Sync(long time, long delta);
    void Load(IModHelper helper);
    void Save(IModHelper helper);
}
