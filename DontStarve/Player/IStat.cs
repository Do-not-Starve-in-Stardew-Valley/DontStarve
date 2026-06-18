using DontStarve.Interface;
using StardewModdingAPI;

namespace DontStarve.Player;

internal interface IStat
{
    void Init(IModHelper helper, ITimeAPI timeApi);
}
