#nullable enable

using DontStarve.Player.Stats.Sanity.PassOut;
using Xunit;

namespace DontStarve.Tests.Sanity.PassOut;

public sealed class LateNightBehaviorEvidenceTests
{
    [Fact]
    public void Late_night_clock_rollback_latches_external_owner_evidence()
    {
        var evidence = new LateNightBehaviorEvidence();
        evidence.Reset(2550);

        Assert.True(evidence.Observe(150, worldReady: true, newDay: false));
        Assert.True(evidence.IsObserved);
        Assert.Equal(
            "passout.compatibility.late-night.behavior-clock-rollback",
            evidence.StableReason
        );
    }

    [Theory]
    [InlineData(2550, 600, false, false)]
    [InlineData(2550, 150, true, false)]
    [InlineData(2300, 150, false, false)]
    [InlineData(2550, 400, false, false)]
    public void Normal_or_new_day_transitions_do_not_latch_external_owner(
        int previous,
        int current,
        bool newDay,
        bool expected
    )
    {
        var evidence = new LateNightBehaviorEvidence();
        evidence.Reset(previous);

        Assert.Equal(expected, evidence.Observe(current, worldReady: true, newDay));
        Assert.Equal(expected, evidence.IsObserved);
    }

    [Fact]
    public void Evidence_is_session_local_and_duplicate_observation_is_idempotent()
    {
        var evidence = new LateNightBehaviorEvidence();
        evidence.Reset(2550);

        Assert.True(evidence.Observe(150, worldReady: true, newDay: false));
        Assert.True(evidence.Observe(160, worldReady: true, newDay: false));
        evidence.Clear();

        Assert.False(evidence.IsObserved);
        Assert.Equal(string.Empty, evidence.StableReason);
    }

    [Fact]
    public void Runtime_route_can_consume_behavior_evidence_without_a_manifest_identity()
    {
        var source = System.IO.File.ReadAllText(
            System.IO.Path.Combine(
                System.AppContext.BaseDirectory,
                "Contracts",
                "PassOut",
                "SmapiSanityTwoAmSpecialDeathService.cs"
            )
        );

        Assert.Contains("lateNightBehaviorEvidence", source, System.StringComparison.Ordinal);
        Assert.Contains("ObserveTimeChanged", source, System.StringComparison.Ordinal);
    }
}
