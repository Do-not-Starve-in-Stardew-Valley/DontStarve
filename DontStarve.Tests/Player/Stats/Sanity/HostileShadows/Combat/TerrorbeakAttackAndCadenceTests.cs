using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Combat;

public sealed class TerrorbeakAttackAndCadenceTests
{
    [Fact]
    public void Shipped_attack_uses_400ms_animation_12s_interval_06s_post_taunt_delay_and_frames_three_four()
    {
        var profile = RuntimeProfile();
        var definition = HostileAttackTestFactory.Definition(profile);

        Assert.Equal(400, profile.MaxHealth);
        Assert.Equal(50, profile.BaseDamage);
        Assert.Equal(1.2d, profile.AttackIntervalSeconds);
        Assert.Equal(0.6d, TerrorbeakAttackTransitionPolicy.DelayAfterTauntSeconds);
        Assert.Equal(4, definition.AttackFrameCount);
        Assert.Equal(100, definition.AttackFrameDurationMilliseconds);
        Assert.Equal(
            400,
            definition.AttackFrameCount
                * definition.AttackFrameDurationMilliseconds
        );
        Assert.False(definition.IsActiveFrame(2));
        Assert.True(definition.IsActiveFrame(3));
        Assert.True(definition.IsActiveFrame(4));
        Assert.False(definition.IsActiveFrame(5));
    }

    [Theory]
    [InlineData(32d)]
    [InlineData(64d)]
    [InlineData(96d)]
    public void Shipped_motion_advances_exactly_one_current_tile_then_resets(
        double tileSize
    )
    {
        var definition = HostileAttackTestFactory.Definition(
            HostileAttackTestFactory.ShippedRuntimeProfile(
                ShadowMonsterAssetBindingIds.Terrorbeak,
                (int)tileSize
            )
        );

        AssertAdvance(definition.Motion, 1, tileSize, tileSize * 0.5d);
        AssertAdvance(definition.Motion, 2, tileSize, tileSize);
        AssertAdvance(definition.Motion, 3, tileSize, tileSize);
        AssertAdvance(definition.Motion, 4, tileSize, tileSize);
        AssertAdvance(definition.Motion, 5, tileSize, 0d);
    }

    [Theory]
    [InlineData(200d, 3)]
    [InlineData(300d, 4)]
    [InlineData(399d, 4)]
    public void Large_in_animation_steps_land_on_an_active_boundary(
        double elapsedMilliseconds,
        int expectedFrame
    )
    {
        var started = StartAttack(sample: 0.25d);
        var decision = started.Machine.Advance(
            started.Input,
            elapsedMilliseconds
        );

        Assert.Equal(HostileShadowStateIds.Attack, decision.StateId);
        Assert.Equal(expectedFrame, decision.AttackFrameNumber);
        Assert.True(started.Definition.IsActiveFrame(expectedFrame));
    }

    [Theory]
    [InlineData(0d, true, 600d)]
    [InlineData(0.249999d, true, 600d)]
    [InlineData(0.25d, false, 1200d)]
    [InlineData(0.999999d, false, 1200d)]
    public void Attack_completion_waits_after_taunt_or_for_profile_interval(
        double sample,
        bool entersTaunt,
        double waitMilliseconds
    )
    {
        var started = StartAttack(sample);
        for (var frame = 0; frame < started.Definition.AttackFrameCount; frame++)
        {
            started.Machine.Advance(
                started.Input,
                started.Definition.AttackFrameDurationMilliseconds
            );
        }

        Assert.Equal(
            entersTaunt ? HostileShadowStateIds.Taunt : HostileShadowStateIds.Chase,
            started.Machine.StateId
        );
        Assert.Equal(1, started.Random.CallCount);

        var duplicate = started.Machine.Advance(started.Input, 0d);
        Assert.Equal(
            entersTaunt ? HostileShadowStateIds.Taunt : HostileShadowStateIds.Chase,
            duplicate.StateId
        );
        Assert.Equal(1, started.Random.CallCount);

        var nextRevisionInput = HostileAttackTestFactory.Input(
            intervalSeconds: started.Profile.AttackIntervalSeconds,
            proposedAttackRevision: HostileAttackTestFactory.AttackRevision + 1
        );
        if (entersTaunt)
        {
            var afterTaunt = started.Machine.Advance(
                nextRevisionInput,
                started.Definition.TauntDurationMilliseconds
            );
            Assert.Equal(HostileShadowStateIds.Chase, afterTaunt.StateId);
        }

        var waiting = started.Machine.Advance(
            nextRevisionInput,
            waitMilliseconds - 1d
        );
        var nextAttack = started.Machine.Advance(nextRevisionInput, 1d);
        Assert.Equal(HostileShadowStateIds.Chase, waiting.StateId);
        Assert.Equal(HostileShadowStateIds.Attack, nextAttack.StateId);
        Assert.Equal(
            HostileAttackTestFactory.AttackRevision + 1,
            nextAttack.AttackInstanceRevision
        );
        Assert.Equal(1, started.Random.CallCount);
    }

