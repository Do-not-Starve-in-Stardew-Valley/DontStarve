using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Combat;

public sealed class HostileAttackHitProcessorTests
{
    [Fact]
    public void Same_player_overlapping_frames_is_settled_once_per_instance()
    {
        var fixture = FixtureAtActiveFrame();
        var pipeline = new FakeLethalPipeline(100);

        Assert.True(fixture.Process(HostileAttackTestFactory.PlayerOne, "nonce-3", pipeline, out var first));
        Assert.Equal(80, pipeline.CurrentHealth);
        Assert.Equal(20, first.Result.AppliedDamage);
        Assert.True(
            fixture.Instance.HasSettledPlayer(
                long.Parse(HostileAttackTestFactory.PlayerOne)
            )
        );

        fixture.AdvanceToFrame(4);
        Assert.False(fixture.Process(HostileAttackTestFactory.PlayerOne, "nonce-4", pipeline, out var replay));
        Assert.Equal("hostile-shadow.attack-hit-player-already-settled", replay.Result.Reason);
        Assert.Equal(1, pipeline.Calls);
        Assert.Equal(80, pipeline.CurrentHealth);
    }

    [Fact]
    public void One_attack_instance_can_settle_each_overlapping_player_once()
    {
        var fixture = FixtureAtActiveFrame();
        var first = new FakeLethalPipeline(100);
        var second = new FakeLethalPipeline(100);

        Assert.True(fixture.Process(HostileAttackTestFactory.PlayerOne, "p1", first, out _));
        Assert.False(
            fixture.Instance.HasSettledPlayer(
                long.Parse(HostileAttackTestFactory.PlayerTwo)
            )
        );
        Assert.True(fixture.Process(HostileAttackTestFactory.PlayerTwo, "p2", second, out _));
        Assert.Equal(80, first.CurrentHealth);
        Assert.Equal(80, second.CurrentHealth);
        Assert.Equal(2, fixture.Instance.HitPlayerCount);
        Assert.True(
            fixture.Instance.HasSettledPlayer(
                long.Parse(HostileAttackTestFactory.PlayerTwo)
            )
        );
    }

    [Theory]
    [InlineData(20, 100, 80)]
    [InlineData(50, 100, 50)]
    [InlineData(20, 7, 0)]
    [InlineData(50, 12, 0)]
    public void Ordinary_damage_is_lethal_and_low_health_can_reach_zero(
        int damage,
        int health,
        int expected
    )
    {
        var fixture = FixtureAtActiveFrame(damage);
        var pipeline = new FakeLethalPipeline(health);

        Assert.True(fixture.Process(HostileAttackTestFactory.PlayerOne, "damage", pipeline, out var receipt));
        Assert.Equal(expected, pipeline.CurrentHealth);
        Assert.Equal(health, receipt.Result.HealthBefore);
        Assert.Equal(expected, receipt.Result.HealthAfter);
        Assert.True(receipt.Result.PipelineInvoked);
    }

    [Fact]
    public void Vanilla_immunity_still_consumes_the_instance_player_ledger()
    {
        var fixture = FixtureAtActiveFrame();
        var pipeline = new FakeLethalPipeline(100, suppressHealthChange: true);

        Assert.True(fixture.Process(HostileAttackTestFactory.PlayerOne, "immune-1", pipeline, out var first));
        Assert.Equal(HostileAttackReceiptStatus.SettledWithoutHealthChange, first.Result.Status);
        Assert.Equal(100, pipeline.CurrentHealth);

        fixture.AdvanceToFrame(4);
        Assert.False(fixture.Process(HostileAttackTestFactory.PlayerOne, "immune-2", pipeline, out var second));
        Assert.Equal("hostile-shadow.attack-hit-player-already-settled", second.Result.Reason);
        Assert.Equal(1, pipeline.Calls);
    }

    [Fact]
    public void Receipt_and_nonce_replay_are_rejected_without_second_damage()
    {
        var fixture = FixtureAtActiveFrame();
        var pipeline = new FakeLethalPipeline(100);

        Assert.True(fixture.Process(HostileAttackTestFactory.PlayerOne, "same", pipeline, out _));
        Assert.False(fixture.Process(HostileAttackTestFactory.PlayerOne, "same", pipeline, out var replay));
        Assert.Equal("hostile-shadow.attack-hit-nonce-replayed", replay.Result.Reason);
        Assert.Equal(1, pipeline.Calls);
        Assert.Equal(80, pipeline.CurrentHealth);
    }

    [Fact]
    public void Old_revision_cross_location_forged_target_and_bad_box_fail_closed()
    {
        AssertRejected(
            (request, context) => request.SessionId = "22222222222222222222222222222222",
            "wrong-session"
        );
        AssertRejected(
            (request, context) => request.EntityId++,
            "wrong-entity"
        );
        AssertRejected(
            (request, context) => request.ObservedEntityRevision--,
            "old-revision"
        );
        AssertRejected(
            (request, context) => request.LocationId = "Mine",
            "cross-location"
        );
        AssertRejected(
            (request, context) => request.TargetPlayerKey = HostileAttackTestFactory.PlayerTwo,
            "forged-target"
        );
        AssertRejected(
            (request, context) => context.StateId = HostileShadowStateIds.Chase,
            "wrong-state"
        );
        AssertRejected(
            (request, context) => request.AttackInstanceId += "-forged",
            "wrong-instance"
        );
        AssertRejected(
            (request, context) => request.ObservedAttackInstanceRevision++,
            "wrong-instance-revision"
        );
        AssertRejected(
            (request, context) => request.ObservedFrameNumber++,
            "out-of-order-frame"
        );
        AssertRejected(
            (request, context) => context.TargetBox = new HostileAttackRectangle(5000d, 5000d, 32d, 32d),
            "bad-box"
        );
        AssertRejected(
            (request, context) => context.TargetStanding = new HostileAttackPoint(5000d, 5000d),
            "out-of-range"
        );
    }

