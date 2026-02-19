using Microsoft.Xna.Framework;
using StardewValley;

namespace DontStarve.Critter;

public class Eye : StardewValley.BellsAndWhistles.Critter
{
    public Eye(Vector2 position)
    {
        this.position = position;
        startingPosition = position;
        sprite = new AnimatedSprite(critterTexture, baseFrame, 32, 32) { loop = true };
    }

    public override bool update(GameTime time, GameLocation environment)
    {
        foreach (var farmer in environment.farmers)
            if (Util.Distance(farmer.Position / Game1.tileSize, position / Game1.tileSize) <= 1)
                return true;

        return false;
    }
}
