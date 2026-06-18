using System;
using System.Collections.Generic;

namespace DontStarve.Interface;

/// <summary>
/// Internal minute-time service contract.
/// </summary>
public interface ITimeAPI
{
    public long Time { get; }
    public List<Action<long>> OnLoad { get; }
    public List<Action<long>> OnUpdate { get; }
    public List<Action<long, long>> OnSync { get; }
}
