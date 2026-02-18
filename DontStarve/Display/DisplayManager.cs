using System.Collections.Generic;
using DontStarve.Display.UIElements;
using DontStarve.Integration;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Display;

internal static class DisplayManager
{
    private static readonly List<INonTimeRelatedUIElement> nonTimeRelatedUIElements =
        new List<INonTimeRelatedUIElement> { new HungerBar(), new SanityBar(), new FoodTooltip() };

    private static readonly List<ITimeRelatedUIElement> timeRelatedUIElements =
        new List<ITimeRelatedUIElement>();

    internal static void Initialize(IModHelper helper)
    {
        foreach (var e in nonTimeRelatedUIElements)
            e.Init(helper);
        foreach (var e in timeRelatedUIElements)
            e.Init(helper);

        helper.Events.Display.RenderingHud += (_, e) =>
        {
            if (!Context.IsWorldReady || Game1.CurrentEvent != null)
                return;
            var uiContext = new UIRenderContext(helper, e);
            foreach (var el in nonTimeRelatedUIElements)
                el.Render(e, uiContext);
            foreach (var el in timeRelatedUIElements)
                el.Render(e, uiContext);
        };

        if (timeRelatedUIElements.Count > 0)
        {
            helper.Events.GameLoop.GameLaunched += (_, _) =>
            {
                var timeApi = helper.ModRegistry.GetApi<TimeApi>("Yurin.MinuteTimeHelper");
                if (timeApi == null)
                    return;
                timeApi.OnUpdate.Add(Update);
                timeApi.OnSync.Add(Sync);
            };

            helper.Events.GameLoop.SaveLoaded += (_, _) => Load(helper);
            helper.Events.GameLoop.Saving += (_, _) => Save(helper);
        }
    }

    private static void Update(ulong time)
    {
        foreach (var e in timeRelatedUIElements)
            e.Update((long)time);
    }

    private static void Sync(ulong time, long delta)
    {
        foreach (var e in timeRelatedUIElements)
            e.Sync((long)time, delta);
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
