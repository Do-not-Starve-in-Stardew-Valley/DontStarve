using StardewModdingAPI;

namespace DontStarve.Player.Stats;

internal interface ITimeRelatedBehavior
{
    void Init(IModHelper helper) { }

    // Update 接收内部分钟 tick；Sync 接收 Stardew 原生 TimeChanged 校正后的时间和 delta。
    // 实现类通常用正向 delta 补跑、负向 delta 增加 wait，避免时间回退后重复结算。
    void Update(long time);
    void Sync(long time, long delta);
    void Load(IModHelper helper);
    void Save(IModHelper helper);
}
