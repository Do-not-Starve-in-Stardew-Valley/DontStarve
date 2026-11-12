using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.Darkness;
using DontStarve.Player.Stats.Sanity.PassOut;
using Xunit;

namespace DontStarve.Tests.Sanity.PassOut;

public sealed class SanityTwoAmSpecialDeathStateMachineTests
{
    private const string Session = "11111111111141118111111111111111";

    [Fact]
    public void SafeAndOffRequestsAreNoChangeAndCreateNoFlow()
    {
        var machine = new SanityTwoAmSpecialDeathStateMachine();

        var safe = machine.Begin(Request(DarknessDamageMode.Default, safe: true));
        var off = machine.Begin(Request(DarknessDamageMode.Off));

        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.NoChange, safe.Status);
        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.NoChange, off.Status);
        Assert.Equal(0, machine.Count);
    }

    [Fact]
    public void CorrelationConflictCannotReplaceAnActiveOwnerFlow()
    {
        var machine = new SanityTwoAmSpecialDeathStateMachine();
        _ = machine.Begin(Request(DarknessDamageMode.Default));

        var conflict = machine.Begin(
            Request(DarknessDamageMode.Default) with
            {
                CorrelationId = "33333333333343338333333333333333",
            }
        );

        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Rejected, conflict.Status);
        Assert.Equal(1, machine.Count);
    }

    [Fact]
    public void CompletedFlowCanBeRetiredForTheNextSession()
    {
        var machine = new SanityTwoAmSpecialDeathStateMachine();
        var state = machine.Begin(Request(DarknessDamageMode.Default)).Snapshot!;
        state = Advance(machine, state, "warning").Snapshot!;
        state = Advance(machine, state, "resolved").Snapshot!;
        state = Advance(machine, state, "outcome").Snapshot!;

        Assert.Equal(SanityTwoAmSpecialDeathPhase.Completed, state.Phase);

        var next = machine.Begin(
            Request(DarknessDamageMode.NonLethal) with
            {
                CorrelationId = "33333333333343338333333333333333",
            }
        );

        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Applied, next.Status);
        Assert.Equal(SanityTwoAmSpecialDeathFlowKind.NonLethalHome, next.Snapshot!.Kind);
    }

    [Fact]
    public void LegacyRecoveryImportIsDiscardedWithoutMailOrClinicAction()
    {
        var machine = new SanityTwoAmSpecialDeathStateMachine();
        var result = machine.ImportRecovery(
            new SanityTwoAmSpecialDeathPersistedFlow
            {
                SessionId = Session,
                CorrelationId = "22222222222242228222222222222222",
                PlayerKey = "1",
                ScreenId = 0,
                AuthorityRevision = 1,
                Kind = "DefaultClinic",
                Phase = "AwaitingRecovery",
                MailQueued = true,
                MailId = "Yurin.DontStarve_SanityDarknessSpecialDeath",
                Revision = 1,
            },
            Session
        );

        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.NoChange, result.Status);
        Assert.Empty(result.Actions);
        Assert.Equal(0, machine.Count);
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
        DarknessDamageMode mode,
        bool safe = false
    ) => new(
        Session,
        "22222222222242228222222222222222",
        "1",
        0,
        SanityAuthorityRole.Host,
        7,
        mode,
        safe,
        "location.unsafe",
        80,
        100
    );
}
