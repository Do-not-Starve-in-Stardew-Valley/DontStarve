using DontStarve.Player.Stats;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player;

internal interface IStat
{
    void Init(IModHelper helper, SurvivalStateTimeOwner<Farmer> survivalTime);
}
