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
    private bool schedulingPaused;
    private double schedulingPausedAtSeconds;
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

    internal bool IsSchedulingPaused => schedulingPaused;

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
        // A state transition observed while the window is inactive starts its remaining delay from
        // that transition, rather than inheriting the old focus-loss timestamp.
        if (schedulingPaused)
            schedulingPausedAtSeconds = nowSeconds;
    }

    internal void Leave()
    {
        state = null;
        NextDueAtSeconds = double.PositiveInfinity;
    }

    internal void PauseScheduling(double nowSeconds)
    {
        if (schedulingPaused)
            return;

        schedulingPaused = true;
        schedulingPausedAtSeconds = double.IsFinite(nowSeconds) ? nowSeconds : 0d;
    }

    internal void ResumeScheduling(double nowSeconds)
    {
        if (!schedulingPaused)
            return;

        var resumedAtSeconds = double.IsFinite(nowSeconds) ? nowSeconds : schedulingPausedAtSeconds;
        var elapsed = resumedAtSeconds - schedulingPausedAtSeconds;
        if (
            double.IsFinite(elapsed)
            && elapsed > 0d
            && double.IsFinite(NextDueAtSeconds)
        )
        {
            var shiftedDue = NextDueAtSeconds + elapsed;
            NextDueAtSeconds = double.IsFinite(shiftedDue) ? shiftedDue : double.MaxValue;
        }

        schedulingPaused = false;
    }

    internal bool IsDue(double nowSeconds) =>
        !schedulingPaused
        && state.HasValue
        && double.IsFinite(nowSeconds)
        && nowSeconds >= NextDueAtSeconds;

    internal void DeferBecauseVoiceBusy()
    {
        // Deliberately leave NextDueAtSeconds unchanged: once the current voice ends, the caller
        // re-evaluates the same due event instead of creating a queue or adding another cooldown.
    }

    internal void MarkStarted(double startedAtSeconds)
    {
        if (schedulingPaused || !state.HasValue || !double.IsFinite(startedAtSeconds))
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
