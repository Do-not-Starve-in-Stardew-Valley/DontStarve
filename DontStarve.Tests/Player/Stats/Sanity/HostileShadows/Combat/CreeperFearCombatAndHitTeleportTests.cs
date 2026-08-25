using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using DontStarve.Resource.Sanity;
using DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Profiles;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Combat;

public sealed class CreeperFearCombatAndHitTeleportTests
{
    [Fact]
    public void Shipped_profile_binds_hurt_attack_damage_range_and_immunity_once()
    {
        var profile = RuntimeProfile();
        var definition = Definition(profile);

        Assert.Equal(20, profile.BaseDamage);
        Assert.Equal(2d, profile.AttackRangeTiles);
        Assert.Equal(128d, profile.AttackRangePixels);
        Assert.Equal(new[] { "Knockback", "Frozen" }, profile.ImmunityTags);
        Assert.False(definition.IsActiveFrame(2));
        Assert.True(definition.IsActiveFrame(3));
        Assert.True(definition.IsActiveFrame(4));
        Assert.False(definition.IsActiveFrame(5));
        Assert.Equal(1d, definition.Motion.TotalAdvanceTiles);
        Assert.True(
            CreeperFearCombatImmunityPolicy.TryCreate(
                profile,
                out var immunity,
                out var immunityReason
            ),
            immunityReason
        );
        Assert.True(immunity!.BlocksKnockback);
        Assert.True(immunity.BlocksFrozen);
        Assert.False(CreeperFearCombatImmunityPolicy.IsFrozenStun(50));
        Assert.True(CreeperFearCombatImmunityPolicy.IsFrozenStun(51));
        Assert.True(CreeperFearCombatImmunityPolicy.IsFrozenStun(2000));

        Assert.True(
            HostileAttackCollisionResolver.TryCreateWorldHurtBox(
                definition,
                100d,
                200d,
                out var hurtBox
            )
        );
        Assert.Equal(
            new HostileAttackRectangle(116d, 392d, 224d, 192d),
            hurtBox
        );
    }

    [Theory]
    [InlineData("Down")]
    [InlineData("Right")]
    [InlineData("Up")]
    [InlineData("Left")]
    public void Shipped_attack_box_keeps_all_four_facings_centered_on_the_hurt_box(
        string facingId
    )
    {
        var definition = Definition(RuntimeProfile());
        var facing = facingId switch
        {
            "Down" => HostileAttackFacing.Down,
            "Right" => HostileAttackFacing.Right,
            "Up" => HostileAttackFacing.Up,
            "Left" => HostileAttackFacing.Left,
            _ => throw new ArgumentOutOfRangeException(nameof(facingId)),
        };

        Assert.True(
            HostileAttackCollisionResolver.TryCreateWorldAttackBox(
                definition,
                100d,
                200d,
                facing,
                out var box
            )
        );
        Assert.True(
            HostileAttackCollisionResolver.TryCreateWorldHurtBox(
                definition,
                100d,
                200d,
                out var hurtBox
            )
        );

        var rotated = facing is HostileAttackFacing.Right or HostileAttackFacing.Left;
        var expectedWidth =
            (rotated
                ? definition.AttackBoxSourcePx.Height
                : definition.AttackBoxSourcePx.Width) * definition.AttackDrawScale;
        var expectedHeight =
            (rotated
                ? definition.AttackBoxSourcePx.Width
                : definition.AttackBoxSourcePx.Height) * definition.AttackDrawScale;

        Assert.Equal(expectedWidth, box.Width, precision: 8);
        Assert.Equal(expectedHeight, box.Height, precision: 8);
        Assert.Equal(
            hurtBox.X + (hurtBox.Width / 2d),
            box.X + (box.Width / 2d),
            precision: 8
        );
        Assert.Equal(
            hurtBox.Y + (hurtBox.Height / 2d),
            box.Y + (box.Height / 2d),
            precision: 8
        );
    }