    [Fact]
    public void Fixed_game_ticks_pause_without_progress_and_visit_both_active_frames()
    {
        var started = StartAttack(sample: 0.25d);
        var paused = started.Machine.Advance(started.Input, 0d);
        Assert.Equal(1, paused.AttackFrameNumber);

        var activeFrames = new HashSet<int>();
        var ticks = 0;
        while (
            string.Equals(
                started.Machine.StateId,
                HostileShadowStateIds.Attack,
                StringComparison.Ordinal
            )
            && ticks < 60
        )
        {
            var decision = started.Machine.Advance(
                started.Input,
                1000d / HostileShadowTargetingLimits.NominalTicksPerSecond
            );
            if (started.Definition.IsActiveFrame(decision.AttackFrameNumber))
                activeFrames.Add(decision.AttackFrameNumber);
            ticks++;
        }

        Assert.Contains(3, activeFrames);
        Assert.Contains(4, activeFrames);
        Assert.Equal(HostileShadowStateIds.Chase, started.Machine.StateId);
        Assert.InRange(ticks, 20, 30);

        var runtime = Contract("SmapiHostileShadowWorldRuntime.cs");
        Assert.Contains("FixedUpdateSeconds * 1000d", runtime);
        Assert.DoesNotContain("ElapsedGameTime", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void Fifty_ordinary_damage_reaches_zero_and_replay_never_reenters_pipeline()
    {
        var started = StartAttack(sample: 0.25d);
        var frameThree = started.Machine.Advance(
            started.Input,
            started.Definition.AttackFrameDurationMilliseconds * 2d
        );
        var instance = Assert.IsType<HostileAttackInstance>(
            started.Machine.CurrentInstance
        );
        Assert.Equal(3, instance.FrameNumber);
        Assert.True(
            HostileAttackCollisionResolver.TryCreateWorldAttackBox(
                started.Definition,
                frameThree.PositionX,
                frameThree.PositionY,
                instance.Facing,
                out var attackBox
            )
        );

        var pipeline = new OrdinaryDamageProbe(40);
        var firstRequest = Request(instance, "terrorbeak-frame-3");
        Assert.True(
            Process(
                firstRequest,
                pipeline,
                started,
                instance,
                attackBox,
                out var first
            )
        );
        Assert.Equal(HostileAttackReceiptStatus.Applied, first.Result.Status);
        Assert.Equal(50, first.Result.RequestedDamage);
        Assert.Equal(40, first.Result.HealthBefore);
        Assert.Equal(0, first.Result.HealthAfter);
        Assert.Equal(40, first.Result.AppliedDamage);

        Assert.False(
            Process(
                firstRequest,
                pipeline,
                started,
                instance,
                attackBox,
                out var replay
            )
        );
        Assert.Equal("hostile-shadow.attack-hit-nonce-replayed", replay.Result.Reason);

        started.Machine.Advance(
            started.Input,
            started.Definition.AttackFrameDurationMilliseconds
        );
        Assert.Equal(4, instance.FrameNumber);
        Assert.False(
            Process(
                Request(instance, "terrorbeak-frame-4"),
                pipeline,
                started,
                instance,
                attackBox,
                out var secondFrame
            )
        );
        Assert.Equal(
            "hostile-shadow.attack-hit-player-already-settled",
            secondFrame.Result.Reason
        );
        Assert.Equal(1, pipeline.OrdinaryCalls);
        Assert.Equal(0, pipeline.NonLethalCalls);
        Assert.Equal(0, pipeline.FloorCalls);

        var adapter = Contract("SmapiHostileAttackDamageAdapter.cs");
        Assert.Contains("target.takeDamage", adapter);
        Assert.DoesNotContain("INonLethalDamageService", adapter);
        Assert.DoesNotContain("ApplyDamageUpToFloor", adapter);
        Assert.DoesNotContain("ReduceToFloor", adapter);
    }

    private static AttackStart StartAttack(double sample)
    {
        var profile = RuntimeProfile();
        var definition = HostileAttackTestFactory.Definition(profile);
        var random = new SequenceTransitionRandom(sample);
        var machine = new HostileAttackStateMachine(
            definition,
            new TerrorbeakAttackTransitionPolicy(
                HostileAttackTestFactory.EntityId,
                random
            )
        );
        var input = HostileAttackTestFactory.Input(
            intervalSeconds: profile.AttackIntervalSeconds
        );
        var taunt = machine.Advance(input, definition.SpawnDurationMilliseconds);
        Assert.Equal(HostileShadowStateIds.Taunt, taunt.StateId);
        var attack = machine.Advance(input, definition.TauntDurationMilliseconds);
        Assert.Equal(HostileShadowStateIds.Attack, attack.StateId);
        return new AttackStart(machine, definition, profile, input, random);
    }

    private static ShadowMonsterRuntimeProfile RuntimeProfile()
    {
        return HostileAttackTestFactory.ShippedRuntimeProfile(
            ShadowMonsterAssetBindingIds.Terrorbeak
        );
    }

    private static ShadowAttackHitRequest Request(
        HostileAttackInstance instance,
        string nonce
    )
    {
        return new ShadowAttackHitRequest
        {
            SessionId = HostileAttackTestFactory.SessionId,
            Nonce = nonce,
            EntityId = HostileAttackTestFactory.EntityId,
            TargetPlayerKey = HostileAttackTestFactory.PlayerOne,
            LocationId = HostileAttackTestFactory.LocationId,
            AttackInstanceId = instance.InstanceId,
            ObservedEntityRevision = 20,
            ObservedAttackInstanceRevision = instance.Revision,
            ObservedFrameNumber = instance.FrameNumber,
        };
    }

    private static bool Process(
        ShadowAttackHitRequest request,
        OrdinaryDamageProbe pipeline,
        AttackStart started,
        HostileAttackInstance instance,
        HostileAttackRectangle attackBox,
        out HostileAttackReceipt receipt
    )
    {
        return HostileAttackHitProcessor.TryProcess(
            request,
            HostileAttackTestFactory.PlayerOne,
            new HostileAttackHitContext
            {
                SessionId = HostileAttackTestFactory.SessionId,
                EntityId = HostileAttackTestFactory.EntityId,
                CurrentEntityRevision = 20,
                LocationId = HostileAttackTestFactory.LocationId,
                StateId = HostileShadowStateIds.Attack,
                CurrentFrameNumber = instance.FrameNumber,
                Instance = instance,
                Definition = started.Definition,
                AttackBox = attackBox,
                TargetBox = new HostileAttackRectangle(
                    attackBox.X + 16d,
                    attackBox.Y + 16d,
                    32d,
                    32d
                ),
                AttackerStanding = new HostileAttackPoint(100d, 200d),
                TargetStanding = new HostileAttackPoint(
                    100d + started.Profile.AttackRangePixels,
                    200d
                ),
                MaximumRangePixels = started.Profile.AttackRangePixels,
                Damage = started.Profile.BaseDamage,
            },
            pipeline,
            out receipt
        );
    }

    private static void AssertAdvance(
        HostileAttackMotionPolicy policy,
        int frame,
        double tileSize,
        double expected
    )
    {
        Assert.True(
            policy.TryGetCumulativeWorldAdvance(frame, tileSize, out var actual)
        );
        Assert.Equal(expected, actual, precision: 8);
    }

    private static string Contract(string fileName)
    {
        return File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "HostileShadowAuthority",
                fileName
            )
        );
    }

