#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.Audio;

internal enum ShadowCreatureSfxCadenceState
{
    Idle,
    Chase,
}

internal sealed class ShadowCreatureSfxCadence
{
    private readonly ShadowCreatureSpecies species;
    private readonly IShadowCreatureSfxRandom random;
    private ShadowCreatureSfxCadenceState? state;
    private long revision;
    internal ShadowCreatureSfxCadence(
        ShadowCreatureSpecies species,
        IShadowCreatureSfxRandom random
    )
    {
        this.species = species;
        this.random = random ?? throw new ArgumentNullException(nameof(random));
    }

    internal ShadowCreatureSfxCadenceState? State => state;

    internal long Revision => revision;

    internal double NextDueAtSeconds { get; private set; }

    internal void Enter(
        ShadowCreatureSfxCadenceState nextState,
        double nowSeconds,
        long stateRevision
    )
    {
        if (!double.IsFinite(nowSeconds))
            throw new ArgumentOutOfRangeException(nameof(nowSeconds));
        state = nextState;
        revision = stateRevision;
        NextDueAtSeconds = nowSeconds + Sample(initial: true);
    }

    internal void Leave()
    {
        state = null;
        NextDueAtSeconds = double.PositiveInfinity;
    }

    internal bool IsDue(double nowSeconds) =>
        state.HasValue && double.IsFinite(nowSeconds) && nowSeconds >= NextDueAtSeconds;

    internal void DeferBecauseVoiceBusy()
    {
        // Deliberately leave NextDueAtSeconds unchanged: once the current voice ends, the caller
        // re-evaluates the same due event instead of creating a queue or adding another cooldown.
    }

    internal void MarkStarted(double startedAtSeconds)
    {
        if (!state.HasValue || !double.IsFinite(startedAtSeconds))
            return;
        NextDueAtSeconds = startedAtSeconds + Sample(initial: false);
    }

    private double Sample(bool initial)
    {
        var range = ShadowCreatureSfxPolicy.GetCadenceRange(
            species,
            state ?? throw new InvalidOperationException("Cadence state is not active."),
            initial
        );
        var sample = random.NextDouble(range.Minimum, range.Maximum);
        if (!double.IsFinite(sample))
            return range.Minimum;
        return Math.Clamp(sample, range.Minimum, range.Maximum);
    }
}
