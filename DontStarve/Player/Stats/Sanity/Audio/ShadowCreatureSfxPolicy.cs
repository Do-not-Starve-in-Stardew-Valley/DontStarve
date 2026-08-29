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
        ShadowCreatureSfxCue.HurtDull => 5,
        ShadowCreatureSfxCue.HurtSharp => 5,
        ShadowCreatureSfxCue.Attack => 4,
        ShadowCreatureSfxCue.Taunt => 3,
        ShadowCreatureSfxCue.Chase => 2,
        ShadowCreatureSfxCue.Idle => 1,
        _ => 0,
    };

    internal static ShadowCreatureSfxCue SelectAttackCue(ShadowCreatureSpecies species) =>
        ShadowCreatureSfxCue.Attack;

    internal static ShadowCreatureSfxCue SelectHurtCue(
        ShadowCreatureSpecies species,
        ShadowCreatureSfxHitSource source
    ) => species == ShadowCreatureSpecies.CreeperFear
        ? source is ShadowCreatureSfxHitSource.Sword or ShadowCreatureSfxHitSource.Dagger
            ? ShadowCreatureSfxCue.HurtSharp
            : ShadowCreatureSfxCue.HurtDull
        : ShadowCreatureSfxCue.Hurt;

    internal static bool IsCueAllowedForHarmlessProjection(ShadowCreatureSfxCue cue) =>
        cue is ShadowCreatureSfxCue.Idle or ShadowCreatureSfxCue.Chase;

    internal static bool IsCueAvailableForSpecies(
        ShadowCreatureSpecies species,
        ShadowCreatureSfxCue cue
    )
    {
        if (species == ShadowCreatureSpecies.CreeperFear)
            return cue is ShadowCreatureSfxCue.Idle
                or ShadowCreatureSfxCue.Chase
                or ShadowCreatureSfxCue.Taunt
                or ShadowCreatureSfxCue.Attack
                or ShadowCreatureSfxCue.HurtDull
                or ShadowCreatureSfxCue.HurtSharp
                or ShadowCreatureSfxCue.Death;
        return cue is not ShadowCreatureSfxCue.HurtDull
            and not ShadowCreatureSfxCue.HurtSharp;
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
            ShadowCreatureSfxCue.HurtDull => "hurt-dull",
            ShadowCreatureSfxCue.HurtSharp => "hurt-sharp",
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
            (ShadowCreatureSpecies.CreeperFear, ShadowCreatureSfxCadenceState.Chase, true) => (0.5d, 1.5d),
            (ShadowCreatureSpecies.CreeperFear, ShadowCreatureSfxCadenceState.Chase, false) => (11d, 14d),
            (ShadowCreatureSpecies.CreeperFear, ShadowCreatureSfxCadenceState.Idle, true) => (1.8d, 3.2d),
            (ShadowCreatureSpecies.CreeperFear, ShadowCreatureSfxCadenceState.Idle, false) => (6d, 8.5d),
            (ShadowCreatureSpecies.Terrorbeak, ShadowCreatureSfxCadenceState.Chase, true) => (0.5d, 1.5d),
            (ShadowCreatureSpecies.Terrorbeak, ShadowCreatureSfxCadenceState.Chase, false) => (13d, 17d),
            (ShadowCreatureSpecies.Terrorbeak, ShadowCreatureSfxCadenceState.Idle, true) => (2d, 3.5d),
            (ShadowCreatureSpecies.Terrorbeak, ShadowCreatureSfxCadenceState.Idle, false) => (8d, 11d),
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
    }
}