    [Fact]
    public void Inactive_frame_is_rejected_without_claiming_player()
    {
        var fixture = FixtureAtActiveFrame();
        fixture.Instance.FrameNumber = 2;
        var pipeline = new FakeLethalPipeline(100);
        var request = fixture.Request(HostileAttackTestFactory.PlayerOne, "inactive");
        var context = fixture.Context(HostileAttackTestFactory.PlayerOne);

        Assert.False(
            HostileAttackHitProcessor.TryProcess(
                request,
                HostileAttackTestFactory.PlayerOne,
                context,
                pipeline,
                out _
            )
        );
        Assert.Equal(0, pipeline.Calls);
        Assert.Equal(100, pipeline.CurrentHealth);
        Assert.Equal(0, fixture.Instance.HitPlayerCount);
    }

    private static void AssertRejected(
        Action<ShadowAttackHitRequest, HostileAttackHitContext> mutate,
        string nonce
    )
    {
        var fixture = FixtureAtActiveFrame();
        var pipeline = new FakeLethalPipeline(100);
        var request = fixture.Request(HostileAttackTestFactory.PlayerOne, nonce);
        var context = fixture.Context(HostileAttackTestFactory.PlayerOne);
        mutate(request, context);

        Assert.False(
            HostileAttackHitProcessor.TryProcess(
                request,
                HostileAttackTestFactory.PlayerOne,
                context,
                pipeline,
                out var receipt
            )
        );
        Assert.Equal(HostileAttackReceiptStatus.Rejected, receipt.Result.Status);
        Assert.False(receipt.Result.PipelineInvoked);
        Assert.Equal(0, pipeline.Calls);
        Assert.Equal(100, pipeline.CurrentHealth);
    }

    private static AttackFixture FixtureAtActiveFrame(int damage = 20)
    {
        var started = HostileAttackTestFactory.StartAttack(damage: damage);
        var duration = started.Definition.AttackFrameDurationMilliseconds;
        started.Machine.Advance(HostileAttackTestFactory.Input(), duration);
        var decision = started.Machine.Advance(HostileAttackTestFactory.Input(), duration);
        Assert.Equal(3, decision.AttackFrameNumber);
        return new AttackFixture(started.Machine, started.Definition, damage);
    }

    private sealed class AttackFixture
    {
        private readonly HostileAttackStateMachine machine;
        private readonly HostileAttackRuntimeDefinition definition;
        private readonly int damage;

        internal AttackFixture(
            HostileAttackStateMachine machine,
            HostileAttackRuntimeDefinition definition,
            int damage
        )
        {
            this.machine = machine;
            this.definition = definition;
            this.damage = damage;
        }

        internal HostileAttackInstance Instance => machine.CurrentInstance!;

        internal void AdvanceToFrame(int frame)
        {
            while (Instance.FrameNumber < frame)
            {
                machine.Advance(
                    HostileAttackTestFactory.Input(),
                    definition.AttackFrameDurationMilliseconds
                );
            }
        }

        internal bool Process(
            string playerKey,
            string nonce,
            FakeLethalPipeline pipeline,
            out HostileAttackReceipt receipt
        )
        {
            return HostileAttackHitProcessor.TryProcess(
                Request(playerKey, nonce),
                playerKey,
                Context(playerKey),
                pipeline,
                out receipt
            );
        }

        internal ShadowAttackHitRequest Request(string playerKey, string nonce)
        {
            return new ShadowAttackHitRequest
            {
                SessionId = HostileAttackTestFactory.SessionId,
                Nonce = nonce,
                EntityId = HostileAttackTestFactory.EntityId,
                TargetPlayerKey = playerKey,
                LocationId = HostileAttackTestFactory.LocationId,
                AttackInstanceId = Instance.InstanceId,
                ObservedEntityRevision = 20,
                ObservedAttackInstanceRevision = Instance.Revision,
                ObservedFrameNumber = Instance.FrameNumber,
            };
        }

        internal HostileAttackHitContext Context(string playerKey)
        {
            return new HostileAttackHitContext
            {
                SessionId = HostileAttackTestFactory.SessionId,
                EntityId = HostileAttackTestFactory.EntityId,
                CurrentEntityRevision = 20,
                LocationId = HostileAttackTestFactory.LocationId,
                StateId = HostileShadowStateIds.Attack,
                CurrentFrameNumber = Instance.FrameNumber,
                TargetPlayerId = long.Parse(playerKey),
                Instance = Instance,
                Definition = definition,
                AttackBox = new HostileAttackRectangle(0d, 0d, 128d, 128d),
                TargetBox = new HostileAttackRectangle(32d, 32d, 32d, 32d),
                AttackerStanding = new HostileAttackPoint(64d, 64d),
                TargetStanding = new HostileAttackPoint(96d, 64d),
                MaximumRangePixels = 128d,
                Damage = damage,
            };
        }
    }

    private sealed class FakeLethalPipeline : IHostileAttackLethalDamagePipeline
    {
        private readonly bool suppressHealthChange;

        internal FakeLethalPipeline(int health, bool suppressHealthChange = false)
        {
            CurrentHealth = health;
            this.suppressHealthChange = suppressHealthChange;
        }

        public int CurrentHealth { get; private set; }
        internal int Calls { get; private set; }

        public void ApplyOrdinaryDamage(int damage)
        {
            Calls++;
            if (!suppressHealthChange)
                CurrentHealth = Math.Max(0, CurrentHealth - damage);
        }
    }
}
