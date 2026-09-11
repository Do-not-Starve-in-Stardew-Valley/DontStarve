using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Collision;

public sealed class HostileShadowCrowdCollisionResolverTests
{
    [Fact]
    public void Moving_rear_participant_pushes_stationary_front_participant()
    {
        var rear = Participant("a-rear", 0d, 2d, 2d);
        var front = Participant("b-front", 8d, 8d, 0d);
        var resolution = Resolve(rear, front);

        Assert.False(resolution.IsResolved, resolution.Reason);
        Assert.Equal(1, resolution.ResidualOverlappingPairCount);
        var rearResult = Entry(resolution, "a-rear");
        var frontResult = Entry(resolution, "b-front");
        Assert.True(rearResult.FinalPosition.X < rearResult.NormalTargetPosition.X);
        Assert.True(frontResult.FinalPosition.X > frontResult.NormalTargetPosition.X);
        Assert.Equal(
            Math.Abs(rearResult.AppliedCorrection.X),
            frontResult.AppliedCorrection.X,
            precision: 10
        );
        Assert.Equal(
            -rearResult.AppliedCorrection.X,
            frontResult.AppliedCorrection.X,
            precision: 10
        );
        Assert.Equal(0d, rearResult.AppliedCorrection.Y, precision: 8);
        Assert.Equal(0d, frontResult.AppliedCorrection.Y, precision: 8);
    }

    [Fact]
    public void Shallow_overlap_uses_a_bounded_reaction_and_repeated_solves_converge()
    {
        var resolver = new HostileShadowCrowdCollisionResolver();
        var currentRear = 0d;
        var currentFront = 9d;
        var first = resolver.Resolve(
            new[]
            {
                Participant("a", currentRear, currentRear, 0d),
                Participant("b", currentFront, currentFront, 0d),
            }
        );

        Assert.False(first.IsResolved);
        Assert.Equal(1, first.ResidualOverlappingPairCount);
        var firstRear = Entry(first, "a");
        var firstFront = Entry(first, "b");
        var firstTotalCorrection =
            Math.Abs(firstRear.AppliedCorrection.X)
            + Math.Abs(firstFront.AppliedCorrection.X);
        Assert.InRange(firstTotalCorrection, 0.000001d, 0.999999d);

        currentRear = firstRear.FinalPosition.X;
        currentFront = firstFront.FinalPosition.X;
        var last = first;
        for (var iteration = 0; iteration < 256 && !last.IsResolved; iteration++)
        {
            last = resolver.Resolve(
                new[]
                {
                    Participant("a", currentRear, currentRear, 0d),
                    Participant("b", currentFront, currentFront, 0d),
                }
            );
            currentRear = Entry(last, "a").FinalPosition.X;
            currentFront = Entry(last, "b").FinalPosition.X;
        }

        Assert.True(last.IsResolved, last.Reason);
        Assert.Equal(0, last.ResidualOverlappingPairCount);
    }

    [Fact]
    public void Deep_overlap_gets_a_larger_but_non_separating_reaction()
    {
        var resolution = Resolve(
            Participant("a", 0d, 0d, 0d),
            Participant("b", 6d, 6d, 0d)
        );

        Assert.False(resolution.IsResolved, resolution.Reason);
        Assert.Equal(1, resolution.ResidualOverlappingPairCount);
        var first = Entry(resolution, "a");
        var second = Entry(resolution, "b");
        var totalCorrection =
            Math.Abs(first.AppliedCorrection.X)
            + Math.Abs(second.AppliedCorrection.X);
        Assert.InRange(totalCorrection, 0.000001d, 3.999999d);
        Assert.True(first.FinalPosition.X + 10d > second.FinalPosition.X);
    }

    [Fact]
    public void Minimum_overlap_axis_takes_priority_over_movement_direction()
    {
        var resolution = Resolve(
            Participant("a", 0d, 0d, 1d, y: 0d),
            Participant("b", 2d, 2d, 0d, y: 7d)
        );

        Assert.False(resolution.IsResolved, resolution.Reason);
        var first = Entry(resolution, "a");
        var second = Entry(resolution, "b");
        Assert.Equal(0d, first.AppliedCorrection.X, precision: 8);
        Assert.Equal(0d, second.AppliedCorrection.X, precision: 8);
        Assert.NotEqual(0d, first.AppliedCorrection.Y);
        Assert.NotEqual(0d, second.AppliedCorrection.Y);
    }

    [Fact]
    public void No_direction_uses_the_minimum_overlap_axis()
    {
        var resolution = Resolve(
            Participant("a", 0d, 0d, 0d),
            Participant("b", 6d, 6d, 0d)
        );

        Assert.False(resolution.IsResolved, resolution.Reason);
        Assert.Equal(1, resolution.ResidualOverlappingPairCount);
        Assert.NotEqual(0d, Entry(resolution, "a").AppliedCorrection.X);
        Assert.Equal(0d, Entry(resolution, "a").AppliedCorrection.Y, precision: 8);
    }

