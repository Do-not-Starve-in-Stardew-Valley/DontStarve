using DontStarve.Player.Stats.Sanity.HostileShadows.Multiplayer;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using DontStarve.Resource.Sanity;
using System.Text.Json.Nodes;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Combat;

public sealed class HostileAttackMetadataAndBoundaryTests
{
    [Theory]
    [InlineData("sanity.binding.creeper-fear", "sanity.animation.creeper-fear.attack")]
    [InlineData("sanity.binding.terrorbeak", "sanity.animation.terrorbeak.attack")]
    public void Shipped_metadata_exposes_frozen_animation_collision_and_motion_contract(
        string bindingId,
        string attackAnimationId
    )
    {
        var catalog = HostileAttackTestFactory.MetadataCatalog();
        Assert.True(catalog.TryGet(bindingId, out var metadata));
        Assert.NotNull(metadata);
        Assert.Equal(attackAnimationId, metadata!.Attack.AnimationId);
        Assert.NotNull(metadata.HitResponseVisual);
        Assert.EndsWith(
            ".death",
            metadata.HitResponseVisual!.AnimationId,
            StringComparison.Ordinal
        );
        Assert.Equal(4, metadata.HitResponseVisual.FrameCount);
        Assert.Equal(new[] { 3, 4 }, metadata.Attack.HitFrames);
        Assert.Equal(new[] { 3, 4 }, metadata.Collision.AttackActiveFrames);
        Assert.True(metadata.Collision.HurtBoxSourcePx.Width > 0);
        Assert.True(metadata.Collision.HurtBoxSourcePx.Height > 0);
        Assert.Equal("ActorOriginRelativeSourcePx", metadata.Collision.CoordinateSpace);
        Assert.Equal("sanity.attack-motion.one-tile-v1", metadata.Motion.PolicyId);
        Assert.Equal(new[] { 0.5d, 0.5d, 0d, 0d }, metadata.Motion.FrameAdvanceTiles);
        Assert.Equal(1d, metadata.Motion.TotalAdvanceTiles);
        Assert.True(metadata.Motion.ResetAfterAnimation);
    }

    [Fact]
    public void Missing_optional_death_visual_does_not_remove_hit_response_business_contract()
    {
        var data = Path.Combine(
            AppContext.BaseDirectory,
            "ShippedMod",
            "Asset",
            "Sanity",
            "Data"
        );
        var animations = JsonNode.Parse(
            File.ReadAllText(Path.Combine(data, "animations.json"))
        )!.AsObject();
        foreach (var profile in animations["AnimationProfiles"]!.AsArray())
        {
            var states = profile!["States"]!.AsArray();
            for (var index = states.Count - 1; index >= 0; index--)
            {
                var animationId = states[index]!["AnimationId"]!.GetValue<string>();
                if (animationId.EndsWith(".death", StringComparison.Ordinal))
                    states.RemoveAt(index);
            }
        }
        var bindings = File.ReadAllText(
            Path.Combine(data, "resource-bindings.json")
        );

        Assert.True(
            SanityHostileAttackMetadataCatalog.TryParse(
                animations.ToJsonString(),
                bindings,
                out var catalog,
                out var reason
            ),
            reason
        );
        Assert.True(catalog!.TryGet("sanity.binding.creeper-fear", out var metadata));
        Assert.Null(metadata!.HitResponseVisual);
        var runtimeProfile = HostileAttackTestFactory.Profile(
            "sanity.binding.creeper-fear",
            metadata
        );
        Assert.True(
            HostileAttackRuntimeDefinition.TryCreate(
                metadata,
                runtimeProfile,
                out var definition,
                out reason
            ),
            reason
        );
        Assert.NotNull(definition);
    }

    [Fact]
    public void Frozen_source_pixel_hurt_box_maps_to_a_nonzero_world_box()
    {
        var definition = HostileAttackTestFactory.Definition();

        Assert.True(
            HostileAttackCollisionResolver.TryCreateWorldHurtBox(
                definition,
                100d,
                200d,
                out var box
            )
        );
        Assert.Equal(-12d, box.X, precision: 8);
        Assert.Equal(200d, box.Y, precision: 8);
        Assert.Equal(224d, box.Width, precision: 8);
        Assert.Equal(192d, box.Height, precision: 8);
    }

