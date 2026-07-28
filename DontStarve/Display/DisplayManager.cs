using System.Collections.Generic;
using DontStarve.Display.UIElements;
using DontStarve.Interface;
using DontStarve.Player.Stats.Sanity;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Display;

internal static class DisplayManager
{
    // HUD 元素只负责读状态和绘制，不在 RenderingHud 里修改 Hunger/Sanity 或写存档。
    private static List<INonTimeRelatedUIElement> nonTimeRelatedUIElements = new();

    private static readonly List<ITimeRelatedUIElement> timeRelatedUIElements =
        new List<ITimeRelatedUIElement>();

    internal static void Initialize(
        IModHelper helper,
        ITimeAPI timeApi,
        ISanitySystemState sanitySystemState
    )
    {
        nonTimeRelatedUIElements =
            new List<INonTimeRelatedUIElement>
            {
                new HungerBar(),
                new SanityBar(sanitySystemState),
                new FoodTooltip(sanitySystemState),
            };
        foreach (var e in nonTimeRelatedUIElements)
            e.Init(helper);
        foreach (var e in timeRelatedUIElements)
            e.Init(helper);

        helper.Events.Display.RenderingHud += (_, e) =>
        {
            if (!Context.IsWorldReady || Game1.CurrentEvent != null)
                return;

            // RenderingHud 使用 UI viewport 坐标；不要混用世界坐标或 Game1.viewport。
            var uiContext = new UIRenderContext(helper, e);
            foreach (var el in nonTimeRelatedUIElements)
                el.Render(e, uiContext);
            foreach (var el in timeRelatedUIElements)
                el.Render(e, uiContext);
        };

        if (timeRelatedUIElements.Count > 0)
        {
            timeApi.OnUpdate.Add(Update);
            timeApi.OnSync.Add(Sync);

            helper.Events.GameLoop.SaveLoaded += (_, _) => Load(helper);
            helper.Events.GameLoop.Saving += (_, _) => Save(helper);
        }
    }

    private static void Update(long time)
    {
        foreach (var e in timeRelatedUIElements)
            e.Update(time);
    }

    private static void Sync(long time, long delta)
    {
        foreach (var e in timeRelatedUIElements)
            e.Sync(time, delta);
    }

    private static void Load(IModHelper helper)
    {
        foreach (var e in timeRelatedUIElements)
            e.Load(helper);
    }

    private static void Save(IModHelper helper)
    {
        foreach (var e in timeRelatedUIElements)
            e.Save(helper);
    }
}