    [Theory]
    [InlineData(32d)]
    [InlineData(64d)]
    [InlineData(96d)]
    public void Shipped_motion_advances_one_tile_then_resets_for_every_tile_size(
        double tileSize
    )
    {
        var motion = Definition(RuntimeProfile(tileSize)).Motion;

        AssertAdvance(motion, 1, tileSize, tileSize * 0.5d);
        AssertAdvance(motion, 2, tileSize, tileSize);
        AssertAdvance(motion, 3, tileSize, tileSize);
        AssertAdvance(motion, 4, tileSize, tileSize);
        AssertAdvance(motion, 5, tileSize, 0d);
    }

    [Fact]
    public void Exact_two_tile_range_enters_attack_and_only_frames_three_four_are_active()
    {
        var profile = RuntimeProfile();
        var definition = Definition(profile);
        var machine = new HostileAttackStateMachine(
            definition,
            new CreeperFearAttackTransitionPolicy(
                HostileAttackTestFactory.EntityId,
                new FixedTransitionRandom(0.5d)
            )
        );
        var input = HostileAttackTestFactory.Input(
            inRange: 128d <= profile.AttackRangePixels,
            intervalSeconds: profile.AttackIntervalSeconds
        );

        var taunt = machine.Advance(input, definition.SpawnDurationMilliseconds);
        Assert.Equal(HostileShadowStateIds.Taunt, taunt.StateId);
        var first = machine.Advance(input, definition.TauntDurationMilliseconds);
        Assert.Equal(HostileShadowStateIds.Attack, first.StateId);
        Assert.Equal(1, first.AttackFrameNumber);
        Assert.Equal(132d, first.PositionX, precision: 8);

        var second = machine.Advance(input, definition.AttackFrameDurationMilliseconds);
        var third = machine.Advance(input, definition.AttackFrameDurationMilliseconds);
        var fourth = machine.Advance(input, definition.AttackFrameDurationMilliseconds);
        var reset = machine.Advance(input, definition.AttackFrameDurationMilliseconds);

        Assert.Equal(2, second.AttackFrameNumber);
        Assert.False(definition.IsActiveFrame(second.AttackFrameNumber));
        Assert.Equal(164d, second.PositionX, precision: 8);
        Assert.Equal(3, third.AttackFrameNumber);
        Assert.True(definition.IsActiveFrame(third.AttackFrameNumber));
        Assert.Equal(4, fourth.AttackFrameNumber);
        Assert.True(definition.IsActiveFrame(fourth.AttackFrameNumber));
        Assert.Equal(0, reset.AttackFrameNumber);
        Assert.Equal(HostileShadowStateIds.Chase, reset.StateId);
        Assert.Equal(100d, reset.PositionX, precision: 8);
        Assert.Equal(200d, reset.PositionY, precision: 8);
        Assert.Null(machine.CurrentInstance);
    }

    [Fact]
    public void One_creeper_attack_uses_profile_twenty_damage_once_per_player()
    {
        var profile = RuntimeProfile();
        var started = StartCreeperAttack(profile);
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

        var firstPipeline = new CountingLethalPipeline(7);
        var firstRequest = Request(instance, HostileAttackTestFactory.PlayerOne, "p1");
        Assert.True(
            Process(
                firstRequest,
                firstPipeline,
                profile,
                started.Definition,
                instance,
                attackBox,
                out var firstReceipt
            )
        );
        Assert.Equal(0, firstPipeline.CurrentHealth);
        Assert.Equal(20, firstReceipt.Result.RequestedDamage);
        Assert.Equal(7, firstReceipt.Result.AppliedDamage);

        var secondPipeline = new CountingLethalPipeline(40);
        Assert.True(
            Process(
                Request(instance, HostileAttackTestFactory.PlayerTwo, "p2"),
                secondPipeline,
                profile,
                started.Definition,
                instance,
                attackBox,
                out _
            )
        );
        Assert.Equal(20, secondPipeline.CurrentHealth);
        Assert.Equal(2, instance.HitPlayerCount);

        Assert.False(
            Process(
                firstRequest,
                firstPipeline,
                profile,
                started.Definition,
                instance,
                attackBox,
                out var replay
            )
        );
        Assert.Equal("hostile-shadow.attack-hit-nonce-replayed", replay.Result.Reason);
        Assert.Equal(1, firstPipeline.Calls);
    }

