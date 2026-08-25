#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity.Audio;

internal static class ShadowCreatureSfxPolicy
{
    internal static int Priority(ShadowCreatureSfxCue cue) => cue switch
    {
        ShadowCreatureSfxCue.Death => 6,
        ShadowCreatureSfxCue.Hurt => 5,
        ShadowCreatureSfxCue.AttackDull => 4,
        ShadowCreatureSfxCue.AttackSharp => 4,
        ShadowCreatureSfxCue.Attack => 4,
        ShadowCreatureSfxCue.Taunt => 3,
        ShadowCreatureSfxCue.Chase => 2,
        ShadowCreatureSfxCue.Idle => 1,
        _ => 0,
    };

    internal static ShadowCreatureSfxCue SelectAttackCue(
        ShadowCreatureSpecies species,
        double healthRatio
    )
    {
        if (!double.IsFinite(healthRatio))
            throw new ArgumentOutOfRangeException(nameof(healthRatio));
        return species == ShadowCreatureSpecies.CreeperFear
            ? healthRatio < 0.5d
                ? ShadowCreatureSfxCue.AttackSharp
                : ShadowCreatureSfxCue.AttackDull
            : ShadowCreatureSfxCue.Attack;
    }

    internal static bool IsCueAllowedForHarmlessProjection(ShadowCreatureSfxCue cue) =>
        cue is ShadowCreatureSfxCue.Idle or ShadowCreatureSfxCue.Chase;

    internal static bool IsCueAvailableForSpecies(
        ShadowCreatureSpecies species,
        ShadowCreatureSfxCue cue
    )
    {
        if (species == ShadowCreatureSpecies.CreeperFear)
            return cue != ShadowCreatureSfxCue.Attack;
        return cue is not ShadowCreatureSfxCue.AttackDull
            and not ShadowCreatureSfxCue.AttackSharp;
    }

    internal static string CueId(
        ShadowCreatureSpecies species,
        ShadowCreatureSfxCue cue
    )
    {
        var group = species == ShadowCreatureSpecies.CreeperFear
            ? "creeper-fear"
            : "terrorbeak";
        var suffix = cue switch
        {
            ShadowCreatureSfxCue.AttackDull => "attack-dull",
            ShadowCreatureSfxCue.AttackSharp => "attack-sharp",
            _ => cue.ToString().ToLowerInvariant(),
        };
        return string.Concat("sanity.cue.", group, ".", suffix);
    }

    internal static (double Minimum, double Maximum) GetCadenceRange(
        ShadowCreatureSpecies species,
        ShadowCreatureSfxCadenceState state,
        bool initial
    )
    {
        return (species, state, initial) switch
        {
            (ShadowCreatureSpecies.CreeperFear, ShadowCreatureSfxCadenceState.Chase, true) => (0.25d, 0.75d),
            (ShadowCreatureSpecies.CreeperFear, ShadowCreatureSfxCadenceState.Chase, false) => (5.5d, 7d),
            (ShadowCreatureSpecies.CreeperFear, ShadowCreatureSfxCadenceState.Idle, true) => (1.8d, 3.2d),
            (ShadowCreatureSpecies.CreeperFear, ShadowCreatureSfxCadenceState.Idle, false) => (6d, 8.5d),
            (ShadowCreatureSpecies.Terrorbeak, ShadowCreatureSfxCadenceState.Chase, true) => (0.25d, 0.75d),
            (ShadowCreatureSpecies.Terrorbeak, ShadowCreatureSfxCadenceState.Chase, false) => (6.5d, 8.5d),
            (ShadowCreatureSpecies.Terrorbeak, ShadowCreatureSfxCadenceState.Idle, true) => (2d, 3.5d),
            (ShadowCreatureSpecies.Terrorbeak, ShadowCreatureSfxCadenceState.Idle, false) => (8d, 11d),
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
    }
}
