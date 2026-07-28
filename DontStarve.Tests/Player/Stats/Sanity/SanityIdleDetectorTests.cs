using DontStarve.Player.Stats.Sanity.Visual;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityIdleDetectorTests
{
    [Fact]
    public void Real_elapsed_crosses_only_at_exact_one_second()
    {
        var detector = new SanityIdleDetector();

        var before = detector.Observe(TimeSpan.FromMilliseconds(999), Eligible());
        var exact = detector.Observe(TimeSpan.FromMilliseconds(1), Eligible());

        Assert.False(before.ThresholdReached);
        Assert.Equal(TimeSpan.FromMilliseconds(999), before.EligibleElapsed);
        Assert.True(exact.ThresholdReached);
        Assert.Equal(SanityIdleDetector.IdleThreshold, exact.EligibleElapsed);
    }

    [Theory]
    [InlineData("world")]
    [InlineData("owner")]
    [InlineData("can-move")]
    [InlineData("busy")]
    [InlineData("moving")]
    [InlineData("directions")]
    [InlineData("using-tool")]
    [InlineData("hit")]
    [InlineData("single-animation")]
    [InlineData("basic-animation")]
    [InlineData("menu")]
    [InlineData("event")]
    [InlineData("warp")]
    public void Every_interruption_resets_accumulated_idle_time(string interruption)
    {
        var detector = new SanityIdleDetector();
        detector.Observe(TimeSpan.FromMilliseconds(999), Eligible());

        var interrupted = detector.Observe(
            TimeSpan.FromMilliseconds(1),
            Interrupted(interruption)
        );

        Assert.False(interrupted.ThresholdReached);
        Assert.Equal(TimeSpan.Zero, interrupted.EligibleElapsed);
        Assert.StartsWith("idle.interrupted.", interrupted.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Recovery_requires_a_fresh_full_second()
    {
        var detector = new SanityIdleDetector();
        detector.Observe(TimeSpan.FromMilliseconds(800), Eligible());
        detector.Observe(TimeSpan.FromMilliseconds(1), Interrupted("moving"));

        Assert.False(detector.Observe(TimeSpan.FromMilliseconds(999), Eligible()).ThresholdReached);
        Assert.True(detector.Observe(TimeSpan.FromMilliseconds(1), Eligible()).ThresholdReached);
    }

    [Fact]
    public void Negative_elapsed_fails_closed_and_resets()
    {
        var detector = new SanityIdleDetector();
        detector.Observe(TimeSpan.FromMilliseconds(900), Eligible());

        var result = detector.Observe(TimeSpan.FromTicks(-1), Eligible());

        Assert.False(result.ThresholdReached);
        Assert.Equal(TimeSpan.Zero, result.EligibleElapsed);
        Assert.Equal("idle.elapsed-invalid", result.Reason);
    }

    [Fact]
    public void Reset_is_idempotent()
    {
        var detector = new SanityIdleDetector();
        detector.Observe(SanityIdleDetector.IdleThreshold, Eligible());

        detector.Reset("idle.event-started");
        detector.Reset("idle.event-started");

        Assert.Equal(TimeSpan.Zero, detector.Snapshot.EligibleElapsed);
        Assert.False(detector.Snapshot.ThresholdReached);
        Assert.Equal("idle.event-started", detector.Snapshot.Reason);
    }

    [Fact]
    public void Owned_presentation_pause_does_not_interrupt_but_other_guards_still_do()
    {
        var detector = new SanityIdleDetector();
        var owned = Eligible() with
        {
            PauseForSingleAnimation = true,
            IsPlayingBasicAnimation = false,
            OwnPresentationActive = true,
        };

        Assert.True(
            detector.Observe(
                SanityIdleDetector.IdleThreshold,
                owned
            ).ThresholdReached
        );
        Assert.False(
            detector.Observe(
                TimeSpan.Zero,
                owned with { IsUsingTool = true }
            ).ThresholdReached
        );
    }

    private static SanityIdleObservation Eligible()
    {
        return new SanityIdleObservation(
            WorldReady: true,
            IsLocalOwner: true,
            CanMove: true,
            IsBusy: false,
            IsMoving: false,
            HasMovementDirections: false,
            IsUsingTool: false,
            IsInHitRecovery: false,
            PauseForSingleAnimation: false,
            IsPlayingBasicAnimation: true,
            IsMenuOpen: false,
            IsEventOrCutsceneActive: false,
            IsWarping: false,
            OwnPresentationActive: false
        );
    }

    private static SanityIdleObservation Interrupted(string interruption)
    {
        var value = Eligible();
        return interruption switch
        {
            "world" => value with { WorldReady = false },
            "owner" => value with { IsLocalOwner = false },
            "can-move" => value with { CanMove = false },
            "busy" => value with { IsBusy = true },
            "moving" => value with { IsMoving = true },
            "directions" => value with { HasMovementDirections = true },
            "using-tool" => value with { IsUsingTool = true },
            "hit" => value with { IsInHitRecovery = true },
            "single-animation" => value with { PauseForSingleAnimation = true },
            "basic-animation" => value with { IsPlayingBasicAnimation = false },
            "menu" => value with { IsMenuOpen = true },
            "event" => value with { IsEventOrCutsceneActive = true },
            "warp" => value with { IsWarping = true },
            _ => throw new ArgumentOutOfRangeException(nameof(interruption)),
        };
    }
}