    [Fact]
    public void Incoming_hit_interrupts_active_creeper_attack_and_uses_four_tile_target()
    {
        var started = StartCreeperAttack(RuntimeProfile());
        var active = started.Machine.Advance(
            started.Input,
            started.Definition.AttackFrameDurationMilliseconds * 2d
        );
        var controller = new HostileShadowHitResponseController(started.Machine);

        var hit = controller.HandleHit(
            new HostileShadowHitResponseInput
            {
                SessionId = HostileAttackTestFactory.SessionId,
                EntityId = HostileAttackTestFactory.EntityId,
                ProposedRevision = 20,
                LocationId = HostileAttackTestFactory.LocationId,
                PositionX = active.PositionX,
                PositionY = active.PositionY,
                TileSizePixels = 64d,
                Health = 280,
                AttackerPlayerKey = HostileAttackTestFactory.PlayerOne,
                Map = new OpenTeleportMap(40, 40),
                Random = new FixedTeleportRandom(2468, 4, 0),
            }
        );

        Assert.True(hit.Valid, hit.Reason);
        Assert.True(hit.AttackInterrupted);
        Assert.Equal(HostileShadowStateIds.HitTeleport, hit.StateId);
        Assert.Equal(100d, hit.PositionX, precision: 8);
        Assert.Equal(200d, hit.PositionY, precision: 8);
        Assert.Null(started.Machine.CurrentInstance);
        var completed = controller.Advance(
            hit.PositionX,
            hit.PositionY,
            HostileShadowHitResponseController.TransitionDurationMilliseconds,
            hasTarget: true
        );
        Assert.Equal(HostileShadowStateIds.Chase, completed.StateId);
        Assert.Equal(100d + 4d * 64d, completed.PositionX, precision: 8);
        Assert.Equal(200d, completed.PositionY, precision: 8);
    }

    [Theory]
    [InlineData("missing-knockback", "creeper-fear.combat-immunity-required-tag-missing")]
    [InlineData("missing-frozen", "creeper-fear.combat-immunity-required-tag-missing")]
    [InlineData("duplicate", "creeper-fear.combat-immunity-tag-duplicate")]
    [InlineData("unknown", "creeper-fear.combat-immunity-tag-unsupported")]
    [InlineData("wrong-binding", "creeper-fear.combat-immunity-profile-invalid")]
    public void Immunity_profile_drift_fails_closed(string mutation, string expectedReason)
    {
        var original = RuntimeProfile();
        var tags = mutation switch
        {
            "missing-knockback" => new[] { "Frozen" },
            "missing-frozen" => new[] { "Knockback" },
            "duplicate" => new[] { "Knockback", "Frozen", "Frozen" },
            "unknown" => new[] { "Knockback", "Frozen", "Poison" },
            _ => new[] { "Knockback", "Frozen" },
        };
        var profile = CopyProfile(
            original,
            mutation == "wrong-binding"
                ? ShadowMonsterAssetBindingIds.Terrorbeak
                : original.AssetBindingId,
            tags
        );

        Assert.False(
            CreeperFearCombatImmunityPolicy.TryCreate(
                profile,
                out var policy,
                out var reason
            )
        );
        Assert.Null(policy);
        Assert.Equal(expectedReason, reason);
    }

