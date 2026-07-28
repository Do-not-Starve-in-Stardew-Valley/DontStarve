using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using MineShaftLocation = StardewValley.Locations.MineShaft;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

/// <summary>
/// 根据矿洞、火山和危险层级降低理智；只影响当前所在地点。
/// </summary>
internal class MineShaft : ITimeRelatedBehavior
{
    private const string SAVE_KEY = "DontStarve.Sanity.MineShaft";
    private long wait;

    public void Update(long _)
    {
        if (wait > 0)
        {
            wait--;
            return;
        }

        var player = Game1.player;
        if (player == null)
            return;

        var location = player.currentLocation;
        if (location == null)
            return;

        double value;
        if (location is MineShaftLocation mineShaft)
        {
            value = SanityBehaviorRules.CalculateMineLoss(
                new MineSanityContext(
                    MineSanityLocationKind.MineShaft,
                    mineShaft.mineLevel,
                    mineShaft.isDarkArea(),
                    mineShaft.isQuarryArea,
                    mineShaft.isSlimeArea,
                    mineShaft.isMonsterArea,
                    mineShaft.isDinoArea,
                    mineShaft.GetAdditionalDifficulty()
                )
            );
        }
        else if (location is VolcanoDungeon)
        {
            value = SanityBehaviorRules.CalculateMineLoss(
                new MineSanityContext(
                    MineSanityLocationKind.VolcanoDungeon,
                    0,
                    false,
                    false,
                    false,
                    false,
                    false,
                    0
                )
            );
        }
        else
        {
            value = 0;
        }

        if (value > 0)
            player.ChangeSanity(-value, SanityChangeSource.Mine);
    }

    public void Sync(long _, long delta)
    {
        if (delta < 0)
            wait += -delta;
    }

    public void Load(IModHelper helper)
    {
        var data = helper.Data.ReadSaveData<MineShaftData>(SAVE_KEY);
        wait = data?.Wait ?? 0;
    }

    public void Save(IModHelper helper)
    {
        helper.Data.WriteSaveData(SAVE_KEY, new MineShaftData { Wait = wait });
    }
}

internal class MineShaftData
{
    public long Wait { get; init; }
}
