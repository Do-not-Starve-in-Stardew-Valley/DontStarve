using StardewModdingAPI;

namespace DontStarve.Player.Stats;

internal interface ITimeRelatedBehavior
{
    void Init(IModHelper helper) { }

    // Update 接收 TimeApi 独占发布的每个正向分钟；Sync 只接收校正边界和原始 delta。
    // 消费者不得正向补跑；回退时可增加各自持久化 wait，避免同一时间段重复结算。
    void Update(long time);
    void Sync(long time, long delta);
    void Load(IModHelper helper);
    void Save(IModHelper helper);
}
