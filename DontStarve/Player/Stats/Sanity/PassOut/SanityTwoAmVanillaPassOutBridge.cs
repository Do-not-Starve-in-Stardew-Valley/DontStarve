#nullable enable

using System;
using Microsoft.Xna.Framework;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.PassOut;

/// <summary>
/// Narrow local bridge for the NonLethal outcome. It mirrors only the vanilla bed destination and
/// `PassOutNewDay` handoff; the vanilla `performPassoutWarp` fee/mail block is intentionally not
/// called because entering the special chain exempts this pass-out from that charge.
/// </summary>
internal static class SanityTwoAmVanillaPassOutBridge
{
    internal static void BeginNoFeePassOut(Farmer farmer)
    {
        if (farmer is null || !farmer.IsLocalPlayer || Game1.currentLocation is null)
            return;

        farmer.CanMove = false;
        farmer.FarmerSprite.setCurrentSingleFrame(5, 3000);
        farmer.FarmerSprite.PauseForSingleAnimation = true;

        var home = Utility.getHomeOfFarmer(farmer);
        var locationName = home.NameOrUniqueName;
        var bedPoint = home.GetPlayerBedSpot();
        farmer.lastSleepLocation.Value = locationName;
        farmer.lastSleepPoint.Value = bedPoint;

        void Complete()
        {
            farmer.Position = new Vector2(bedPoint.X * 64f, bedPoint.Y * 64f);
            Game1.PassOutNewDay();
        }

        if (farmer.currentLocation.NameOrUniqueName == locationName)
        {
            Complete();
            return;
        }

        var request = Game1.getLocationRequest(locationName);
        request.OnWarp += Complete;
        Game1.warpFarmer(request, bedPoint.X, bedPoint.Y, 2);
    }

    /// <summary>
    /// Pending-darkness (Default death) outcome: no home warp — the farmer stays on the collapse
    /// tile and hands the day change straight to the vanilla PassOutNewDay bridge. The wake
    /// target (Hospital/revive tile) is written by the caller AFTER this returns, because
    /// PassOutNewDay itself overwrites lastSleepLocation/lastSleepPoint with the current tile.
    /// Single-player needs <c>passedOut</c> to pass Game1.NewDay's fade gate
    /// (<c>isInBed || passedOut</c>, Game1.cs:10372) — the vanilla multiplayer branch of
    /// PassOutNewDay sets the same flag itself, so this mirrors vanilla semantics without
    /// inventing a third path.
    /// </summary>
    internal static void BeginNoFeeNewDayInPlace(Farmer farmer)
    {
        if (farmer is null || !farmer.IsLocalPlayer || Game1.currentLocation is null)
            return;

        farmer.CanMove = false;
        farmer.FarmerSprite.setCurrentSingleFrame(5, 3000);
        farmer.FarmerSprite.PauseForSingleAnimation = true;
        farmer.passedOut = true;
        Game1.PassOutNewDay();
    }
}
