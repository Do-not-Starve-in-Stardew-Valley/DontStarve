using System;
using System.Collections.Generic;

namespace DontStarve.Integration;

public interface TimeApi
{
    public ulong Time { get; }
    public List<Action<ulong>> OnLoad { get; }
    public List<Action<ulong>> OnUpdate { get; }
    public List<Action<ulong, long>> OnSync { get; }
}