    [Fact]
    public void Smapi_binding_keeps_creeper_on_public_stardew_immunity_seams()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "HostileShadowAuthority"
        );
        var monster = File.ReadAllText(Path.Combine(root, "HostileShadowMonster.cs"));
        var runtime = File.ReadAllText(
            Path.Combine(root, "SmapiHostileShadowWorldRuntime.cs")
        );
        var immunity = File.ReadAllText(
            Path.Combine(root, "CreeperFearCombatImmunityPolicy.cs")
        );

        Assert.Contains("public override void setTrajectory(Vector2 trajectory)", monster);
        Assert.Contains("Slipperiness = -1;", monster);
        Assert.Contains("IsFrozenStun(stunTime.Value)", monster);
        Assert.Contains("stunTime.Value = 0;", monster);
        Assert.Contains("KnockbackImmunityModDataKey", monster);
        Assert.Contains("FrozenImmunityModDataKey", monster);
        Assert.Contains("CreeperFearCombatImmunityPolicy.TryCreate", runtime);
        Assert.Contains("monster.ApplyCombatImmunity(combatImmunity);", runtime);
        Assert.Contains("ShadowMonsterAssetBindingIds.CreeperFear", runtime);
        Assert.Contains("CreeperFearCombatImmunityPolicy", immunity);

        var ordinarySources = string.Join("\n", monster, runtime, immunity);
        Assert.DoesNotContain(
            "INonLethalDamageService",
            ordinarySources,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "ApplyDamageUpToFloor",
            ordinarySources,
            StringComparison.Ordinal
        );
    }

    private static ShadowMonsterRuntimeProfile RuntimeProfile(double tileSize = 64d)
    {
        var validation = ShadowMonsterProfileTestFixture.ValidateShipped();
        var catalog = Assert.IsType<ShadowMonsterProfileCatalog>(validation.Catalog);
        Assert.True(
            catalog.TryGetProfile(
                ShadowMonsterDifficultyProfileIds.Compatible,
                out var difficulty
            )
        );
        var capability = ShadowMonsterProfileVersionAdapterFactory.Resolve(
            catalog.Schema,
            new ShadowMonsterProfileVersionFacts(
                "1.6.15.24356",
                ShadowMonsterProfileVersionFactIds.Stardew16MonstersReturnType,
                false,
                null
            )
        );
        var adapted = capability.Adapter!.Adapt(
            difficulty!,
            ShadowMonsterAssetBindingIds.CreeperFear,
            (int)tileSize
        );
        Assert.True(adapted.Success, adapted.Reason);
        return Assert.IsType<ShadowMonsterRuntimeProfile>(adapted.Profile);
    }

    private static HostileAttackRuntimeDefinition Definition(
        ShadowMonsterRuntimeProfile profile
    )
    {
        var catalog = HostileAttackTestFactory.MetadataCatalog();
        Assert.True(catalog.TryGet(profile.AssetBindingId, out var metadata));
        Assert.True(
            HostileAttackRuntimeDefinition.TryCreate(
                metadata,
                profile,
                out var definition,
                out var reason
            ),
            reason
        );
        return definition!;
    }

    private static (
        HostileAttackStateMachine Machine,
        HostileAttackRuntimeDefinition Definition,
        HostileAttackStateInput Input
    ) StartCreeperAttack(ShadowMonsterRuntimeProfile profile)
    {
        var definition = Definition(profile);
        var machine = new HostileAttackStateMachine(
            definition,
            new CreeperFearAttackTransitionPolicy(
                HostileAttackTestFactory.EntityId,
                new FixedTransitionRandom(0.5d)
            )
        );
        var input = HostileAttackTestFactory.Input(
            inRange: true,
            intervalSeconds: profile.AttackIntervalSeconds
        );
        var taunt = machine.Advance(input, definition.SpawnDurationMilliseconds);
        Assert.Equal(HostileShadowStateIds.Taunt, taunt.StateId);
        var attack = machine.Advance(input, definition.TauntDurationMilliseconds);
        Assert.Equal(HostileShadowStateIds.Attack, attack.StateId);
        return (machine, definition, input);
    }

    private static ShadowAttackHitRequest Request(
        HostileAttackInstance instance,
        string playerKey,
        string nonce
    )
    {
        return new ShadowAttackHitRequest
        {
            SessionId = HostileAttackTestFactory.SessionId,
            Nonce = nonce,
            EntityId = HostileAttackTestFactory.EntityId,
            TargetPlayerKey = playerKey,
            LocationId = HostileAttackTestFactory.LocationId,
            AttackInstanceId = instance.InstanceId,
            ObservedEntityRevision = 20,
            ObservedAttackInstanceRevision = instance.Revision,
            ObservedFrameNumber = instance.FrameNumber,
        };
    }

    private static bool Process(
        ShadowAttackHitRequest request,
        CountingLethalPipeline pipeline,
        ShadowMonsterRuntimeProfile profile,
        HostileAttackRuntimeDefinition definition,
        HostileAttackInstance instance,
        HostileAttackRectangle attackBox,
        out HostileAttackReceipt receipt
    )
    {
        return HostileAttackHitProcessor.TryProcess(
            request,
            request.TargetPlayerKey,
            new HostileAttackHitContext
            {
                SessionId = HostileAttackTestFactory.SessionId,
                EntityId = HostileAttackTestFactory.EntityId,
                CurrentEntityRevision = 20,
                LocationId = HostileAttackTestFactory.LocationId,
                StateId = HostileShadowStateIds.Attack,
                CurrentFrameNumber = instance.FrameNumber,
                Instance = instance,
                Definition = definition,
                AttackBox = attackBox,
                TargetBox = new HostileAttackRectangle(
                    attackBox.X + 16d,
                    attackBox.Y + 16d,
                    32d,
                    32d
                ),
                AttackerStanding = new HostileAttackPoint(100d, 200d),
                TargetStanding = new HostileAttackPoint(
                    100d + profile.AttackRangePixels,
                    200d
                ),
                MaximumRangePixels = profile.AttackRangePixels,
                Damage = profile.BaseDamage,
            },
            pipeline,
            out receipt
        );
    }

    private static ShadowMonsterRuntimeProfile CopyProfile(
        ShadowMonsterRuntimeProfile source,
        string bindingId,
        IReadOnlyList<string> tags
    )
    {
        return new ShadowMonsterRuntimeProfile(
            source.DifficultyProfileId,
            bindingId,
            source.AdapterVersion,
            source.MaxHealth,
            source.BaseDamage,
            source.MovementSpeed,
            source.Defense,
            source.DetectionRadiusTiles,
            source.DetectionRadiusPixels,
            source.AttackRangeTiles,
            source.AttackRangePixels,
            source.AttackIntervalSeconds,
            source.NaturalDespawnGameHours,
            source.DisplayNameKey,
            source.WallTraversalMode,
            tags,
            source.DropTable,
            source.SanityReward,
            source.AnimationProfileId,
            source.CueSetId,
            source.AttackMotionPolicyId,
            source.PostAttackPolicyId,
            source.ExperienceValue,
            source.KillCounterId
        );
    }

    private static void AssertAdvance(
        HostileAttackMotionPolicy motion,
        int frame,
        double tileSize,
        double expected
    )
    {
        Assert.True(
            motion.TryGetCumulativeWorldAdvance(frame, tileSize, out var actual)
        );
        Assert.Equal(expected, actual, precision: 8);
    }

    private sealed class FixedTransitionRandom : IHostileAttackTransitionRandom
    {
        private readonly double sample;

        internal FixedTransitionRandom(double sample)
        {
            this.sample = sample;
        }

        public double NextSample()
        {
            return sample;
        }
    }

    private sealed class CountingLethalPipeline : IHostileAttackLethalDamagePipeline
    {
        internal CountingLethalPipeline(int health)
        {
            CurrentHealth = health;
        }

        public int CurrentHealth { get; private set; }
        internal int Calls { get; private set; }

        public void ApplyOrdinaryDamage(int damage)
        {
            Calls++;
            CurrentHealth = Math.Max(0, CurrentHealth - damage);
        }
    }

    private sealed class FixedTeleportRandom : IHostileShadowTeleportRandom
    {
        private readonly Queue<int> values;

        internal FixedTeleportRandom(int seed, params int[] values)
        {
            Seed = seed;
            this.values = new Queue<int>(values);
        }

        public int Seed { get; }

        public int Next(int minimumInclusive, int maximumExclusive)
        {
            var value = values.Dequeue();
            Assert.InRange(value, minimumInclusive, maximumExclusive - 1);
            return value;
        }
    }

    private sealed class OpenTeleportMap : IHostileShadowTeleportMap
    {
        private readonly int width;
        private readonly int height;

        internal OpenTeleportMap(int width, int height)
        {
            this.width = width;
            this.height = height;
        }

        public bool IsLocationValid(string expectedLocationId)
        {
            return string.Equals(
                expectedLocationId,
                HostileAttackTestFactory.LocationId,
                StringComparison.Ordinal
            );
        }

        public bool IsTileOnMap(int tileX, int tileY)
        {
            return tileX >= 0 && tileY >= 0 && tileX < width && tileY < height;
        }

        public bool IsTileLocationOpen(int tileX, int tileY)
        {
            return true;
        }

        public bool IsTilePassable(int tileX, int tileY)
        {
            return true;
        }
    }
}
