using System.Text.Json.Nodes;
using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using DontStarve.Resource.Sanity;
using DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Combat;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Collision;

public sealed class HostileShadowPushBoxContractTests
{
    private static string DataRoot =>
        Path.Combine(AppContext.BaseDirectory, "ShippedMod", "Asset", "Sanity", "Data");

    [Theory]
    [InlineData(
        "sanity.binding.creeper-fear",
        "sanity.animation.creeper-fear.profile",
        4,
        48,
        56,
        48,
        0.25
    )]
    [InlineData(
        "sanity.binding.terrorbeak",
        "sanity.animation.terrorbeak.profile",
        8,
        32,
        32,
        32,
        0.5
    )]
    public void Shipped_profiles_expose_an_independent_push_box_contract(
        string bindingId,
        string profileId,
        int x,
        int y,
        int width,
        int height,
        double pushForce
    )
    {
        var catalog = ParseShippedAttackMetadata();
        Assert.True(catalog.TryGet(bindingId, out var metadata));
        Assert.NotNull(metadata);

        var pushBox = metadata!.Collision.PushBox;
        Assert.Equal(
            SanityHostilePushBoxDefinition.CoordinateSpaceId,
            pushBox.CoordinateSpace
        );
        Assert.Equal(new SanityResourceRectangle(x, y, width, height), pushBox.SourcePx);
        Assert.Equal("shadow-creature.v1", pushBox.GroupId);
        Assert.Equal(pushForce, pushBox.PushForce);
        Assert.Equal(metadata.Collision.HurtBoxSourcePx, pushBox.SourcePx);

        using var document =
            System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(Path.Combine(DataRoot, "animations.json"))
            );
        var profile = document.RootElement
            .GetProperty("AnimationProfiles")
            .EnumerateArray()
            .Single(value => value.GetProperty("AnimationProfileId").GetString() == profileId);
        Assert.Equal(
            "shadow-creature.v1",
            profile.GetProperty("Collision").GetProperty("PushBox").GetProperty("GroupId").GetString()
        );
    }

    [Fact]
    public void Visual_metadata_preview_exposes_push_box_without_changing_hurt_or_attack_boxes()
    {
        var json = File.ReadAllText(Path.Combine(DataRoot, "animations.json"));
        Assert.True(
            SanityVisualMetadataCatalog.TryParse(json, out var catalog, out var reason),
            reason
        );
        Assert.True(
            catalog!.TryCreatePreview(
                "sanity.animation.creeper-fear.attack",
                2,
                out var preview,
                out reason
            ),
            reason
        );
        Assert.NotNull(preview);
        Assert.Equal(new SanityResourceRectangle(4, 48, 56, 48), preview!.PushBoxSourcePx);
        Assert.Equal("shadow-creature.v1", preview.PushBoxGroupId);
        Assert.Equal(0.25d, preview.PushForce);
        Assert.Equal(new SanityResourceRectangle(4, 48, 56, 48), preview.HurtBoxSourcePx);
        Assert.Equal(new SanityResourceRectangle(0, 64, 64, 64), preview.AttackBoxSourcePx);
    }

    [Fact]
    public void Strict_metadata_parser_requires_push_box_instead_of_falling_back_to_hurt_box()
    {
        var animations = ReadAnimations();
        FindProfile(animations, "sanity.animation.creeper-fear.profile")
            ["Collision"]!
            .AsObject()
            .Remove("PushBox");

        var parsed = SanityHostileAttackMetadataCatalog.TryParse(
            animations.ToJsonString(),
            ReadBindings(),
            out _,
            out var reason
        );

        Assert.False(parsed);
        Assert.Equal("resource.hostile-attack-metadata.missing-field", reason);
    }

    [Fact]
    public void Visual_contract_validator_rejects_invalid_push_force_and_group_id()
    {
        var animations = ReadAnimations();
        var pushBox = FindProfile(animations, "sanity.animation.creeper-fear.profile")
            ["Collision"]!
            .AsObject()["PushBox"]!
            .AsObject();

        pushBox["PushForce"] = 0d;
        var forceResult = SanityHostileVisualContractValidator.Validate(
            animations.ToJsonString(),
            ReadBindings()
        );
        Assert.Contains(
            forceResult.Issues,
            issue => issue.Code == "hostile.push-box.push-force-invalid"
        );

        pushBox["PushForce"] = 1d;
        pushBox["GroupId"] = new string('a', SanityHostilePushBoxDefinition.MaximumGroupIdLength + 1);
        var groupResult = SanityHostileVisualContractValidator.Validate(
            animations.ToJsonString(),
            ReadBindings()
        );
        Assert.Contains(
            groupResult.Issues,
            issue => issue.Code == "hostile.push-box.group-id-invalid"
        );
    }

    [Fact]
    public void Push_box_contract_rejects_non_positive_geometry_non_stable_group_and_non_finite_force()
    {
        Assert.False(
            SanityHostilePushBoxDefinition.TryCreate(
                SanityHostilePushBoxDefinition.CoordinateSpaceId,
                new SanityResourceRectangle(0, 0, 0, 10),
                "shadow-creature.v1",
                1d,
                out _,
                out var rectangleReason
            )
        );
        Assert.Equal("resource.hostile-push-box.source-rectangle-invalid", rectangleReason);

        Assert.False(
            SanityHostilePushBoxDefinition.TryCreate(
                SanityHostilePushBoxDefinition.CoordinateSpaceId,
                new SanityResourceRectangle(0, 0, 10, 10),
                "Shadow Creature",
                1d,
                out _,
                out var groupReason
            )
        );
        Assert.Equal("resource.hostile-push-box.group-id-invalid", groupReason);

        Assert.False(
            SanityHostilePushBoxDefinition.TryCreate(
                SanityHostilePushBoxDefinition.CoordinateSpaceId,
                new SanityResourceRectangle(0, 0, 10, 10),
                "shadow-creature.v1",
                double.PositiveInfinity,
                out _,
                out var forceReason
            )
        );
        Assert.Equal("resource.hostile-push-box.push-force-invalid", forceReason);
    }

    [Fact]
    public void Runtime_definition_keeps_push_box_data_independent_from_hurt_box_data()
    {
        var animations = ReadAnimations();
        var pushBox = FindProfile(animations, "sanity.animation.creeper-fear.profile")
            ["Collision"]!
            .AsObject()["PushBox"]!
            .AsObject();
        pushBox["SourcePx"]!["X"] = 7;
        pushBox["SourcePx"]!["Y"] = 50;
        pushBox["SourcePx"]!["Width"] = 20;
        pushBox["SourcePx"]!["Height"] = 10;

        Assert.True(
            SanityHostileAttackMetadataCatalog.TryParse(
                animations.ToJsonString(),
                ReadBindings(),
                out var catalog,
                out var reason
            ),
            reason
        );
        Assert.True(
            catalog!.TryGet("sanity.binding.creeper-fear", out var metadata)
        );
        var profile = HostileAttackTestFactory.Profile(
            "sanity.binding.creeper-fear",
            metadata!
        );
        Assert.True(
            HostileAttackRuntimeDefinition.TryCreate(
                metadata,
                profile,
                out var definition,
                out reason
            ),
            reason
        );

        Assert.Equal(new SanityResourceRectangle(7, 50, 20, 10), definition!.PushBoxSourcePx);
        Assert.Equal(new HostileAttackRectangle(4d, 48d, 56d, 48d), definition.HurtBoxSourcePx);
        Assert.True(
            HostileShadowPushBoxGeometry.TryCreateWorldBox(
                definition,
                100d,
                200d,
                out var worldBox
            )
        );
        Assert.Equal(128d, worldBox.X, precision: 8);
        Assert.Equal(400d, worldBox.Y, precision: 8);
        Assert.Equal(80d, worldBox.Width, precision: 8);
        Assert.Equal(40d, worldBox.Height, precision: 8);
    }

    [Fact]
    public void World_push_box_geometry_uses_actor_origin_pivot_and_scale()
    {
        Assert.True(
            HostileShadowPushBoxGeometry.TryCreateWorldBox(
                new HostileAttackPoint(10d, 20d),
                new HostileAttackPoint(4d, 5d),
                new SanityResourceRectangle(2, 3, 6, 7),
                2d,
                100d,
                200d,
                out var worldBox
            )
        );
        Assert.Equal(116d, worldBox.X, precision: 8);
        Assert.Equal(236d, worldBox.Y, precision: 8);
        Assert.Equal(12d, worldBox.Width, precision: 8);
        Assert.Equal(14d, worldBox.Height, precision: 8);
    }

    [Fact]
    public void World_push_box_geometry_fails_closed_for_invalid_or_overflowing_inputs()
    {
        Assert.False(
            HostileShadowPushBoxGeometry.TryCreateWorldBox(
                new HostileAttackPoint(double.NaN, 20d),
                new HostileAttackPoint(4d, 5d),
                new SanityResourceRectangle(2, 3, 6, 7),
                2d,
                100d,
                200d,
                out var invalidPointBox
            )
        );
        Assert.False(invalidPointBox.IsValid);

        Assert.False(
            HostileShadowPushBoxGeometry.TryCreateWorldBox(
                new HostileAttackPoint(10d, 20d),
                new HostileAttackPoint(4d, 5d),
                new SanityResourceRectangle(2, 3, 0, 7),
                2d,
                100d,
                200d,
                out var invalidRectangleBox
            )
        );
        Assert.False(invalidRectangleBox.IsValid);

        Assert.False(
            HostileShadowPushBoxGeometry.TryCreateWorldBox(
                new HostileAttackPoint(10d, 20d),
                new HostileAttackPoint(4d, 5d),
                new SanityResourceRectangle(2, 3, 6, 7),
                double.MaxValue,
                100d,
                200d,
                out var overflowBox
            )
        );
        Assert.False(overflowBox.IsValid);
    }

    [Fact]
    public void World_push_box_rectangles_report_strict_intersection_depth()
    {
        var left = new HostileShadowPushBoxWorldRectangle(0d, 0d, 10d, 10d);
        var right = new HostileShadowPushBoxWorldRectangle(9d, 2d, 3d, 4d);
        Assert.True(left.Intersects(right));
        Assert.True(left.TryGetIntersectionDepth(right, out var overlapX, out var overlapY));
        Assert.Equal(1d, overlapX, precision: 8);
        Assert.Equal(4d, overlapY, precision: 8);
        Assert.False(
            left.Intersects(new HostileShadowPushBoxWorldRectangle(10d, 0d, 2d, 2d))
        );
        Assert.False(default(HostileShadowPushBoxWorldRectangle).IsValid);
    }

    [Fact]
    public void Push_box_geometry_source_has_no_player_or_map_collision_dependency()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "HostileShadowAuthority",
            "HostileShadowPushBoxGeometry.cs"
        );
        var source = File.ReadAllText(path);
        Assert.DoesNotContain("Farmer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GameLocation", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1", source, StringComparison.Ordinal);
        Assert.DoesNotContain("isCollidingPosition", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryCreateWorldHurtBox", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HostileAttackCollisionResolver", source, StringComparison.Ordinal);
    }

    private static SanityHostileAttackMetadataCatalog ParseShippedAttackMetadata()
    {
        Assert.True(
            SanityHostileAttackMetadataCatalog.TryParse(
                File.ReadAllText(Path.Combine(DataRoot, "animations.json")),
                ReadBindings(),
                out var catalog,
                out var reason
            ),
            reason
        );
        return catalog!;
    }

    private static JsonObject ReadAnimations()
    {
        return JsonNode.Parse(
            File.ReadAllText(Path.Combine(DataRoot, "animations.json"))
        )!.AsObject();
    }

    private static string ReadBindings()
    {
        return File.ReadAllText(Path.Combine(DataRoot, "resource-bindings.json"));
    }

    private static JsonObject FindProfile(JsonObject root, string profileId)
    {
        return root["AnimationProfiles"]!
            .AsArray()
            .Select(node => node!.AsObject())
            .Single(profile =>
                profile["AnimationProfileId"]!.GetValue<string>() == profileId
            );
    }
}