    [Fact]
    public void Fully_coincident_boxes_use_stable_id_order_and_are_input_order_independent()
    {
        var forward = Resolve(
            Participant("a", 0d, 0d, 0d),
            Participant("b", 0d, 0d, 0d)
        );
        var reverse = Resolve(
            Participant("b", 0d, 0d, 0d),
            Participant("a", 0d, 0d, 0d)
        );

        Assert.False(forward.IsResolved, forward.Reason);
        Assert.False(reverse.IsResolved, reverse.Reason);
        Assert.Equal(1, forward.ResidualOverlappingPairCount);
        Assert.Equal(1, reverse.ResidualOverlappingPairCount);
        Assert.Equal(new[] { "a", "b" }, forward.Entries.Select(entry => entry.StableId));
        Assert.Equal(new[] { "a", "b" }, reverse.Entries.Select(entry => entry.StableId));
        Assert.Equal(
            Entry(forward, "a").FinalPosition,
            Entry(reverse, "a").FinalPosition
        );
        Assert.Equal(
            Entry(forward, "b").FinalPosition,
            Entry(reverse, "b").FinalPosition
        );
        Assert.True(Entry(forward, "a").FinalPosition.X < 0d);
        Assert.True(Entry(forward, "b").FinalPosition.X > 0d);
    }

    [Fact]
    public void Three_participant_chain_transfers_push_with_finite_iterations()
    {
        var resolution = Resolve(
            Participant("a", 0d, 2d, 2d),
            Participant("b", 8d, 8d, 0d),
            Participant("c", 14d, 14d, 0d)
        );

        Assert.False(resolution.IsResolved, resolution.Reason);
        Assert.True(resolution.ResidualOverlappingPairCount > 0);
        Assert.InRange(
            resolution.IterationsExecuted,
            1,
            HostileShadowCrowdCollisionResolver.DefaultMaximumIterations
        );
        Assert.True(
            Entry(resolution, "a").FinalPosition.X
                < Entry(resolution, "a").NormalTargetPosition.X
        );
        Assert.True(
            Entry(resolution, "c").FinalPosition.X
                > Entry(resolution, "c").NormalTargetPosition.X
        );
        Assert.True(resolution.ResolvedPairCount >= 2);
    }

    [Theory]
    [InlineData("Farm", "shadow-creature.v1", "Mine", "shadow-creature.v1")]
    [InlineData("Farm", "shadow-creature.v1", "Farm", "other-group.v1")]
    public void Different_location_or_group_isolation_does_not_move_participants(
        string firstLocation,
        string firstGroup,
        string secondLocation,
        string secondGroup
    )
    {
        var resolution = Resolve(
            Participant("a", 0d, 0d, 1d, location: firstLocation, group: firstGroup),
            Participant("b", 0d, 0d, 0d, location: secondLocation, group: secondGroup)
        );

        Assert.True(resolution.IsResolved, resolution.Reason);
        Assert.Equal(0, resolution.ResidualOverlappingPairCount);
        Assert.Equal(0, resolution.CandidatePairCount);
        Assert.Equal(
            Entry(resolution, "a").NormalTargetPosition,
            Entry(resolution, "a").FinalPosition
        );
        Assert.Equal(
            Entry(resolution, "b").NormalTargetPosition,
            Entry(resolution, "b").FinalPosition
        );
    }

    [Fact]
    public void Binding_projection_is_skipped_while_binding_representative_participates_once()
    {
        var resolution = Resolve(
            Participant("unbound", 0d, 0d, 0d),
            Participant(
                "binding-entity",
                6d,
                6d,
                0d,
                isBinding: true,
                isBindingRepresentative: true
            ),
            Participant(
                "binding-projection",
                6d,
                6d,
                0d,
                isBinding: true,
                isBindingRepresentative: false
            )
        );

        Assert.False(resolution.IsResolved, resolution.Reason);
        Assert.True(resolution.ResidualOverlappingPairCount > 0);
        Assert.Equal(3, resolution.InputParticipantCount);
        Assert.Equal(2, resolution.EligibleParticipantCount);
        Assert.Equal(1, resolution.SkippedParticipantCount);
        Assert.Equal(1, resolution.CandidatePairCount);
        Assert.DoesNotContain(
            resolution.Entries,
            entry => entry.StableId == "binding-projection"
        );
    }

