using System;
using System.Collections.Generic;

namespace DontStarve.Interface;

/// <summary>
/// 项目内部分钟时间服务契约。消费者通过 ModEntry 传入的同一实例注册回调，不重新查询外部时间 API。
/// </summary>
public interface ITimeAPI
{
    public long Time { get; }
    public List<Action<long>> OnLoad { get; }
    public List<Action<long>> OnUpdate { get; }
    public List<Action<long, long>> OnSync { get; }
}
