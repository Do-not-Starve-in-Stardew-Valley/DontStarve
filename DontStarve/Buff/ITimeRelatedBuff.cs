using StardewModdingAPI;

namespace DontStarve.Buff;

internal interface ITimeRelatedBuff
{
    void Update(long time);
    void Sync(long time, long delta);
    void Load(IModHelper helper);
    void Save(IModHelper helper);
}
