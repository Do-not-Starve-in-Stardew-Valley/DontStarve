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
    // 复用 HUD 请求缓冲区，避免每帧为可见条创建集合。
    private static readonly List<HudBarLayoutRequest> hudBarLayoutRequests = new(4);

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
            };
        // 保留 FoodTooltip 源码作为可逆的旧实现，但当前属性只由原版集中 tooltip 显示。
        foreach (var e in nonTimeRelatedUIElements)
            e.Init(helper);
        foreach (var e in timeRelatedUIElements)
            e.Init(helper);

        helper.Events.Display.RenderingHud += (_, e) =>
        {
            if (!Context.IsWorldReady || Game1.CurrentEvent != null)
                return;

            // RenderingHud 使用 UI viewport 坐标；不要混用世界坐标或 Game1.viewport。
            hudBarLayoutRequests.Clear();
            foreach (var element in nonTimeRelatedUIElements)
            {
                if (element is IHudStatusBarProvider provider)
                    AddHudBarLayoutRequest(provider);
            }
            foreach (var element in timeRelatedUIElements)
            {
                if (element is IHudStatusBarProvider provider)
                    AddHudBarLayoutRequest(provider);
            }

            var viewport = Game1.uiViewport;
            var player = Game1.player;
            var vanillaLayout = HudDisplayRules.CreateVanillaLayout(
                viewport.Width,
                viewport.Height,
                Game1.showingHealth,
                player.maxHealth,
                player.MaxStamina
            );
            foreach (var el in nonTimeRelatedUIElements)
            {
                var uiContext = CreateUIRenderContext(
                    helper,
                    e,
                    vanillaLayout,
                    el as IHudStatusBarProvider
                );
                el.Render(e, uiContext);
            }
            foreach (var el in timeRelatedUIElements)
            {
                var uiContext = CreateUIRenderContext(
                    helper,
                    e,
                    vanillaLayout,
                    el as IHudStatusBarProvider
                );
                el.Render(e, uiContext);
            }
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

    private static void AddHudBarLayoutRequest(IHudStatusBarProvider provider)
    {
        if (provider.TryGetLayoutRequest(out var request))
            hudBarLayoutRequests.Add(request);
    }

    private static UIRenderContext CreateUIRenderContext(
        IModHelper helper,
        StardewModdingAPI.Events.RenderingHudEventArgs e,
        VanillaHudLayout vanillaLayout,
        IHudStatusBarProvider provider
    )
    {
        HudBounds? statusBarBounds = null;
        if (
            provider != null
            && HudDisplayRules.TryGetBarBounds(
                vanillaLayout,
                hudBarLayoutRequests,
                provider.HudBarId,
                out var bounds
            )
        )
        {
            statusBarBounds = bounds;
        }

        return new UIRenderContext(helper, e, statusBarBounds);
    }
}