    [Fact]
    public void Push_force_scales_symmetric_pair_response_without_changing_input()
    {
        var normal = Participant("rear", 0d, 2d, 2d, pushForce: 1d);
        var weak = Participant("rear", 0d, 2d, 2d, pushForce: 0.5d);
        var strong = Participant("rear", 0d, 2d, 2d, pushForce: 4d);
        var front = Participant("front", 8d, 8d, 0d);
        var normalResolution = Resolve(normal, front);
        var weakResolution = Resolve(weak, front);
        var strongResolution = Resolve(strong, front);

        Assert.False(normalResolution.IsResolved, normalResolution.Reason);
        Assert.False(weakResolution.IsResolved, weakResolution.Reason);
        Assert.False(strongResolution.IsResolved, strongResolution.Reason);
        var normalRearCorrection = Entry(normalResolution, "rear").AppliedCorrection.X;
        var normalFrontCorrection = Entry(normalResolution, "front").AppliedCorrection.X;
        var weakRearCorrection = Entry(weakResolution, "rear").AppliedCorrection.X;
        var weakFrontCorrection = Entry(weakResolution, "front").AppliedCorrection.X;
        var strongRearCorrection = Entry(strongResolution, "rear").AppliedCorrection.X;
        var strongFrontCorrection = Entry(strongResolution, "front").AppliedCorrection.X;
        Assert.Equal(-normalRearCorrection, normalFrontCorrection, precision: 10);
        Assert.Equal(-weakRearCorrection, weakFrontCorrection, precision: 10);
        Assert.Equal(-strongRearCorrection, strongFrontCorrection, precision: 10);
        Assert.Equal(normalFrontCorrection * 0.5d, weakFrontCorrection, precision: 10);
        Assert.True(strongFrontCorrection > normalFrontCorrection);
        Assert.Equal(2d, normal.NormalTargetPosition.X);
        Assert.Equal(
            new HostileShadowPushBoxPoint(2d, 0d),
            normal.MovementVector
        );
        Assert.Equal(1d, normal.PushForce);
        Assert.Equal(4d, strong.PushForce);
    }

    [Fact]
    public void Reaction_magnitude_increases_monotonically_with_overlap_depth()
    {
        var shallow = Resolve(
            Participant("a", 0d, 0d, 0d),
            Participant("b", 9d, 9d, 0d)
        );
        var deep = Resolve(
            Participant("a", 0d, 0d, 0d),
            Participant("b", 6d, 6d, 0d)
        );

        var shallowMagnitude = TotalCorrection(shallow);
        var deepMagnitude = TotalCorrection(deep);
        Assert.True(deepMagnitude > shallowMagnitude);
        Assert.InRange(shallowMagnitude, 0.000001d, 0.999999d);
        Assert.InRange(deepMagnitude, 0.000001d, 3.999999d);
    }

    [Fact]
    public void Existing_overlap_reacts_even_without_closing_movement_and_no_overlap_is_unchanged()
    {
        var overlapping = Resolve(
            Participant("a", 0d, 0d, 0d),
            Participant("b", 9d, 9d, 0d)
        );
        Assert.NotEqual(0d, Entry(overlapping, "a").AppliedCorrection.X);
        Assert.NotEqual(0d, Entry(overlapping, "b").AppliedCorrection.X);

        var separated = Resolve(
            Participant("a", 0d, 0d, 0d),
            Participant("b", 10d, 10d, 0d)
        );
        Assert.Equal(
            Entry(separated, "a").NormalTargetPosition,
            Entry(separated, "a").FinalPosition
        );
        Assert.Equal(
            Entry(separated, "b").NormalTargetPosition,
            Entry(separated, "b").FinalPosition
        );
    }

    [Fact]
    public void Reaction_displacement_uses_a_neutral_global_baseline_without_changing_depth_curve()
    {
        var resolution = Resolve(
            Participant("a", 0d, 0d, 0d),
            Participant("b", 9d, 9d, 0d)
        );

        var expectedWithoutScale = 1d * (0.20d + (0.60d * (0.10d / 1.10d)));
        Assert.Equal(1d, HostileShadowCrowdCollisionResolver.ReactionDisplacementScale);
        Assert.Equal(
            expectedWithoutScale * HostileShadowCrowdCollisionResolver.ReactionDisplacementScale,
            TotalCorrection(resolution),
            precision: 10
        );
    }

    [Fact]
    public void Tiny_positive_overlap_still_scales_by_push_force()
    {
        var baseline = Resolve(
            Participant("a", 0d, 0d, 0d, pushForce: 1d),
            Participant("b", 10d - 1e-8d, 10d - 1e-8d, 0d, pushForce: 1d)
        );
        var weak = Resolve(
            Participant("a", 0d, 0d, 0d, pushForce: 0.25d),
            Participant("b", 10d - 1e-8d, 10d - 1e-8d, 0d, pushForce: 0.25d)
        );

        Assert.True(TotalCorrection(baseline) > 0d);
        Assert.True(TotalCorrection(weak) > 0d);
        Assert.Equal(
            TotalCorrection(baseline) * 0.25d,
            TotalCorrection(weak),
            precision: 14
        );
    }