    private sealed record AttackStart(
        HostileAttackStateMachine Machine,
        HostileAttackRuntimeDefinition Definition,
        ShadowMonsterRuntimeProfile Profile,
        HostileAttackStateInput Input,
        SequenceTransitionRandom Random
    );

    private sealed class SequenceTransitionRandom : IHostileAttackTransitionRandom
    {
        private readonly Queue<double> samples;

        internal SequenceTransitionRandom(params double[] samples)
        {
            this.samples = new Queue<double>(samples);
        }

        internal int CallCount { get; private set; }

        public double NextSample()
        {
            CallCount++;
            if (!samples.TryDequeue(out var sample))
                throw new InvalidOperationException("No RNG sample remains.");
            return sample;
        }
    }

    private sealed class OrdinaryDamageProbe : IHostileAttackLethalDamagePipeline
    {
        internal OrdinaryDamageProbe(int currentHealth)
        {
            CurrentHealth = currentHealth;
        }

        public int CurrentHealth { get; private set; }
        internal int OrdinaryCalls { get; private set; }
        internal int NonLethalCalls { get; private set; }
        internal int FloorCalls { get; private set; }

        public void ApplyOrdinaryDamage(int damage)
        {
            OrdinaryCalls++;
            CurrentHealth = Math.Max(0, CurrentHealth - damage);
        }
    }
}
