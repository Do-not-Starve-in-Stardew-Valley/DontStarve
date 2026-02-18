using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace DontStarve.Display;

internal interface INonTimeRelatedUIElement
{
    void Init(IModHelper helper);
    void Render(RenderingHudEventArgs e, UIRenderContext uiContext);
}
