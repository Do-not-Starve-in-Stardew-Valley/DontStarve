using System.Linq;
using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.Darkness;
using DontStarve.Player.Stats.Sanity.PassOut;
using Xunit;

namespace DontStarve.Tests.Sanity.PassOut;

public sealed class SanityTwoAmSpecialDeathTimelineTests
{
    private const string Session = "11111111111141118111111111111111";

    [Theory]
    [InlineData((int)DarknessDamageMode.Default, (int)SanityTwoAmSpecialDeathFlowKind.DefaultDeath)]
    [InlineData((int)DarknessDamageMode.NonLethal, (int)SanityTwoAmSpecialDeathFlowKind.NonLethalHome)]
    public void BeginStartsOwnerLocalBlackEventAndEnteredPrompt(int mode, int expectedKind)
    {
        var machine = new SanityTwoAmSpecialDeathStateMachine();

        var mutation = machine.Begin(Request((DarknessDamageMode)mode));

        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Applied, mutation.Status);
        Assert.Equal((SanityTwoAmSpecialDeathFlowKind)expectedKind, mutation.Snapshot!.Kind);
        Assert.Equal(SanityTwoAmSpecialDeathPhase.PromptEntered, mutation.Snapshot.Phase);
        Assert.Equal(
            new[]
            {
                SanityTwoAmSpecialDeathActionKind.BeginSpecialOverlay,
                SanityTwoAmSpecialDeathActionKind.ShowPromptEntered,
            },
            mutation.Actions.Select(action => action.Kind)
        );
    }

    [Fact]
    public void DefaultTimelineEmitsWarningHitDeathAndResolvedPromptInOrder()
    {
        var machine = new SanityTwoAmSpecialDeathStateMachine();
        var state = machine.Begin(Request(DarknessDamageMode.Default)).Snapshot!;

        var warning = Advance(machine, state, "warning");
        Assert.Equal(SanityTwoAmSpecialDeathPhase.PromptWarning, warning.Snapshot!.Phase);
        Assert.Equal(
            new[]
            {
                SanityTwoAmSpecialDeathActionKind.PlayWarningCue,
                SanityTwoAmSpecialDeathActionKind.ShowPromptWarning,
            },
            warning.Actions.Select(action => action.Kind)
        );

        var resolved = Advance(machine, warning.Snapshot, "resolved");
        Assert.Equal(SanityTwoAmSpecialDeathPhase.PromptResolved, resolved.Snapshot!.Phase);
        Assert.Equal(
            new[]
            {
                SanityTwoAmSpecialDeathActionKind.PlayHurtAndAttack,
                SanityTwoAmSpecialDeathActionKind.PlayDeathSound,
                SanityTwoAmSpecialDeathActionKind.ShowPromptResolved,
            },
            resolved.Actions.Select(action => action.Kind)
        );

        var outcome = Advance(machine, resolved.Snapshot, "outcome");
        Assert.Equal(SanityTwoAmSpecialDeathPhase.Completed, outcome.Snapshot!.Phase);
        Assert.Equal(
            new[]
            {
                SanityTwoAmSpecialDeathActionKind.EndSpecialOverlay,
                SanityTwoAmSpecialDeathActionKind.BeginPendingDarknessDeath,
            },
            outcome.Actions.Select(action => action.Kind)
        );
    }

    [Fact]
    public void NonLethalTimelineKeepsHurtAndAttackButOmitsOnlyDeathSound()
    {
        var machine = new SanityTwoAmSpecialDeathStateMachine();
        var state = machine.Begin(Request(DarknessDamageMode.NonLethal)).Snapshot!;

        var warning = Advance(machine, state, "warning");
        var resolved = Advance(machine, warning.Snapshot!, "resolved");
        var outcome = Advance(machine, resolved.Snapshot!, "outcome");

        Assert.Equal(
            new[]
            {
                SanityTwoAmSpecialDeathActionKind.PlayHurtAndAttack,
                SanityTwoAmSpecialDeathActionKind.ShowPromptResolved,
            },
            resolved.Actions.Select(action => action.Kind)
        );
        Assert.Contains(
            SanityTwoAmSpecialDeathActionKind.BeginNonLethalPassOut,
            outcome.Actions.Select(action => action.Kind)
        );
        Assert.DoesNotContain(
            SanityTwoAmSpecialDeathActionKind.PlayDeathSound,
            resolved.Actions.Select(action => action.Kind)
        );
    }

    [Fact]
    public void ReplayedPresentationSignalIsDuplicateAndCannotRepeatAudio()
    {
        var machine = new SanityTwoAmSpecialDeathStateMachine();
        var state = machine.Begin(Request(DarknessDamageMode.Default)).Snapshot!;

        var first = Advance(machine, state, "warning");
        var replay = machine.Signal(
            state.PlayerKey,
            state.CorrelationId,
            SanityTwoAmSpecialDeathSignal.AdvancePresentation,
            "warning"
        );

        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Applied, first.Status);
        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Duplicate, replay.Status);
        Assert.Equal(first.Snapshot, replay.Snapshot);
        Assert.Empty(replay.Actions);
    }

    private static SanityTwoAmSpecialDeathMutation Advance(
        SanityTwoAmSpecialDeathStateMachine machine,
        SanityTwoAmSpecialDeathSnapshot state,
        string signalId
    ) => machine.Signal(
        state.PlayerKey,
        state.CorrelationId,
        SanityTwoAmSpecialDeathSignal.AdvancePresentation,
        signalId
    );

    private static SanityTwoAmSpecialDeathStartRequest Request(
        DarknessDamageMode mode
    ) => new(
        Session,
        "22222222222242228222222222222222",
        "1",
        0,
        SanityAuthorityRole.Host,
        7,
        mode,
        false,
        "location.unsafe",
        80,
        100
    );
}
