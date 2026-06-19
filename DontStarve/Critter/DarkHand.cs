using Microsoft.Xna.Framework;
using StardewValley;

namespace DontStarve.Critter;

/// <summary>
/// 低理智幻觉 critter；玩家靠近到 1 格内时通过 update 返回 true 让原版移除它。
/// </summary>
public class DarkHand : StardewValley.BellsAndWhistles.Critter
{
    public DarkHand(Vector2 position)
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
