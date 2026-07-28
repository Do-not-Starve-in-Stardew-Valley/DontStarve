using System;
using System.Collections.Generic;

namespace DontStarve.Interface;

/// <summary>
/// 项目内部分钟时间服务契约。消费者通过 ModEntry 传入的同一实例注册回调，不重新查询外部时间 API。
/// 正向同步由时间源独占发布 (oldTime, syncTime] 的逐分钟 OnUpdate，之后只发一次 OnSync；
/// 消费者不得在 OnSync 中再次补跑正向分钟。
/// </summary>
public interface ITimeAPI
{
    public long Time { get; }
    public List<Action<long>> OnLoad { get; }
    public List<Action<long>> OnUpdate { get; }
    public List<Action<long, long>> OnSync { get; }
}
