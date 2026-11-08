#nullable enable

using System;
using Microsoft.Xna.Framework;
using StardewValley;

namespace DontStarve.Player.Stats.Hunger;

/// <summary>
/// Host-only SMAPI 边界：直接提交 Hunger 结算后的 health，不调用 Farmer.takeDamage。
/// </summary>
internal sealed class SmapiHungerDamageExecutor
{
    private readonly Func<HungerDamageMode> modeProvider;
    private readonly Action<Farmer>? lethalDamageHandler;

    internal SmapiHungerDamageExecutor(
        HungerDamageMode mode,
        Action<Farmer>? lethalDamageHandler = null
    )
    {
        modeProvider = () => mode;
        this.lethalDamageHandler = lethalDamageHandler;
    }

    internal SmapiHungerDamageExecutor(
        Func<HungerDamageMode> modeProvider,
        Action<Farmer>? lethalDamageHandler = null
    )
    {
        this.modeProvider = modeProvider ?? throw new ArgumentNullException(nameof(modeProvider));
        this.lethalDamageHandler = lethalDamageHandler;
    }

    internal bool TryApply(Farmer player, out HungerDamageResult result)
    {
        var service = new HungerDamageService(modeProvider());
        result = service.Resolve(player.health, player.maxHealth);
        if (result.Damage <= 0)
            return false;

        player.health = result.RemainingHealth;
        if (player.health <= 0)
            lethalDamageHandler?.Invoke(player);
        // Use Stardew's native floating number presentation only. Hunger damage must not enter
        // Farmer.takeDamage: that path adds sound, invincibility, attack attribution and combat
        // side effects which are intentionally outside Hunger's direct health-loss contract.
        var standingPixel = player.StandingPixel;
        player.currentLocation?.debris.Add(
            new Debris(
                result.Damage,
                new Vector2(standingPixel.X + 8, standingPixel.Y),
                Color.Red,
                1f,
                player
            )
        );
        return true;
    }
}
