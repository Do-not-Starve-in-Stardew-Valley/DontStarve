using StardewModdingAPI;

namespace DontStarve.Player.Stats;

internal interface ITimeRelatedBehavior
{
    void Init(IModHelper helper) { }
    void Update(long time);
    void Sync(long time, long delta);
    void Load(IModHelper helper);
    void Save(IModHelper helper);
}
