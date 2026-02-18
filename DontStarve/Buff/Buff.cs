using DontStarve.Integration;
using StardewModdingAPI;

namespace DontStarve.Buff;

internal static class Buff
{
    internal static void init(IModHelper helper)
    {
        Electric.init(helper);

        helper.Events.GameLoop.GameLaunched += (_, _) =>
        {
            var timeApi = helper.ModRegistry.GetApi<TimeApi>("Yurin.MinuteTimeHelper");
            if (timeApi == null)
                return;
            timeApi.OnUpdate.Add(update);
            timeApi.OnSync.Add(sync);
        };

        helper.Events.GameLoop.SaveLoaded += (_, _) => load(helper);
        helper.Events.GameLoop.Saving += (_, _) => save(helper);
    }

    private static void update(ulong time)
    {
        Health.update((long)time);
        Stamina.update((long)time);
        Sanity.update((long)time);
    }

    private static void sync(ulong time, long delta)
    {
        Health.sync((long)time, delta);
        Stamina.sync((long)time, delta);
        Sanity.sync((long)time, delta);
    }

    private static void load(IModHelper helper)
    {
        Health.load(helper);
        Stamina.load(helper);
        Sanity.load(helper);
    }

    private static void save(IModHelper helper)
    {
        Health.save(helper);
        Stamina.save(helper);
        Sanity.save(helper);
    }
}
