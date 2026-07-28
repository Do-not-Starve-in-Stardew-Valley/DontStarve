using DontStarve.Config;
using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.Damage;
using DontStarve.Player.Stats.Sanity.Darkness;
using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using DontStarve.Tests.Config;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class DarknessAttackResolutionTests
{
    private const string SessionA = "11111111111111111111111111111111";
    private const string SessionB = "22222222222222222222222222222222";

    [Fact]
    public void DarknessAttackResolution_Off_records_disabled_without_rng_damage_or_sanity()
    {
        var fixture = Fixture(0);

        var result = fixture.Service.Resolve(Request(DarknessDamageMode.Off));

        Assert.Equal(DarknessAttackResolutionStatus.Disabled, result.Status);
        Assert.Equal(DarknessAttackResolutionReceiptStatus.Disabled, result.Receipt!.Status);
        Assert.Equal(DarknessAttackDamageOperation.Disabled, result.Receipt.Operation);
        Assert.Equal(-1, result.Receipt.RngRoll);
        Assert.Equal(0, fixture.Random.Calls);
        Assert.Equal(0, fixture.Damage.Calls);
        Assert.Equal(0, fixture.Sanity.Calls);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(94, 100)]
    [InlineData(95, 101)]
    [InlineData(99, 101)]
    public void DarknessAttackResolution_95_5_boundaries_are_exact(
        int roll,
        int expectedBaseDamage
    )
    {
        var fixture = Fixture(roll);

        var result = fixture.Service.Resolve(Request(DarknessDamageMode.Default));

        Assert.Equal(DarknessAttackResolutionStatus.Settled, result.Status);
        Assert.Equal(expectedBaseDamage, result.Receipt!.BaseDamage);
        Assert.Equal(DarknessAttackDamageOperation.DefaultPhysical, result.Receipt.Operation);
        Assert.Equal(roll, result.Receipt.RngRoll);
        Assert.Equal(1, fixture.Random.Calls);
        Assert.Equal(1, fixture.Damage.Calls);
        Assert.Equal(1, fixture.Sanity.Calls);
    }

    [Fact]
    public void DarknessAttackResolution_Default_zero_hp_delta_still_applies_authorized_sanity()
    {
        var fixture = Fixture(12);
        fixture.Damage.Handler = request => DarknessAttackDamageReceipt.Settled(
            request,
            beforeHealth: 80,
            maximumHealth: 100,
            actualDamage: 0,
            afterHealth: 80,
            "test.default-iframe-settled"
        );

        var result = fixture.Service.Resolve(Request(DarknessDamageMode.Default));

        Assert.True(result.Receipt!.IsSettled);
        Assert.Equal(0, result.Receipt.DamageReceipt!.ActualDamage);
        Assert.Equal(SanityChangeSource.DarknessAttack, result.Receipt.SanityReceipt!.Source);
        Assert.Equal(result.Receipt.ReceiptId, result.Receipt.SanityReceipt.DamageReceiptId);
        Assert.Equal(1, fixture.Sanity.Calls);
    }

    [Fact]
    public void DarknessAttackResolution_SanityChangeSource_links_request_and_damage_receipt()
    {
        var fixture = Fixture(0);

        var result = fixture.Service.Resolve(Request(DarknessDamageMode.Default));

        var receipt = Assert.IsType<DarknessAttackResolutionReceipt>(result.Receipt);
        var sanity = Assert.IsType<DarknessAttackSanityReceipt>(receipt.SanityReceipt);
        Assert.Equal(SanityChangeSource.DarknessAttack, sanity.Source);
        Assert.Equal(receipt.RequestId, sanity.RequestId);
        Assert.Equal(receipt.ReceiptId, sanity.DamageReceiptId);
        Assert.Equal(DarknessAttackResolutionService.SanityDelta, sanity.Delta);
    }

    [Fact]
    public void DarknessAttackResolution_Default_smaller_defended_delta_is_still_one_settlement()
    {
        var fixture = Fixture(0);
        fixture.Damage.Handler = request => DarknessAttackDamageReceipt.Settled(
            request,
            beforeHealth: 100,
            maximumHealth: 100,
            actualDamage: 13,
            afterHealth: 87,
            "test.default-defense-settled"
        );

        var result = fixture.Service.Resolve(Request(DarknessDamageMode.Default));

        Assert.Equal(DarknessAttackResolutionStatus.Settled, result.Status);
        Assert.Equal(13, result.Receipt!.DamageReceipt!.ActualDamage);
        Assert.Equal(1, fixture.Damage.Calls);
        Assert.Equal(1, fixture.Sanity.Calls);
    }

    [Fact]
    public void DarknessAttackResolution_NonLethal_uses_apply_operation_and_near_floor_receipt()
    {
        var fixture = Fixture(95);
        fixture.Damage.Handler = request => DarknessAttackDamageReceipt.Settled(
            request,
            beforeHealth: 31,
            maximumHealth: 150,
            actualDamage: 1,
            afterHealth: 30,
            "test.nonlethal-landed-on-floor"
        );

        var result = fixture.Service.Resolve(Request(DarknessDamageMode.NonLethal));

        Assert.Equal(DarknessAttackResolutionStatus.Settled, result.Status);
        Assert.Equal(
            DarknessAttackDamageOperation.ApplyDamageUpToFloor,
            result.Receipt!.Operation
        );
        Assert.Equal(101, result.Receipt.BaseDamage);
        Assert.Equal(30, result.Receipt.DamageReceipt!.AfterHealth);
        Assert.Equal(1, fixture.Sanity.Calls);
    }

    [Theory]
    [InlineData((int)DarknessDamageMode.Off, 150, 150, false, 0)]
    [InlineData((int)DarknessDamageMode.Default, 1, 150, true, 0)]
    [InlineData((int)DarknessDamageMode.NonLethal, 31, 150, true, 30)]
    [InlineData((int)DarknessDamageMode.NonLethal, 30, 150, false, 30)]
    [InlineData((int)DarknessDamageMode.NonLethal, 29, 150, false, 30)]
    public void DarknessAttackResolution_Mode_policy_stops_nonlethal_at_floor(
        int modeValue,
        int health,
        int maximumHealth,
        bool expectedCanSettle,
        int expectedFloor
    )
    {
        var mode = (DarknessDamageMode)modeValue;
        var policy = DarknessAttackModePolicy.Evaluate(mode, health, maximumHealth);

        Assert.Equal(expectedCanSettle, policy.CanSettle);
        Assert.Equal(expectedFloor, policy.FloorHealth);
        if (mode == DarknessDamageMode.NonLethal)
        {
            Assert.True(
                NonLethalDamageCalculator.TryCalculateFloor(
                    maximumHealth,
                    out var damageFloor,
                    out _
                )
            );
            Assert.Equal(damageFloor, policy.FloorHealth);
        }
    }

    [Fact]
    public void DarknessAttackResolution_Mode_tracker_requires_reset_on_runtime_switch()
    {
        var tracker = new DarknessDamageModeTracker();

        var first = tracker.Observe(Mode(DarknessDamageMode.Default));
        var same = tracker.Observe(Mode(DarknessDamageMode.Default));
        var nonLethal = tracker.Observe(Mode(DarknessDamageMode.NonLethal));
        var off = tracker.Observe(Mode(DarknessDamageMode.Off));
        var unavailable = tracker.Observe(
            DarknessDamageModeResolution.Unavailable("test.unavailable")
        );

        Assert.False(first.ResetRequired);
        Assert.False(same.ResetRequired);
        Assert.True(nonLethal.ResetRequired);
        Assert.True(nonLethal.CanRun);
        Assert.True(off.ResetRequired);
        Assert.False(off.CanRun);
        Assert.False(unavailable.CanRun);
    }

    [Fact]
    public void DarknessAttackResolution_Mode_trackers_keep_split_screens_independent()
    {
        var firstScreen = new DarknessDamageModeTracker();
        var secondScreen = new DarknessDamageModeTracker();
        firstScreen.Observe(Mode(DarknessDamageMode.Default));
        secondScreen.Observe(Mode(DarknessDamageMode.Default));

        var firstChanged = firstScreen.Observe(Mode(DarknessDamageMode.NonLethal));
        var secondUnchanged = secondScreen.Observe(Mode(DarknessDamageMode.Default));

        Assert.True(firstChanged.ResetRequired);
        Assert.False(secondUnchanged.ResetRequired);
        Assert.True(secondUnchanged.CanRun);
    }

    [Fact]
    public void DarknessAttackResolution_Mode_switch_cancels_old_request_before_new_cycle()
    {
        var owner = new DarknessAttackOwnerKey("1", 0, SessionA);
        var tracker = new DarknessDamageModeTracker();
        tracker.Observe(Mode(DarknessDamageMode.Default));
        using var stateMachine = new DarknessAttackStateMachine(
            new FakeCountdownClock(),
            new FixedCountdownRandom(5),
            new FixedRequestIds("mode-switch"),
            warningLeadSeconds: 0.48d
        );
        stateMachine.Observe(Observation(owner, revision: 1));
        Assert.True(stateMachine.TryGetSnapshot(owner, out var first));

        var transition = tracker.Observe(Mode(DarknessDamageMode.NonLethal));
        Assert.True(transition.ResetRequired);
        stateMachine.Cancel(owner, "test.mode-changed");
        stateMachine.Observe(Observation(owner, revision: 2));
        Assert.True(stateMachine.TryGetSnapshot(owner, out var second));
        var staleReceipt = stateMachine.CompleteReceipt(
            Observation(owner, revision: 3),
            new DarknessAttackReceipt(
                owner,
                first.RequestId,
                DarknessAttackReceiptDisposition.Applied,
                "test.stale"
            )
        );

        Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.Equal(DarknessAttackMutationStatus.NoChange, staleReceipt.Status);
        Assert.Equal(second.RequestId, stateMachine.TryGetSnapshot(owner, out var current)
            ? current.RequestId
            : string.Empty);
    }

    [Fact]
    public void DarknessAttackResolution_Duplicate_returns_original_without_replaying_effects()
    {
        var fixture = Fixture(0);
        var request = Request(DarknessDamageMode.Default);

        var first = fixture.Service.Resolve(request);
        var duplicate = fixture.Service.Resolve(request);

        Assert.Equal(DarknessAttackResolutionStatus.Settled, first.Status);
        Assert.Equal(DarknessAttackResolutionStatus.Duplicate, duplicate.Status);
        Assert.Same(first.Receipt, duplicate.Receipt);
        Assert.Equal(1, fixture.Random.Calls);
        Assert.Equal(1, fixture.Damage.Calls);
        Assert.Equal(1, fixture.Sanity.Calls);
    }

    [Fact]
    public void DarknessAttackResolution_Same_request_different_operation_is_conflict()
    {
        var fixture = Fixture(0);
        var first = fixture.Service.Resolve(Request(DarknessDamageMode.Default));

        var conflict = fixture.Service.Resolve(Request(DarknessDamageMode.NonLethal));

        Assert.Equal(DarknessAttackResolutionStatus.CorrelationConflict, conflict.Status);
        Assert.Same(first.Receipt, conflict.Receipt);
        Assert.Equal(1, fixture.Random.Calls);
        Assert.Equal(1, fixture.Damage.Calls);
        Assert.Equal(1, fixture.Sanity.Calls);
    }

    [Fact]
    public void DarknessAttackResolution_Two_players_are_isolated()
    {
        var fixture = Fixture(0, 95);

        var first = fixture.Service.Resolve(
            Request(DarknessDamageMode.Default, playerKey: "1", screenId: 0, requestId: "p1")
        );
        var second = fixture.Service.Resolve(
            Request(DarknessDamageMode.Default, playerKey: "2", screenId: 1, requestId: "p2")
        );

        Assert.Equal(100, first.Receipt!.BaseDamage);
        Assert.Equal(101, second.Receipt!.BaseDamage);
        Assert.NotEqual(first.Receipt.ReceiptId, second.Receipt.ReceiptId);
        Assert.Equal(2, fixture.Damage.Calls);
        Assert.Equal(2, fixture.Sanity.Calls);
    }

    [Fact]
    public void DarknessAttackResolution_New_session_clears_window_and_rejects_old_session()
    {
        var fixture = Fixture(0, 0);
        var oldRequest = Request(DarknessDamageMode.Default, sessionId: SessionA);
        Assert.Equal(
            DarknessAttackResolutionStatus.Settled,
            fixture.Service.Resolve(oldRequest).Status
        );

        Assert.True(fixture.Service.BeginSession(SessionB).Accepted);
        var stale = fixture.Service.Resolve(oldRequest);
        var current = fixture.Service.Resolve(
            Request(DarknessDamageMode.Default, sessionId: SessionB)
        );

        Assert.Equal(DarknessAttackResolutionStatus.SessionMismatch, stale.Status);
        Assert.Equal(DarknessAttackResolutionStatus.Settled, current.Status);
        Assert.Equal(2, fixture.Damage.Calls);
    }

    [Fact]
    public void DarknessAttackResolution_Client_cannot_draw_or_settle()
    {
        var fixture = Fixture(0);

        var result = fixture.Service.Resolve(
            Request(DarknessDamageMode.Default) with
            {
                Authority = SanityAuthorityRole.Client,
            }
        );

        Assert.Equal(DarknessAttackResolutionStatus.RequiresHostAuthority, result.Status);
        Assert.Equal(0, fixture.Random.Calls);
        Assert.Equal(0, fixture.Damage.Calls);
        Assert.Equal(0, fixture.Sanity.Calls);
    }

    [Fact]
    public void DarknessAttackResolution_Damage_rejection_never_calls_sanity()
    {
        var fixture = Fixture(0);
        fixture.Damage.Handler = request => DarknessAttackDamageReceipt.Rejected(
            request,
            "test.damage-rejected"
        );

        var result = fixture.Service.Resolve(Request(DarknessDamageMode.Default));

        Assert.Equal(DarknessAttackResolutionStatus.Rejected, result.Status);
        Assert.Equal(1, fixture.Damage.Calls);
        Assert.Equal(0, fixture.Sanity.Calls);
    }

    [Fact]
    public void DarknessAttackResolution_Damage_exception_is_recorded_and_not_retried()
    {
        var fixture = Fixture(0);
        fixture.Damage.Handler = _ => throw new InvalidOperationException("test");
        var request = Request(DarknessDamageMode.Default);

        var failed = fixture.Service.Resolve(request);
        var duplicate = fixture.Service.Resolve(request);

        Assert.Equal(DarknessAttackResolutionStatus.Rejected, failed.Status);
        Assert.Equal(DarknessAttackResolutionStatus.Duplicate, duplicate.Status);
        Assert.Contains("damage-authority-threw", failed.Reason, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Damage.Calls);
        Assert.Equal(0, fixture.Sanity.Calls);
    }

    [Fact]
    public void DarknessAttackResolution_Sanity_rejection_does_not_repeat_damage_on_replay()
    {
        var fixture = Fixture(0);
        fixture.Sanity.Handler = request => SanityReceipt(
            request,
            DarknessAttackSanityReceiptStatus.Rejected,
            beforeRevision: request.ExpectedRevision,
            afterRevision: request.ExpectedRevision,
            beforeSanity: 80,
            afterSanity: 80,
            "test.sanity-rejected"
        );
        var request = Request(DarknessDamageMode.Default);

        var failed = fixture.Service.Resolve(request);
        var duplicate = fixture.Service.Resolve(request);

        Assert.Equal(DarknessAttackResolutionStatus.Rejected, failed.Status);
        Assert.Equal(DarknessAttackResolutionStatus.Duplicate, duplicate.Status);
        Assert.Equal(1, fixture.Damage.Calls);
        Assert.Equal(1, fixture.Sanity.Calls);
    }

    [Fact]
    public void DarknessAttackResolution_Sanity_at_zero_is_accepted_no_change()
    {
        var fixture = Fixture(0);
        fixture.Sanity.Handler = request => SanityReceipt(
            request,
            DarknessAttackSanityReceiptStatus.NoChange,
            beforeRevision: request.ExpectedRevision,
            afterRevision: request.ExpectedRevision,
            beforeSanity: 0,
            afterSanity: 0,
            "sanity-value-is-unchanged"
        );

        var result = fixture.Service.Resolve(Request(DarknessDamageMode.Default));

        Assert.Equal(DarknessAttackResolutionStatus.Settled, result.Status);
        Assert.Equal(
            DarknessAttackSanityReceiptStatus.NoChange,
            result.Receipt!.SanityReceipt!.Status
        );
    }

    [Fact]
    public void DarknessAttackResolution_Invalid_rng_is_one_rejected_receipt()
    {
        var fixture = Fixture(100);
        var request = Request(DarknessDamageMode.Default);

        var first = fixture.Service.Resolve(request);
        var duplicate = fixture.Service.Resolve(request);

        Assert.Equal(DarknessAttackResolutionStatus.Rejected, first.Status);
        Assert.Equal(DarknessAttackResolutionStatus.Duplicate, duplicate.Status);
        Assert.Equal(1, fixture.Random.Calls);
        Assert.Equal(0, fixture.Damage.Calls);
        Assert.Equal(0, fixture.Sanity.Calls);
    }

    [Fact]
    public void DarknessAttackResolution_Receipt_window_fails_closed_without_eviction()
    {
        var fixture = Fixture(Enumerable.Repeat(0, 257).ToArray());
        for (var index = 0; index < DarknessAttackResolutionService.MaximumReceipts; index++)
        {
            var result = fixture.Service.Resolve(
                Request(DarknessDamageMode.Default, requestId: $"capacity-{index}")
            );
            Assert.Equal(DarknessAttackResolutionStatus.Settled, result.Status);
        }

        var overflow = fixture.Service.Resolve(
            Request(DarknessDamageMode.Default, requestId: "capacity-overflow")
        );

        Assert.Equal(DarknessAttackResolutionStatus.CapacityExceeded, overflow.Status);
        Assert.Equal(DarknessAttackResolutionService.MaximumReceipts, fixture.Service.ReceiptCount);
        Assert.Equal(DarknessAttackResolutionService.MaximumReceipts, fixture.Random.Calls);
    }

    [Fact]
    public void DarknessAttackResolution_Diagnostic_snapshot_is_owner_local_and_clearable()
    {
        var fixture = Fixture(0);
        var result = fixture.Service.Resolve(Request(DarknessDamageMode.Default));

        Assert.True(fixture.Service.TryGetLatest("1", 0, out var snapshot));
        Assert.Same(result.Receipt, snapshot);
        Assert.False(fixture.Service.TryGetLatest("2", 0, out _));

        fixture.Service.ForgetDiagnosticOwner("1");
        Assert.False(fixture.Service.TryGetLatest("1", 0, out _));
    }

    [Fact]
    public void DarknessAttackResolution_Correlation_is_stable_and_operation_scoped()
    {
        var key = new DarknessAttackOwnerKey("1", 0, SessionA);

        var first = DarknessAttackResolutionCorrelation.Create(
            key,
            "request-a",
            DarknessAttackDamageOperation.DefaultPhysical
        );
        var repeat = DarknessAttackResolutionCorrelation.Create(
            key,
            "request-a",
            DarknessAttackDamageOperation.DefaultPhysical
        );
        var otherOperation = DarknessAttackResolutionCorrelation.Create(
            key,
            "request-a",
            DarknessAttackDamageOperation.ApplyDamageUpToFloor
        );

        Assert.Equal(first, repeat);
        Assert.NotEqual(first, otherOperation);
        Assert.True(Guid.TryParseExact(first, "N", out var parsed));
        Assert.NotEqual(Guid.Empty, parsed);
    }

    [Fact]
    public void DarknessAttackResolution_Typed_resolver_uses_schema_default_and_saved_off()
    {
        var registry = ConfigTestData.LoadShippedRegistry();
        var defaultStore = Assert.IsType<FlatConfigValueStore>(
            FlatConfigValueStore.Load(
                registry,
                new MemoryFlatConfigFileAccess(null, isMissing: true)
            ).Store
        );
        var configuredStore = Assert.IsType<FlatConfigValueStore>(
            FlatConfigValueStore.Load(
                registry,
                new MemoryFlatConfigFileAccess("{\"DarknessDamageMode\":\"Off\"}")
            ).Store
        );

        var defaultMode = new TypedDarknessDamageModeResolver(
            new TypedConfigResolver(registry, defaultStore)
        ).Resolve();
        var offMode = new TypedDarknessDamageModeResolver(
            new TypedConfigResolver(registry, configuredStore)
        ).Resolve();

        Assert.True(defaultMode.HasValue);
        Assert.Equal(DarknessDamageMode.Default, defaultMode.Mode);
        Assert.True(offMode.HasValue);
        Assert.Equal(DarknessDamageMode.Off, offMode.Mode);
    }

    [Fact]
    public void DarknessAttackResolution_Fake_clock_rng_damage_end_to_end_settles_once()
    {
        var clock = new FakeCountdownClock();
        var countdownRandom = new FixedCountdownRandom(5);
        using var stateMachine = new DarknessAttackStateMachine(
            clock,
            countdownRandom,
            new FixedRequestIds("end-to-end-request"),
            warningLeadSeconds: 0.48d
        );
        var owner = new DarknessAttackOwnerKey("1", 0, SessionA);
        var started = stateMachine.Observe(Observation(owner, revision: 1));
        Assert.Equal(DarknessAttackMutationStatus.Applied, started.Status);

        clock.Elapsed = TimeSpan.FromSeconds(5);
        var expired = stateMachine.Observe(Observation(owner, revision: 2));
        var intent = Assert.IsType<DarknessAttackExpiryIntent>(expired.ExpiryIntent);
        var fixture = Fixture(94);
        var settled = fixture.Service.Resolve(
            new DarknessAttackResolutionRequest(
                intent,
                DarknessDamageMode.Default,
                SanityAuthorityRole.Host,
                3
            )
        );
        var completed = stateMachine.CompleteReceipt(
            Observation(owner, revision: 3),
            new DarknessAttackReceipt(
                owner,
                intent.RequestId,
                DarknessAttackReceiptDisposition.Applied,
                settled.Reason
            )
        );
        var replay = fixture.Service.Resolve(
            new DarknessAttackResolutionRequest(
                intent,
                DarknessDamageMode.Default,
                SanityAuthorityRole.Host,
                3
            )
        );

        Assert.Equal(DarknessAttackResolutionStatus.Settled, settled.Status);
        Assert.Equal(DarknessAttackMutationStatus.Applied, completed.Status);
        Assert.Equal(DarknessAttackResolutionStatus.Duplicate, replay.Status);
        Assert.Equal(1, fixture.Random.Calls);
        Assert.Equal(1, fixture.Damage.Calls);
        Assert.Equal(1, fixture.Sanity.Calls);
        Assert.True(stateMachine.TryGetSnapshot(owner, out var next));
        Assert.Equal(DarknessAttackOwnerState.Countdown, next.State);
        Assert.NotEqual(intent.RequestId, next.RequestId);
    }

    [Fact]
    public void DarknessAttackResolution_Runtime_keeps_default_and_nonlethal_operations_separate()
    {
        var resolutionSource = File.ReadAllText(RuntimeResolutionContractPath);
        var countdownSource = File.ReadAllText(RuntimeCountdownContractPath);
        var damageAuthorityStart = resolutionSource.IndexOf(
            "internal sealed class SmapiDarknessAttackDamageAuthority",
            StringComparison.Ordinal
        );
        var damageAuthorityEnd = resolutionSource.IndexOf(
            "internal sealed class SmapiDarknessAttackSanityAuthority",
            damageAuthorityStart,
            StringComparison.Ordinal
        );
        Assert.True(damageAuthorityStart >= 0);
        Assert.True(damageAuthorityEnd > damageAuthorityStart);
        var damageAuthoritySource = resolutionSource[
            damageAuthorityStart..damageAuthorityEnd
        ];

        Assert.Contains("player.takeDamage", resolutionSource, StringComparison.Ordinal);
        Assert.Contains("ApplyDamageUpToFloor", damageAuthoritySource, StringComparison.Ordinal);
        Assert.DoesNotContain("ReduceToFloor", damageAuthoritySource, StringComparison.Ordinal);
        Assert.Contains("Random.Shared.Next(100)", resolutionSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.random", resolutionSource, StringComparison.Ordinal);
        Assert.DoesNotContain("farmerSanity", resolutionSource, StringComparison.Ordinal);
        Assert.Contains("DarknessAttackModePolicy.Evaluate", countdownSource, StringComparison.Ordinal);
        Assert.Contains("Dictionary<int, DarknessDamageModeTracker>", countdownSource, StringComparison.Ordinal);
        Assert.Contains("ResetForModeChange", countdownSource, StringComparison.Ordinal);
    }

    private static ResolutionFixture Fixture(params int[] rolls)
    {
        var random = new SequenceResolutionRandom(rolls);
        var damage = new FakeDamageAuthority();
        var sanity = new FakeSanityAuthority();
        var service = new DarknessAttackResolutionService(random, damage, sanity);
        Assert.True(service.BeginSession(SessionA).Accepted);
        return new ResolutionFixture(service, random, damage, sanity);
    }

    private static DarknessAttackResolutionRequest Request(
        DarknessDamageMode mode,
        string playerKey = "1",
        int screenId = 0,
        string sessionId = SessionA,
        string requestId = "request-1"
    )
    {
        return new DarknessAttackResolutionRequest(
            new DarknessAttackExpiryIntent(
                new DarknessAttackOwnerKey(playerKey, screenId, sessionId),
                requestId,
                Revision: 200,
                LightReason: "test.confirmed-pitch-black",
                DarknessAttackContract.ContractVersion
            ),
            mode,
            SanityAuthorityRole.Host,
            SanityAuthorityRevision: 3
        );
    }

    private static DarknessDamageModeResolution Mode(DarknessDamageMode mode)
    {
        return DarknessDamageModeResolution.Available(mode, "test.mode");
    }

    private static DarknessAttackSanityReceipt SanityReceipt(
        DarknessAttackSanityRequest request,
        DarknessAttackSanityReceiptStatus status,
        long beforeRevision,
        long afterRevision,
        double beforeSanity,
        double afterSanity,
        string reason
    )
    {
        return new DarknessAttackSanityReceipt(
            status,
            request.Key,
            request.RequestId,
            request.DamageReceiptId,
            request.Delta,
            request.Source,
            beforeRevision,
            afterRevision,
            beforeSanity,
            afterSanity,
            reason
        );
    }

    private static DarknessAttackObservation Observation(
        DarknessAttackOwnerKey key,
        long revision
    )
    {
        return new DarknessAttackObservation(
            key,
            revision,
            SanityAuthorityRole.Host,
            GameplaySettleable: true,
            Paused: false,
            EnvironmentLightLevel.PitchBlack,
            EnvironmentLightEvidenceStatus.Confirmed,
            PitchBlackAuthorized: true,
            LightReason: "test.confirmed-pitch-black",
            UnsettleableReason: string.Empty
        );
    }

    private static string RuntimeResolutionContractPath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "DarknessAttack",
            "SmapiDarknessAttackResolutionService.cs"
        );

    private static string RuntimeCountdownContractPath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "DarknessAttack",
            "SmapiDarknessAttackService.cs"
        );

    private sealed record ResolutionFixture(
        DarknessAttackResolutionService Service,
        SequenceResolutionRandom Random,
        FakeDamageAuthority Damage,
        FakeSanityAuthority Sanity
    );

    private sealed class SequenceResolutionRandom : IDarknessAttackResolutionRandom
    {
        private readonly Queue<int> rolls;

        internal SequenceResolutionRandom(IEnumerable<int> rolls)
        {
            this.rolls = new Queue<int>(rolls);
        }

        internal int Calls { get; private set; }

        public int NextRoll100()
        {
            Calls++;
            return rolls.Count > 0 ? rolls.Dequeue() : 0;
        }
    }

    private sealed class FakeDamageAuthority : IDarknessAttackDamageAuthority
    {
        internal Func<DarknessAttackDamageRequest, DarknessAttackDamageReceipt>? Handler
        {
            get;
            set;
        }

        internal int Calls { get; private set; }

        public DarknessAttackDamageReceipt Settle(DarknessAttackDamageRequest request)
        {
            Calls++;
            if (Handler is not null)
                return Handler(request);
            var actualDamage = request.Operation == DarknessAttackDamageOperation.DefaultPhysical
                ? 0
                : 1;
            return DarknessAttackDamageReceipt.Settled(
                request,
                beforeHealth: 100,
                maximumHealth: 100,
                actualDamage,
                afterHealth: 100 - actualDamage,
                "test.damage-settled"
            );
        }
    }

    private sealed class FakeSanityAuthority : IDarknessAttackSanityAuthority
    {
        internal Func<DarknessAttackSanityRequest, DarknessAttackSanityReceipt>? Handler
        {
            get;
            set;
        }

        internal int Calls { get; private set; }

        public DarknessAttackSanityReceipt Apply(DarknessAttackSanityRequest request)
        {
            Calls++;
            if (Handler is not null)
                return Handler(request);
            return SanityReceipt(
                request,
                DarknessAttackSanityReceiptStatus.Applied,
                request.ExpectedRevision,
                request.ExpectedRevision + 1,
                beforeSanity: 80,
                afterSanity: 60,
                "test.sanity-applied"
            );
        }
    }

    private sealed class FakeCountdownClock : IDarknessAttackClock
    {
        public TimeSpan ElapsedGameTime => Elapsed;

        internal TimeSpan Elapsed { get; set; }
    }

    private sealed class FixedCountdownRandom : IDarknessAttackRandom
    {
        private readonly int value;

        internal FixedCountdownRandom(int value)
        {
            this.value = value;
        }

        public int NextInclusive(int minimum, int maximum)
        {
            return Math.Clamp(value, minimum, maximum);
        }
    }

    private sealed class FixedRequestIds : IDarknessAttackRequestIdSource
    {
        private readonly string prefix;
        private int next;

        internal FixedRequestIds(string prefix)
        {
            this.prefix = prefix;
        }

        public string NextRequestId(DarknessAttackOwnerKey key)
        {
            _ = key;
            next++;
            return $"{prefix}-{next}";
        }
    }
}
