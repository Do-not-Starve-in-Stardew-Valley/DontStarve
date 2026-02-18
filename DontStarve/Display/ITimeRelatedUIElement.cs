using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace DontStarve.Display;

internal interface ITimeRelatedUIElement
{
    void Init(IModHelper helper) { }
    void Render(RenderingHudEventArgs e, UIRenderContext uiContext);
    void Update(long time);
    void Sync(long time, long delta);
    void Load(IModHelper helper);
    void Save(IModHelper helper);
}
