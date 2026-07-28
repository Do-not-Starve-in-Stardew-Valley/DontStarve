using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using DontStarve.Resource.Sanity;
using DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Profiles;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Combat;

internal static class HostileAttackTestFactory
{
    internal const string SessionId = "11111111111111111111111111111111";
    internal const string PlayerOne = "101";
    internal const string PlayerTwo = "202";
    internal const string LocationId = "Farm";
    internal const long EntityId = 77;
    internal const long AttackRevision = 12;

    internal static HostileAttackRuntimeDefinition Definition(
        string bindingId = "sanity.binding.creeper-fear",
        int damage = 20,
        double intervalSeconds = 0d
    )
    {
        var catalog = MetadataCatalog();
        Assert.True(catalog.TryGet(bindingId, out var metadata));
        Assert.NotNull(metadata);
        var profile = Profile(
            bindingId,
            metadata!,
            damage,
            intervalSeconds
        );
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

    internal static ShadowMonsterRuntimeProfile Profile(
        string bindingId,
        SanityHostileAttackMetadataDefinition metadata,
        int damage = 20,
        double intervalSeconds = 0d
    )
    {
        return new ShadowMonsterRuntimeProfile(
            "sanity.shadow-monster.test",
            bindingId,
            adapterVersion: 1,
            maxHealth: 100,
            baseDamage: damage,
            movementSpeed: 2d,
            defense: 0,
            detectionRadiusTiles: 20d,
            detectionRadiusPixels: 1280d,
            attackRangeTiles: 8d,
            attackRangePixels: 512d,
            attackIntervalSeconds: intervalSeconds,
            naturalDespawnGameHours: 2d,
            displayNameKey: "test",
            wallTraversalMode: "None",
            immunityTags: Array.Empty<string>(),
            dropTable: new ShadowMonsterDropTable(
                1,
                "test",
                "test",
                0,
                0,
                0d
            ),
            sanityReward: 0,
            animationProfileId: metadata.AnimationProfileId,
            cueSetId: "test",
            attackMotionPolicyId: metadata.AttackMotionPolicyId,
            postAttackPolicyId: "test",
            experienceValue: 0,
            killCounterId: null
        );
    }

    internal static ShadowMonsterRuntimeProfile ShippedRuntimeProfile(
        string bindingId,
        int tileSize = 64
    )
    {
        var validation = ShadowMonsterProfileTestFixture.ValidateShipped();
        var catalog = Assert.IsType<ShadowMonsterProfileCatalog>(
            validation.Catalog
        );
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
            bindingId,
            tileSize
        );
        Assert.True(adapted.Success, adapted.Reason);
        return Assert.IsType<ShadowMonsterRuntimeProfile>(adapted.Profile);
    }

    internal static HostileAttackRuntimeDefinition Definition(
        ShadowMonsterRuntimeProfile profile
    )
    {
        var catalog = MetadataCatalog();
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

    internal static HostileAttackStateInput Input(
        double tileSize = 64d,
        bool hasTarget = true,
        bool inRange = true,
        double intervalSeconds = 0d,
        string targetPlayerKey = PlayerOne,
        long proposedAttackRevision = AttackRevision
    )
    {
        return HostileAttackStateInput.Capture(
            SessionId,
            EntityId,
            proposedAttackRevision,
            LocationId,
            targetPlayerKey,
            hasTarget,
            inRange,
            100d,
            200d,
            132d,
            248d,
            196d,
            248d,
            tileSize,
            intervalSeconds
        );
    }

    internal static (
        HostileAttackStateMachine Machine,
        HostileAttackRuntimeDefinition Definition,
        HostileAttackStateDecision Decision
    ) StartAttack(
        double tileSize = 64d,
        IHostileAttackTransitionPolicy? policy = null,
        int damage = 20,
        double intervalSeconds = 0d
    )
    {
        var definition = Definition(damage: damage, intervalSeconds: intervalSeconds);
        var machine = new HostileAttackStateMachine(definition, policy);
        var decision = machine.Advance(
            Input(tileSize, intervalSeconds: intervalSeconds),
            definition.SpawnDurationMilliseconds
        );
        Assert.True(decision.Valid, decision.Reason);
        Assert.Equal(HostileShadowStateIds.Attack, decision.StateId);
        Assert.NotNull(machine.CurrentInstance);
        return (machine, definition, decision);
    }

    internal static SanityHostileAttackMetadataCatalog MetadataCatalog()
    {
        var data = Path.Combine(
            AppContext.BaseDirectory,
            "ShippedMod",
            "Asset",
            "Sanity",
            "Data"
        );
        var animations = File.ReadAllText(Path.Combine(data, "animations.json"));
        var bindings = File.ReadAllText(
            Path.Combine(data, "resource-bindings.json")
        );
        Assert.True(
            SanityHostileAttackMetadataCatalog.TryParse(
                animations,
                bindings,
                out var catalog,
                out var reason
            ),
            reason
        );
        return catalog!;
    }
}