    [Fact]
    public void Duplicate_stable_ids_are_skipped_instead_of_becoming_order_dependent()
    {
        var resolution = Resolve(
            Participant("duplicate", 0d, 0d, 0d),
            Participant("duplicate", 6d, 6d, 0d),
            Participant("unique", 100d, 100d, 0d)
        );

        Assert.True(resolution.IsResolved, resolution.Reason);
        Assert.Equal(3, resolution.InputParticipantCount);
        Assert.Equal(2, resolution.SkippedParticipantCount);
        Assert.Equal(1, resolution.EligibleParticipantCount);
        Assert.Equal("unique", resolution.Entries.Single().StableId);
    }

    [Fact]
    public void Invalid_participants_fail_closed_without_poisoning_valid_results()
    {
        var resolution = Resolve(
            new HostileShadowCrowdParticipant(
                "invalid",
                "Farm",
                "shadow-creature.v1",
                new HostileShadowPushBoxPoint(double.NaN, 0d),
                new HostileShadowPushBoxPoint(double.NaN, 0d),
                new HostileShadowPushBoxPoint(0d, 0d),
                new HostileShadowPushBoxWorldRectangle(0d, 0d, 10d, 10d),
                1d
            ),
            Participant("valid", 1000d, 1000d, 0d)
        );

        Assert.True(resolution.IsResolved, resolution.Reason);
        Assert.Equal(2, resolution.InputParticipantCount);
        Assert.Equal(1, resolution.EligibleParticipantCount);
        Assert.Equal(1, resolution.SkippedParticipantCount);
        Assert.All(resolution.Entries, entry => Assert.True(entry.IsFinite));
        Assert.Equal("valid", resolution.Entries.Single().StableId);
    }

    [Fact]
    public void Spatial_candidates_are_bounded_and_iterations_are_finite()
    {
        var participants = Enumerable
            .Range(0, 300)
            .Select(
                index => Participant(
                    $"participant-{index:000}",
                    index * 1000d,
                    index * 1000d,
                    0d
                )
            )
            .ToArray();
        var resolution = Resolve(participants);

        Assert.True(resolution.IsResolved, resolution.Reason);
        Assert.Equal(0, resolution.CandidatePairCount);
        Assert.InRange(
            resolution.IterationsExecuted,
            0,
            HostileShadowCrowdCollisionResolver.DefaultMaximumIterations
        );
        Assert.Equal(participants.Length, resolution.Entries.Count);
    }

    [Fact]
    public void Resolver_source_has_no_entity_or_world_collision_dependency()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "HostileShadowAuthority",
            "HostileShadowCrowdCollisionResolver.cs"
        );
        var source = File.ReadAllText(path);
        Assert.DoesNotContain("Farmer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GameLocation", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1", source, StringComparison.Ordinal);
        Assert.DoesNotContain("isCollidingPosition", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HostileAttack", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HurtBox", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolver_source_has_no_tolerance_band_or_strict_settlement_path()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "HostileShadowAuthority",
            "HostileShadowCrowdCollisionResolver.cs"
        );
        var source = File.ReadAllText(path);
        Assert.Contains(
            "CalculateReactionDisplacement",
            source,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("SoftOverlapFraction", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TryCreateStrictSettlementAdjustment",
            source,
            StringComparison.Ordinal
        );
    }

    private static HostileShadowCrowdCollisionResolution Resolve(
        params HostileShadowCrowdParticipant[] participants
    )
    {
        return new HostileShadowCrowdCollisionResolver().Resolve(participants);
    }

    private static HostileShadowCrowdParticipant Participant(
        string id,
        double x,
        double targetX,
        double movementX,
        double pushForce = 1d,
        string location = "Farm",
        string group = "shadow-creature.v1",
        double y = 0d,
        bool isActive = true,
        bool isBinding = false,
        bool isBindingRepresentative = false
    )
    {
        return new HostileShadowCrowdParticipant(
            id,
            location,
            group,
            new HostileShadowPushBoxPoint(x, y),
            new HostileShadowPushBoxPoint(targetX, y),
            new HostileShadowPushBoxPoint(movementX, 0d),
            new HostileShadowPushBoxWorldRectangle(x, y, 10d, 10d),
            pushForce,
            isActive,
            isBinding,
            isBindingRepresentative
        );
    }

    private static HostileShadowCrowdCollisionResolutionEntry Entry(
        HostileShadowCrowdCollisionResolution resolution,
        string stableId
    )
    {
        return resolution.Entries.Single(entry => entry.StableId == stableId);
    }

    private static double TotalCorrection(
        HostileShadowCrowdCollisionResolution resolution
    )
    {
        return resolution.Entries.Sum(
            entry => Math.Abs(entry.AppliedCorrection.X)
                + Math.Abs(entry.AppliedCorrection.Y)
        );
    }
}
