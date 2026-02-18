using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Buff.Buffs;

internal class ElectricityAppendBuff : INonTimeRelatedBuff
{
    private const string ELECTRICITY_APPEND_BUFF_ID = "DS_BUFF_ELECTRICITY_APPEND";
    private const float BASE_BONUS = 0.5f;
    private const float WEATHER_BONUS = 1.0f;
    private float appliedBonus;

    public void Init(IModHelper helper)
    {
        helper.Events.GameLoop.OneSecondUpdateTicked += (_, _) => Update();
    }

    private void Update()
    {
        var player = Game1.player;
        if (player == null)
            return;

        var hasBuff = player.hasBuff(ELECTRICITY_APPEND_BUFF_ID);
        if (!hasBuff && appliedBonus == 0f)
            return;

        var hasWeatherBuff =
            hasBuff
            && (Game1.isRaining || Game1.isGreenRain || Game1.isLightning || Game1.isSnowing);

        var totalBonus = hasBuff ? BASE_BONUS + (hasWeatherBuff ? WEATHER_BONUS : 0f) : 0f;

        var bonusDelta = totalBonus - appliedBonus;
        if (bonusDelta != 0f)
        {
            player
                .buffs.GetValues()
                .AttackMultiplier.Set(player.buffs.AttackMultiplier + bonusDelta);
            appliedBonus = totalBonus;
        }
    }
}