    [Fact]
    public void Runtime_loader_reuses_one_cached_strict_metadata_registry()
    {
        var deploymentRoot = Path.Combine(AppContext.BaseDirectory, "ShippedMod");
        using var loader = new SanityRuntimeResourceLoader(
            deploymentRoot,
            new UnusedResourceFactory()
        );
        loader.Prime();

        Assert.True(
            loader.TryGetHostileAttackMetadata(
                "sanity.binding.creeper-fear",
                out var first,
                out var firstReason
            ),
            firstReason
        );
        Assert.True(
            loader.TryGetHostileAttackMetadata(
                "sanity.binding.terrorbeak",
                out var second,
                out var secondReason
            ),
            secondReason
        );
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, loader.Snapshot().VisualMetadataParseCount);
    }

    [Fact]
    public void Protocol_rejects_forged_sender_and_old_schema()
    {
        var request = new ShadowAttackHitRequest
        {
            SessionId = HostileAttackTestFactory.SessionId,
            Nonce = "nonce",
            EntityId = HostileAttackTestFactory.EntityId,
            TargetPlayerKey = HostileAttackTestFactory.PlayerOne,
            LocationId = HostileAttackTestFactory.LocationId,
            AttackInstanceId = "attack",
            ObservedEntityRevision = 2,
            ObservedAttackInstanceRevision = 1,
            ObservedFrameNumber = 3,
        };

        Assert.True(
            HostileShadowProtocol.IsValidAttackHitRequest(
                request,
                HostileAttackTestFactory.PlayerOne,
                HostileAttackTestFactory.SessionId,
                out _
            )
        );
        Assert.False(
            HostileShadowProtocol.IsValidAttackHitRequest(
                request,
                HostileAttackTestFactory.PlayerTwo,
                HostileAttackTestFactory.SessionId,
                out _
            )
        );
        request.SchemaVersion--;
        Assert.False(
            HostileShadowProtocol.IsValidAttackHitRequest(
                request,
                HostileAttackTestFactory.PlayerOne,
                HostileAttackTestFactory.SessionId,
                out _
            )
        );
    }

    [Fact]
    public void Client_revision_store_consumes_host_attack_instance_and_frame_only()
    {
        var state = AttackState(revision: 5, frame: 3);
        var store = new ShadowStateRevisionStore();
        var full = new ShadowStateSnapshotMessage
        {
            SessionId = HostileAttackTestFactory.SessionId,
            LocationId = "Farm",
            Trigger = ShadowSnapshotTrigger.Join,
            Revision = 5,
            Entities = new List<ShadowStateSnapshot> { state },
        };

        Assert.Equal(ShadowRevisionApplyStatus.Applied, store.ApplyFull(full).Status);
        Assert.True(store.TryGet(HostileAttackTestFactory.EntityId, out var mirrored));
        Assert.Equal("attack-instance", mirrored!.AttackInstanceId);
        Assert.Equal(3, mirrored.AttackFrameNumber);

        var next = AttackState(revision: 6, frame: 4);
        var delta = new ShadowStateDeltaMessage
        {
            SessionId = HostileAttackTestFactory.SessionId,
            BaseRevision = 5,
            Revision = 6,
            Change = new ShadowStateDelta
            {
                Kind = ShadowStateDeltaKind.Updated,
                EntityId = HostileAttackTestFactory.EntityId,
                State = next,
                Reason = "hostile-shadow.attack-frame",
                SettlementEligible = false,
            },
        };
        Assert.Equal(ShadowRevisionApplyStatus.Applied, store.ApplyDelta(delta).Status);
        Assert.True(store.TryGet(HostileAttackTestFactory.EntityId, out mirrored));
        Assert.Equal(4, mirrored!.AttackFrameNumber);
        Assert.Equal(6, mirrored.Revision);
    }

    [Fact]
    public void Attack_runtime_and_adapter_do_not_reference_the_separate_damage_family()
    {
        var contractRoot = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "HostileShadowAuthority"
        );
        var sources = string.Join(
            "\n",
            File.ReadAllText(Path.Combine(contractRoot, "HostileAttackDamage.cs")),
            File.ReadAllText(Path.Combine(contractRoot, "HostileAttackRuntime.cs")),
            File.ReadAllText(Path.Combine(contractRoot, "HostileAttackGeometry.cs")),
            File.ReadAllText(Path.Combine(contractRoot, "HostileAttackInstance.cs")),
            File.ReadAllText(Path.Combine(contractRoot, "HostileAttackStateMachine.cs")),
            File.ReadAllText(Path.Combine(contractRoot, "SmapiHostileAttackDamageAdapter.cs")),
            File.ReadAllText(Path.Combine(contractRoot, "SmapiHostileAttackCombatService.cs")),
            File.ReadAllText(Path.Combine(contractRoot, "SmapiHostileShadowWorldRuntime.cs"))
        );
        Assert.DoesNotContain("INonLethalDamageService", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyDamageUpToFloor", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("ReduceToFloor", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("20%", sources, StringComparison.Ordinal);
        Assert.Contains("target.takeDamage", sources, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_receipt_contains_no_threshold_semantics()
    {
        var propertyNames = typeof(HostileAttackReceipt)
            .GetProperties(
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic
            )
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(
            propertyNames,
            name => name.Contains("Floor", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Contains(nameof(HostileAttackReceipt.AttackInstanceId), propertyNames);
        Assert.Contains(nameof(HostileAttackReceipt.EntityRevision), propertyNames);
        Assert.Contains(nameof(HostileAttackReceipt.Result), propertyNames);
        Assert.Equal(5, propertyNames.Length);
    }

    private static ShadowStateSnapshot AttackState(long revision, int frame)
    {
        return new ShadowStateSnapshot
        {
            EntityId = HostileAttackTestFactory.EntityId,
            OwnerPlayerKey = HostileAttackTestFactory.PlayerOne,
            LocationId = HostileAttackTestFactory.LocationId,
            DifficultyProfileId = "test",
            AssetBindingId = "sanity.binding.creeper-fear",
            StateId = HostileShadowStateIds.Attack,
            TargetPlayerKey = HostileAttackTestFactory.PlayerOne,
            PositionX = 100d,
            PositionY = 200d,
            Health = 100,
            MaxHealth = 100,
            AttackInstanceId = "attack-instance",
            AttackInstanceRevision = 5,
            AttackFrameNumber = frame,
            Revision = revision,
        };
    }

    private sealed class UnusedResourceFactory : ISanityPhysicalResourceFactory
    {
        public SanityPhysicalResourceCreationResult CreateTexture(
            string path,
            byte[] bytes
        )
        {
            return SanityPhysicalResourceCreationResult.Failed(
                "test.unused",
                "No physical texture should be created by metadata lookup."
            );
        }

        public SanityPhysicalResourceCreationResult CreateSoundEffect(
            string path,
            byte[] bytes
        )
        {
            return SanityPhysicalResourceCreationResult.Failed(
                "test.unused",
                "No physical audio should be created by metadata lookup."
            );
        }
    }
}
