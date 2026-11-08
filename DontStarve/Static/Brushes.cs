using DontStarve.Player.Stats.Hunger;
using DontStarve.Player.Stats.Sanity;
using Microsoft.Xna.Framework;
using StardewValley;

namespace DontStarve;

internal static class Brushes
{
    private static readonly Color fullSanityBrush = new(0xFF, 0xC7, 0x00);
    private static readonly Color emptySanityBrush = new(0xA9, 0xA9, 0xA9);
    // Hunger only changes fill height; keep its color independent of player state.
    internal static Color HungerBrush { get; } = new(0xCE, 0xAF, 0x3D);

    internal static Color SanityBrush
    {
        get
        {
            if (Game1.player == null)
            {
                return emptySanityBrush;
            }

            var percent = Game1.player.GetSanity() / Game1.player.GetMaxSanity();
            var lerpR = fullSanityBrush.R - emptySanityBrush.R;
            var lerpG = fullSanityBrush.G - emptySanityBrush.G;
            var lerpB = fullSanityBrush.B - emptySanityBrush.B;
            return new Color(
                emptySanityBrush.R + (int)(percent * lerpR),
                emptySanityBrush.G + (int)(percent * lerpG),
                emptySanityBrush.B + (int)(percent * lerpB)
            );
        }
    }

}
