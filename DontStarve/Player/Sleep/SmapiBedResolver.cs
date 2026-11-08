#nullable enable

using System;
using System.Globalization;
using StardewValley;

namespace DontStarve.Player.Sleep;

internal readonly record struct SleepBedFacts(
    string BedId,
    double BedSanityMultiplier,
    double HungerCostPerSanity
);

/// <summary>
/// 当前阶段只接受引擎能直接观察到的地点/玩家位置作为床身份；无法得到稳定事实时
/// 返回 false，第三方睡眠入口不会被猜测成可结算的自定义睡眠。
/// </summary>
internal sealed class SmapiBedResolver
{
    internal bool TryResolve(
        Farmer farmer,
        bool requireInBed,
        out SleepBedFacts facts,
        out string reason
    )
    {
        facts = default;
        if (farmer is null)
        {
            reason = "sleep-bed-player-is-unavailable";
            return false;
        }
        var location = farmer.currentLocation;
        if (location is null || string.IsNullOrWhiteSpace(location.NameOrUniqueName))
        {
            reason = "sleep-bed-location-is-unavailable";
            return false;
        }
        if (requireInBed && !farmer.isInBed.Value)
        {
            reason = "sleep-bed-player-is-not-in-bed";
            return false;
        }

        var tile = farmer.TilePoint;
        var bedId = string.Concat(
            location.NameOrUniqueName,
            ":",
            tile.X.ToString(CultureInfo.InvariantCulture),
            ":",
            tile.Y.ToString(CultureInfo.InvariantCulture)
        );
        if (bedId.Length > 128)
        {
            reason = "sleep-bed-id-is-too-long";
            return false;
        }

        facts = new SleepBedFacts(bedId, 1d, 1d);
        reason = "sleep-bed-facts-resolved";
        return true;
    }
}
