using DontStarve.Player.Hunger;
using DontStarve.Player.Sanity;
using Microsoft.Xna.Framework;
using StardewValley;

namespace DontStarve;

internal static class Brushes
{
    private static readonly Color fullSanityBrush = new(0xFF, 0xC7, 0x00);
    private static readonly Color fullHungerBrush = new(0xFF, 0xC7, 0x00);
    private static readonly Color emptySanityBrush = new(0xA9, 0xA9, 0xA9);
    private static readonly Color emptyHungerBrush = new(0xA9, 0xA9, 0xA9);

    internal static Color SanityBrush
    {
        get
        {
            if (Game1.player == null)
            {
                return emptySanityBrush;
            }

            var percent = Game1.player.getSanity() / Game1.player.getMaxSanity();
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

    internal static Color HungerBrush
    {
        get
        {
            if (Game1.player == null)
            {
                return emptyHungerBrush;
            }

            var percent = Game1.player.getHunger() / Game1.player.getMaxHunger();
            var lerpR = fullHungerBrush.R - emptyHungerBrush.R;
            var lerpG = fullHungerBrush.G - emptyHungerBrush.G;
            var lerpB = fullHungerBrush.B - emptyHungerBrush.B;
            return new Color(
                emptyHungerBrush.R + (int)(percent * lerpR),
                emptyHungerBrush.G + (int)(percent * lerpG),
                emptyHungerBrush.B + (int)(percent * lerpB)
            );
        }
    }
}
