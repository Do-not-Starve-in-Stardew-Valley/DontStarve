using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.Darkness;
using DontStarve.Player.Stats.Sanity.PassOut;
using Xunit;

namespace DontStarve.Tests.Sanity.PassOut;

public sealed class SanityTwoAmSpecialDeathStateMachineTests
{
    private const string SessionA = "11111111111141118111111111111111";
    private const string SessionB = "22222222222242228222222222222222";
    private const string CorrelationA = "aaaaaaaaaaaa4aaa8aaaaaaaaaaaaaaa";
    private const string CorrelationB = "bbbbbbbbbbbb4bbb8bbbbbbbbbbbbbbb";

    [Fact]
    public void OffAndSafeLocationsNeverStartAFlow()
    {
        var machine = new SanityTwoAmSpecialDeathStateMachine();

        var off = machine.Begin(Request(DarknessDamageMode.Off));
        var safe = machine.Begin(Request(DarknessDamageMode.Default, safe: true));

        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.NoChange, off.Status);
        Assert.Equal("passout.two-am.mode-off", off.Reason);
        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.NoChange, safe.Status);
        Assert.Equal("passout.two-am.location-safe", safe.Reason);
        Assert.Equal(0, machine.Count);
    }

    [Fact]
    public void ClientRequestIsRejectedAndHostReplayReturnsNoSecondActions()
    {
        var machine = new SanityTwoAmSpecialDeathStateMachine();
        var client = machine.Begin(Request(authority: SanityAuthorityRole.Client));
        var host = machine.Begin(Request());
        var replay = machine.Begin(Request());

        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Rejected, client.Status);
        Assert.Equal("passout.two-am.host-authority-required", client.Reason);
        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Applied, host.Status);
        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Duplicate, replay.Status);
        Assert.Empty(replay.Actions);
        Assert.Same(host.Snapshot, replay.Snapshot);
    }

    [Fact]
    public void DefaultFlowEmitsAWarningBAndCInOrder()
    {
        var machine = new SanityTwoAmSpecialDeathStateMachine();
        var begin = machine.Begin(Request());

        Assert.Equal(SanityTwoAmSpecialDeathPhase.PromptA, begin.Snapshot!.Phase);
        Assert.Equal(
            new[]
            {
                SanityTwoAmSpecialDeathActionKind.BeginSpecialOverlay,
                SanityTwoAmSpecialDeathActionKind.ShowPromptA,
            },
            begin.Actions.Select(action => action.Kind)
        );

        var warning = Advance(machine, begin.Snapshot, "a");
        Assert.Equal(SanityTwoAmSpecialDeathPhase.WarningCue, warning.Snapshot!.Phase);
        Assert.Equal(SanityTwoAmSpecialDeathActionKind.PlayWarningCue, Assert.Single(warning.Actions).Kind);

        var promptB = Advance(machine, warning.Snapshot, "warning");
        Assert.Equal(SanityTwoAmSpecialDeathPhase.PromptB, promptB.Snapshot!.Phase);
        Assert.Equal(
            new[]
            {
                SanityTwoAmSpecialDeathActionKind.StopWarningCue,
                SanityTwoAmSpecialDeathActionKind.ShowPromptB,
            },
            promptB.Actions.Select(action => action.Kind)
        );

        var promptC = Advance(machine, promptB.Snapshot, "b");
        Assert.Equal(SanityTwoAmSpecialDeathPhase.PromptC, promptC.Snapshot!.Phase);
        Assert.Equal(
            new[]
            {
                SanityTwoAmSpecialDeathActionKind.ShowPromptC,
                SanityTwoAmSpecialDeathActionKind.ReportMissingDeathCue,
            },
            promptC.Actions.Select(action => action.Kind)
        );

        var terminal = Advance(machine, promptC.Snapshot, "c");
        Assert.Equal(SanityTwoAmSpecialDeathPhase.AwaitingDayEnding, terminal.Snapshot!.Phase);
        Assert.Equal(
            new[]
            {
                SanityTwoAmSpecialDeathActionKind.QueueMail,
                SanityTwoAmSpecialDeathActionKind.BeginOfficialNewDay,
            },
            terminal.Actions.Select(action => action.Kind)
        );
    }

    [Fact]
    public void EveryPresentationCallbackIsExactlyOnceBySignalId()
    {
        var machine = new SanityTwoAmSpecialDeathStateMachine();
        var begin = machine.Begin(Request());
        var first = Advance(machine, begin.Snapshot!, "presentation-a");
        var duplicate = machine.Signal(
            "1",
            CorrelationA,
            SanityTwoAmSpecialDeathSignal.AdvancePresentation,
            "presentation-a"
        );

        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Applied, first.Status);
        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Duplicate, duplicate.Status);
        Assert.Empty(duplicate.Actions);
        Assert.Same(first.Snapshot, duplicate.Snapshot);
    }

    [Fact]
    public void EveryStateEdgeRejectsAnExactCallbackReplayWithoutActions()
    {
        var machine = new SanityTwoAmSpecialDeathStateMachine();
        var begin = machine.Begin(Request());
        var beginReplay = machine.Begin(Request());
        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Duplicate, beginReplay.Status);
        Assert.Empty(beginReplay.Actions);

        var state = begin.Snapshot!;
        foreach (var edge in new[] { "a", "warning", "b", "c" })
        {
            var applied = Advance(machine, state, edge);
            var replay = machine.Signal(
                state.PlayerKey,
                state.CorrelationId,
                SanityTwoAmSpecialDeathSignal.AdvancePresentation,
                edge
            );
            Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Applied, applied.Status);
            Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Duplicate, replay.Status);
            Assert.Empty(replay.Actions);
            state = applied.Snapshot!;
        }

        foreach (var edge in new[]
        {
            (SanityTwoAmSpecialDeathSignal.MailQueued, "mail"),
            (SanityTwoAmSpecialDeathSignal.DayEnding, "day-ending"),
            (SanityTwoAmSpecialDeathSignal.Saving, "saving"),
            (SanityTwoAmSpecialDeathSignal.DayStarted, "day-started"),
            (SanityTwoAmSpecialDeathSignal.LaterSaving, "later-saving"),
        })
        {
            var applied = Signal(machine, state, edge.Item1, edge.Item2);
            var replay = machine.Signal(
                state.PlayerKey,
                state.CorrelationId,
                edge.Item1,
                edge.Item2
            );
            Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Applied, applied.Status);
            Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Duplicate, replay.Status);
            Assert.Empty(replay.Actions);
            state = applied.Snapshot!;
        }

        var nonLethal = new SanityTwoAmSpecialDeathStateMachine();
        var nonLethalState = nonLethal.Begin(Request(DarknessDamageMode.NonLethal)).Snapshot!;
        var settled = Signal(
            nonLethal,
            nonLethalState,
            SanityTwoAmSpecialDeathSignal.NonLethalSettled,
            "nonlethal"
        );
        var settledReplay = nonLethal.Signal(
            nonLethalState.PlayerKey,
            nonLethalState.CorrelationId,
            SanityTwoAmSpecialDeathSignal.NonLethalSettled,
            "nonlethal"
        );
        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Applied, settled.Status);
        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Duplicate, settledReplay.Status);
        Assert.Empty(settledReplay.Actions);
    }

    [Fact]
    public void MissingDeathCueIsExplicitAndDoesNotBlockOfficialNewDay()
    {
        var machine = new SanityTwoAmSpecialDeathStateMachine();
        var state = machine.Begin(Request()).Snapshot!;
        state = Advance(machine, state, "a").Snapshot!;
        state = Advance(machine, state, "warning").Snapshot!;
        var c = Advance(machine, state, "b");

        Assert.Contains(
            c.Actions,
            action => action.Kind == SanityTwoAmSpecialDeathActionKind.ReportMissingDeathCue
        );
        var terminal = Advance(machine, c.Snapshot!, "c");
        Assert.Contains(
            terminal.Actions,
            action => action.Kind == SanityTwoAmSpecialDeathActionKind.BeginOfficialNewDay
        );
    }

    [Fact]
    public void MailReceiptUsesStableIdAndRepeatedQueueCallbackDoesNothing()
    {
        var machine = new SanityTwoAmSpecialDeathStateMachine();
        var state = ToAwaitingDayEnding(machine);
        var queued = machine.Signal(
            state.PlayerKey,
            state.CorrelationId,
            SanityTwoAmSpecialDeathSignal.MailQueued,
            "mail"
        );
        var replay = machine.Signal(
            state.PlayerKey,
            state.CorrelationId,
            SanityTwoAmSpecialDeathSignal.MailQueued,
            "mail"
        );

        Assert.True(queued.Snapshot!.MailQueued);
        Assert.Equal(
            "Yurin.DontStarve_SanityDarknessSpecialDeath",
            queued.Snapshot.MailId
        );
        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Duplicate, replay.Status);
        Assert.Empty(replay.Actions);
    }

    [Fact]
    public void DayEndingSavingAndDayStartedRecoverAtClinicOnce()
    {
        var machine = new SanityTwoAmSpecialDeathStateMachine();
        var state = ToAwaitingDayEnding(machine);
        state = Signal(machine, state, SanityTwoAmSpecialDeathSignal.MailQueued, "mail").Snapshot!;
        state = Signal(machine, state, SanityTwoAmSpecialDeathSignal.DayEnding, "day-ending").Snapshot!;
        Assert.Equal(SanityTwoAmSpecialDeathPhase.AwaitingSaving, state.Phase);
        state = Signal(machine, state, SanityTwoAmSpecialDeathSignal.Saving, "saving").Snapshot!;
        Assert.Equal(SanityTwoAmSpecialDeathPhase.AwaitingRecovery, state.Phase);

        var recovered = Signal(machine, state, SanityTwoAmSpecialDeathSignal.DayStarted, "day-started");
        Assert.Equal(SanityTwoAmSpecialDeathPhase.Recovered, recovered.Snapshot!.Phase);
        Assert.Equal(
            new[]
            {
                SanityTwoAmSpecialDeathActionKind.RecoverAtHarveyClinic,
                SanityTwoAmSpecialDeathActionKind.EndSpecialOverlay,
                SanityTwoAmSpecialDeathActionKind.StopWarningCue,
            },
            recovered.Actions.Select(action => action.Kind)
        );
        var replay = Signal(machine, recovered.Snapshot, SanityTwoAmSpecialDeathSignal.DayStarted, "day-started");
        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Duplicate, replay.Status);
        Assert.Empty(replay.Actions);
    }

    [Fact]
    public void RecoveredFlowCanBeRetiredForANewDayWhileExactReplayStaysDuplicate()
    {
        var machine = new SanityTwoAmSpecialDeathStateMachine();
        var state = ToAwaitingDayEnding(machine);
        state = Signal(machine, state, SanityTwoAmSpecialDeathSignal.MailQueued, "mail").Snapshot!;
        state = Signal(machine, state, SanityTwoAmSpecialDeathSignal.DayEnding, "day-ending").Snapshot!;
        state = Signal(machine, state, SanityTwoAmSpecialDeathSignal.Saving, "saving").Snapshot!;
        state = Signal(machine, state, SanityTwoAmSpecialDeathSignal.DayStarted, "day-started").Snapshot!;

        var exactReplay = machine.Begin(Request());
        var nextDay = machine.Begin(Request(correlationId: CorrelationB));

        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Duplicate, exactReplay.Status);
        Assert.Empty(exactReplay.Actions);
        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Applied, nextDay.Status);
        Assert.Equal(SanityTwoAmSpecialDeathPhase.PromptA, nextDay.Snapshot!.Phase);
        Assert.Equal(CorrelationB, nextDay.Snapshot.CorrelationId);
        Assert.Equal(1, machine.Count);
    }

    [Fact]
    public void CrashBeforeSavingHasNoPersistedFlow()
    {
        var machine = new SanityTwoAmSpecialDeathStateMachine();
        _ = ToAwaitingDayEnding(machine);

        var data = SanityTwoAmSpecialDeathPersistence.Capture(machine.SnapshotAll());

        Assert.Empty(data.Flows);
    }

    [Fact]
    public void CrashAfterSavingImportsRecoveryIntoTheNewSession()
    {
        var machine = new SanityTwoAmSpecialDeathStateMachine();
        var state = ToAwaitingDayEnding(machine);
        state = Signal(machine, state, SanityTwoAmSpecialDeathSignal.MailQueued, "mail").Snapshot!;
        state = Signal(machine, state, SanityTwoAmSpecialDeathSignal.DayEnding, "day-ending").Snapshot!;
        _ = Signal(machine, state, SanityTwoAmSpecialDeathSignal.Saving, "saving");
        var data = SanityTwoAmSpecialDeathPersistence.Capture(machine.SnapshotAll());
        var persisted = Assert.Single(data.Flows);

        var recoveredMachine = new SanityTwoAmSpecialDeathStateMachine();
        var imported = recoveredMachine.ImportRecovery(persisted, SessionB);

        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Applied, imported.Status);
        Assert.Equal(SessionB, imported.Snapshot!.SessionId);
        Assert.Equal(SanityTwoAmSpecialDeathPhase.AwaitingRecovery, imported.Snapshot.Phase);
        Assert.True(imported.Snapshot.MailQueued);
    }

    [Fact]
    public void LoadReplayOfSamePersistedFlowIsIdempotent()
    {
        var flow = PersistedFlow();
        var machine = new SanityTwoAmSpecialDeathStateMachine();

        var first = machine.ImportRecovery(flow, SessionB);
        var replay = machine.ImportRecovery(flow, SessionB);

        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Applied, first.Status);
        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Duplicate, replay.Status);
        Assert.Empty(replay.Actions);
        Assert.Same(first.Snapshot, replay.Snapshot);
    }

    [Fact]
    public void InvalidSaveIsReadOnlyAndNeverReturnedAsUsableData()
    {
        var wrongSchema = SanityTwoAmSpecialDeathPersistence.Validate(
            new SanityTwoAmSpecialDeathSaveData { SchemaVersion = 99 }
        );
        var wrongMail = PersistedFlow();
        wrongMail.MailId = "other-mail";
        var wrongMailResult = SanityTwoAmSpecialDeathPersistence.Validate(
            new SanityTwoAmSpecialDeathSaveData { Flows = new() { wrongMail } }
        );

        Assert.Equal(SanityTwoAmSpecialDeathPersistenceStatus.Invalid, wrongSchema.Status);
        Assert.False(wrongSchema.CanWrite);
        Assert.Null(wrongSchema.Data);
        Assert.Equal(SanityTwoAmSpecialDeathPersistenceStatus.Invalid, wrongMailResult.Status);
        Assert.False(wrongMailResult.CanWrite);
        Assert.Null(wrongMailResult.Data);
    }

    [Fact]
    public void PersistedDtoRemainsRecoveryOnlyAndExcludesSessionTransportState()
    {
        Assert.Equal(
            new[] { "Flows", "SchemaVersion" },
            typeof(SanityTwoAmSpecialDeathSaveData)
                .GetProperties()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
        );
        Assert.Equal(
            new[]
            {
                "AuthorityRevision",
                "CorrelationId",
                "Kind",
                "MailId",
                "MailQueued",
                "Phase",
                "PlayerKey",
                "Revision",
                "ScreenId",
                "SessionId",
            },
            typeof(SanityTwoAmSpecialDeathPersistedFlow)
                .GetProperties()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
        );
    }

    [Fact]
    public void DuplicateSavePlayerOrCorrelationIsRejected()
    {
        var first = PersistedFlow();
        var duplicatePlayer = PersistedFlow();
        duplicatePlayer.CorrelationId = CorrelationB;
        var playerResult = SanityTwoAmSpecialDeathPersistence.Validate(
            new SanityTwoAmSpecialDeathSaveData
            {
                Flows = new() { first, duplicatePlayer },
            }
        );

        var duplicateCorrelation = PersistedFlow();
        duplicateCorrelation.PlayerKey = "2";
        var correlationResult = SanityTwoAmSpecialDeathPersistence.Validate(
            new SanityTwoAmSpecialDeathSaveData
            {
                Flows = new() { first, duplicateCorrelation },
            }
        );

        Assert.Equal("passout.two-am.save-player-duplicated", playerResult.Reason);
        Assert.Equal("passout.two-am.save-correlation-duplicated", correlationResult.Reason);
    }

    [Fact]
    public void NonLethalEmitsOnlyReduceAndThenCompletesForVanillaHomePassOut()
    {
        var machine = new SanityTwoAmSpecialDeathStateMachine();
        var begin = machine.Begin(Request(DarknessDamageMode.NonLethal));

        Assert.Equal(SanityTwoAmSpecialDeathFlowKind.NonLethalHome, begin.Snapshot!.Kind);
        Assert.Equal(SanityTwoAmSpecialDeathPhase.NonLethalHomePending, begin.Snapshot.Phase);
        Assert.Equal(SanityTwoAmSpecialDeathActionKind.ReduceToFloor, Assert.Single(begin.Actions).Kind);
        Assert.DoesNotContain(
            begin.Actions,
            action => action.Kind is SanityTwoAmSpecialDeathActionKind.QueueMail
                or SanityTwoAmSpecialDeathActionKind.RecoverAtHarveyClinic
                or SanityTwoAmSpecialDeathActionKind.BeginOfficialNewDay
        );

        var settled = Signal(
            machine,
            begin.Snapshot,
            SanityTwoAmSpecialDeathSignal.NonLethalSettled,
            "nonlethal"
        );
        Assert.Equal(SanityTwoAmSpecialDeathPhase.Completed, settled.Snapshot!.Phase);
        Assert.Empty(SanityTwoAmSpecialDeathPersistence.Capture(machine.SnapshotAll()).Flows);
    }

    [Fact]
    public void CorrelationConflictCannotRestartOrDoubleSettleAPlayer()
    {
        var machine = new SanityTwoAmSpecialDeathStateMachine();
        var first = machine.Begin(Request());
        var conflict = machine.Begin(Request(correlationId: CorrelationB));

        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Applied, first.Status);
        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Rejected, conflict.Status);
        Assert.Equal("passout.two-am.player-correlation-conflict", conflict.Reason);
        Assert.Equal(1, machine.Count);
    }

    [Fact]
    public void UnexpectedWarpCancelsOnlyTheMatchingPlayerFlow()
    {
        var machine = new SanityTwoAmSpecialDeathStateMachine();
        var first = machine.Begin(Request());
        _ = machine.Begin(Request(playerKey: "2", correlationId: CorrelationB));

        var cancelled = machine.CancelPlayer(
            "1",
            first.Snapshot!.CorrelationId,
            "passout.two-am.unexpected-warp-cleared"
        );
        var replay = machine.CancelPlayer(
            "1",
            first.Snapshot.CorrelationId,
            "passout.two-am.unexpected-warp-cleared"
        );

        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Applied, cancelled.Status);
        Assert.Equal(
            new[]
            {
                SanityTwoAmSpecialDeathActionKind.EndSpecialOverlay,
                SanityTwoAmSpecialDeathActionKind.StopWarningCue,
            },
            cancelled.Actions.Select(action => action.Kind)
        );
        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.NoChange, replay.Status);
        Assert.True(machine.TryGetSnapshot("2", out _));
        Assert.Equal(1, machine.Count);
    }

    [Fact]
    public void FlowCapacityIsBounded()
    {
        var machine = new SanityTwoAmSpecialDeathStateMachine();
        for (var index = 1; index <= SanityTwoAmSpecialDeathContract.MaximumFlows; index++)
        {
            var result = machine.Begin(
                Request(
                    playerKey: index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    correlationId: GuidFor(index)
                )
            );
            Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Applied, result.Status);
        }

        var overflow = machine.Begin(Request(playerKey: "99", correlationId: GuidFor(99)));
        Assert.Equal(SanityTwoAmSpecialDeathMutationStatus.Rejected, overflow.Status);
        Assert.Equal("passout.two-am.flow-capacity-exceeded", overflow.Reason);
    }

    [Fact]
    public void RuntimeStaticContractUsesOfficialNewDayAndNeverVanillaDeathPipeline()
    {
        var source = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "PassOut",
                "SmapiSanityTwoAmSpecialDeathService.cs"
            )
        );

        Assert.Contains("Game1.PassOutNewDay()", source, StringComparison.Ordinal);
        Assert.Contains("Game1.warpFarmer(\"Hospital\", 20, 12, false)", source, StringComparison.Ordinal);
        Assert.Contains("Game1.addMailForTomorrow", source, StringComparison.Ordinal);
        Assert.Contains(
            "if (!farmer.hasOrWillReceiveMail(SanityTwoAmSpecialDeathContract.MailId))\r\n                {\r\n                    Game1.addMailForTomorrow(",
            source.Replace("\n", "\r\n", StringComparison.Ordinal).Replace("\r\r\n", "\r\n", StringComparison.Ordinal),
            StringComparison.Ordinal
        );
        Assert.Contains("ReduceSanityDarknessSpecialDeathToFloor", source, StringComparison.Ordinal);
        Assert.Contains("ModMessageReceived", source, StringComparison.Ordinal);
        Assert.Contains("SanityTwoAmSpecialDeathRequestMessage", source, StringComparison.Ordinal);
        Assert.Contains("SanityTwoAmSpecialDeathDecisionMessage", source, StringComparison.Ordinal);
        Assert.Contains("SanityTwoAmSpecialDeathActionMessage", source, StringComparison.Ordinal);
        Assert.Contains("hostRequestReceipts", source, StringComparison.Ordinal);
        Assert.Contains("clientReceipts", source, StringComparison.Ordinal);
        Assert.Contains("SnapshotMessageType", source, StringComparison.Ordinal);
        Assert.Contains("PeerConnected", source, StringComparison.Ordinal);
        Assert.Contains("PeerDisconnected", source, StringComparison.Ordinal);
        Assert.Contains("Game1.GetPlayer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.killScreen", source, StringComparison.Ordinal);
        Assert.DoesNotContain("takeDamage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("mailReceived.Add", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessExitDoesNotReenterSmapiServicesOrClosedMonitor()
    {
        var source = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "PassOut",
                "SmapiSanityTwoAmSpecialDeathService.cs"
            )
        );
        var processExitStart = source.IndexOf(
            "private void OnProcessExit",
            StringComparison.Ordinal
        );
        var logOnceStart = source.IndexOf(
            "private void LogOnce",
            processExitStart,
            StringComparison.Ordinal
        );

        Assert.True(processExitStart >= 0);
        Assert.True(logOnceStart > processExitStart);
        var processExit = source[processExitStart..logOnceStart];
        Assert.DoesNotContain("Dispose();", processExit, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearSession(", processExit, StringComparison.Ordinal);
        Assert.DoesNotContain("monitor.Log", processExit, StringComparison.Ordinal);
    }

    private static SanityTwoAmSpecialDeathSnapshot ToAwaitingDayEnding(
        SanityTwoAmSpecialDeathStateMachine machine
    )
    {
        var state = machine.Begin(Request()).Snapshot!;
        state = Advance(machine, state, "a").Snapshot!;
        state = Advance(machine, state, "warning").Snapshot!;
        state = Advance(machine, state, "b").Snapshot!;
        return Advance(machine, state, "c").Snapshot!;
    }

    private static SanityTwoAmSpecialDeathMutation Advance(
        SanityTwoAmSpecialDeathStateMachine machine,
        SanityTwoAmSpecialDeathSnapshot state,
        string signalId
    )
    {
        return Signal(
            machine,
            state,
            SanityTwoAmSpecialDeathSignal.AdvancePresentation,
            signalId
        );
    }

    private static SanityTwoAmSpecialDeathMutation Signal(
        SanityTwoAmSpecialDeathStateMachine machine,
        SanityTwoAmSpecialDeathSnapshot state,
        SanityTwoAmSpecialDeathSignal signal,
        string signalId
    )
    {
        return machine.Signal(state.PlayerKey, state.CorrelationId, signal, signalId);
    }

    private static SanityTwoAmSpecialDeathStartRequest Request(
        DarknessDamageMode mode = DarknessDamageMode.Default,
        bool safe = false,
        SanityAuthorityRole authority = SanityAuthorityRole.Host,
        string playerKey = "1",
        string correlationId = CorrelationA
    )
    {
        return new SanityTwoAmSpecialDeathStartRequest(
            SessionA,
            correlationId,
            playerKey,
            0,
            authority,
            7,
            mode,
            safe,
            safe
                ? "passout.location.two-am-special-death.safe-rule-matched"
                : "passout.location.two-am-special-death.unsafe-rule-matched",
            100,
            100
        );
    }

    private static SanityTwoAmSpecialDeathPersistedFlow PersistedFlow()
    {
        return new SanityTwoAmSpecialDeathPersistedFlow
        {
            SessionId = SessionA,
            CorrelationId = CorrelationA,
            PlayerKey = "1",
            ScreenId = 0,
            AuthorityRevision = 7,
            Kind = SanityTwoAmSpecialDeathFlowKind.DefaultClinic.ToString(),
            Phase = SanityTwoAmSpecialDeathPhase.AwaitingRecovery.ToString(),
            MailQueued = true,
            MailId = SanityTwoAmSpecialDeathContract.MailId,
            Revision = 8,
        };
    }

    private static string GuidFor(int value)
    {
        return value.ToString("x8", System.Globalization.CultureInfo.InvariantCulture)
            + "000040008000000000000000";
    }
}
