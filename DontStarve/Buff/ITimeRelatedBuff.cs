using StardewModdingAPI;

namespace DontStarve.Buff;

internal interface ITimeRelatedBuff
{
    // 只用于真正需要内部分钟 tick 的 Buff；固定总量恢复类应优先用 active Buff 生命周期自行计量。
    void Update(long time);
    void Sync(long time, long delta);
    void Load(IModHelper helper);
    void Save(IModHelper helper);
}
