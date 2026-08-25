using DontStarve.Player.Stats.Sanity.Audio;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class ShadowCreatureSfxTests
{
    [Fact]
    public void Each_owner_has_one_instance_but_five_owners_can_play()
    {
        var random = new SequenceShadowCreatureSfxRandom(0);
        var lanes = Enumerable.Range(1, 5)
            .Select(id => new ShadowCreatureSfxOwnerLane(
                new ShadowCreatureSfxOwnerKey("session", id.ToString()),
                ShadowCreatureSpecies.CreeperFear,
                Pools(ShadowCreatureSfxCue.Idle, 2),
                random
            ))
            .ToArray();

        foreach (var lane in lanes)
            Assert.Equal(ShadowCreatureSfxRequestStatus.Started, lane.Request(
                ShadowCreatureSfxCue.Idle,
                "idle-1",
                ShadowCreatureSfxSpatial.AtOrigin(1f)
            ).Status);

        Assert.All(lanes, lane => Assert.Equal(1, lane.PhysicalInstanceCount));
    }

    [Fact]
    public void Same_or_lower_priority_is_dropped_and_higher_priority_preempts()
    {
        var random = new SequenceShadowCreatureSfxRandom(0);
        var lane = new ShadowCreatureSfxOwnerLane(
            new ShadowCreatureSfxOwnerKey("session", "1"),
            ShadowCreatureSpecies.CreeperFear,
            Pools(
                ShadowCreatureSfxCue.Idle, 1,
                ShadowCreatureSfxCue.Chase, 1,
                ShadowCreatureSfxCue.Taunt, 1
            ),
            random
        );

        Assert.Equal(ShadowCreatureSfxRequestStatus.Started, lane.Request(
            ShadowCreatureSfxCue.Chase, "chase-1", ShadowCreatureSfxSpatial.AtOrigin(1f)
        ).Status);
        Assert.Equal(ShadowCreatureSfxRequestStatus.DroppedPriority, lane.Request(
            ShadowCreatureSfxCue.Chase, "chase-2", ShadowCreatureSfxSpatial.AtOrigin(1f)
        ).Status);
        Assert.Equal(ShadowCreatureSfxRequestStatus.DroppedPriority, lane.Request(
            ShadowCreatureSfxCue.Idle, "idle-1", ShadowCreatureSfxSpatial.AtOrigin(1f)
        ).Status);
        Assert.Equal(ShadowCreatureSfxRequestStatus.Started, lane.Request(
            ShadowCreatureSfxCue.Taunt, "taunt-1", ShadowCreatureSfxSpatial.AtOrigin(1f)
        ).Status);
        Assert.Equal(ShadowCreatureSfxCue.Taunt, lane.CurrentCue);
        Assert.Equal(1, lane.PhysicalInstanceCount);
        Assert.Equal(1, ((FakeInstance)lane.CreatedInstances[0]).StopCount);
    }

    [Fact]
    public void Attack_instance_id_is_consumed_once_even_after_the_voice_ends()
    {
        var lane = new ShadowCreatureSfxOwnerLane(
            new ShadowCreatureSfxOwnerKey("session", "1"),
            ShadowCreatureSpecies.Terrorbeak,
            Pools(ShadowCreatureSfxCue.Attack, 1),
            new SequenceShadowCreatureSfxRandom(0)
        );

        var first = lane.Request(
            ShadowCreatureSfxCue.Attack,
            "attack-instance-42",
            ShadowCreatureSfxSpatial.AtOrigin(1f)
        );
        lane.Stop();
        var duplicate = lane.Request(
            ShadowCreatureSfxCue.Attack,
            "attack-instance-42",
            ShadowCreatureSfxSpatial.AtOrigin(1f)
        );

        Assert.Equal(ShadowCreatureSfxRequestStatus.Started, first.Status);
        Assert.Equal(ShadowCreatureSfxRequestStatus.DroppedDuplicate, duplicate.Status);
    }

    [Theory]
    [InlineData(0.5, 3)]
    [InlineData(0.499999, 4)]
    public void Creeper_attack_variant_uses_the_frozen_half_health_boundary(
        double healthRatio,
        int expectedRaw
    )
    {
        Assert.Equal(
            (ShadowCreatureSfxCue)expectedRaw,
            ShadowCreatureSfxPolicy.SelectAttackCue(
                ShadowCreatureSpecies.CreeperFear,
                healthRatio
            )
        );
    }

    [Fact]
    public void Terrorbeak_attack_never_selects_creeper_sharp_or_dull()
    {
        Assert.Equal(
            ShadowCreatureSfxCue.Attack,
            ShadowCreatureSfxPolicy.SelectAttackCue(
                ShadowCreatureSpecies.Terrorbeak,
                0.01d
            )
        );
        Assert.False(ShadowCreatureSfxPolicy.IsCueAllowedForHarmlessProjection(
            ShadowCreatureSfxCue.AttackSharp
        ));
    }

    [Fact]
    public void Cadence_uses_species_state_ranges_and_does_not_advance_until_started()
    {
        var random = new SequenceShadowCreatureSfxRandom(0d, 1d);
        var cadence = new ShadowCreatureSfxCadence(ShadowCreatureSpecies.CreeperFear, random);

        cadence.Enter(ShadowCreatureSfxCadenceState.Chase, 100d, 7);
        Assert.Equal(100.25d, cadence.NextDueAtSeconds, 6);
        Assert.True(cadence.IsDue(100.25d));
        cadence.DeferBecauseVoiceBusy();
        Assert.Equal(100.25d, cadence.NextDueAtSeconds, 6);
        cadence.MarkStarted(100.25d);
        Assert.Equal(107.25d, cadence.NextDueAtSeconds, 6);

        cadence.Enter(ShadowCreatureSfxCadenceState.Idle, 200d, 8);
        Assert.Equal(203.2d, cadence.NextDueAtSeconds, 6);
    }

    [Theory]
    [InlineData(0d, 1f, 0f, true)]
    [InlineData(15d, 0.5f, 1f, true)]
    [InlineData(30d, 0f, 1f, true)]
    [InlineData(30.01d, 0f, 0f, false)]
    public void Spatial_mix_has_linear_thirty_tile_falloff_and_pan(
        double dx,
        float expectedVolumeFactor,
        float expectedPan,
        bool audible
    )
    {
        var result = ShadowCreatureSfxSpatial.FromDelta(dx, 0d, 0.8f);

        Assert.Equal(audible, result.IsAudible);
        Assert.Equal(expectedVolumeFactor * 0.8f, result.Volume, 5);
        Assert.Equal(expectedPan, result.Pan, 5);
    }

    [Fact]
    public void Tick_updates_position_and_stops_when_owner_leaves_audible_radius()
    {
        var lane = new ShadowCreatureSfxOwnerLane(
            new ShadowCreatureSfxOwnerKey("session", "1"),
            ShadowCreatureSpecies.CreeperFear,
            Pools(ShadowCreatureSfxCue.Idle, 1),
            new SequenceShadowCreatureSfxRandom(0)
        );
        lane.Request(ShadowCreatureSfxCue.Idle, "idle-1", ShadowCreatureSfxSpatial.AtOrigin(1f));

        lane.Tick(ShadowCreatureSfxSpatial.FromDelta(10d, 0d, 0.6f), 0.6f);
        Assert.Equal(0.4f, lane.CreatedInstances[0].Volume, 5);
        Assert.Equal(1f, lane.CreatedInstances[0].Pan, 5);
        lane.Tick(ShadowCreatureSfxSpatial.FromDelta(31d, 0d, 0.6f), 0.6f);

        Assert.Equal(0, lane.PhysicalInstanceCount);
        Assert.Equal(1, ((FakeInstance)lane.CreatedInstances[0]).StopCount);
    }

    [Fact]
    public void Tick_recomputes_volume_from_the_current_global_sound_volume()
    {
        var lane = new ShadowCreatureSfxOwnerLane(
            new ShadowCreatureSfxOwnerKey("session", "volume"),
            ShadowCreatureSpecies.CreeperFear,
            Pools(ShadowCreatureSfxCue.Idle, 1),
            new SequenceShadowCreatureSfxRandom(0)
        );
        lane.Request(
            ShadowCreatureSfxCue.Idle,
            "idle-1",
            ShadowCreatureSfxSpatial.AtOrigin(1f)
        );

        lane.Tick(ShadowCreatureSfxSpatial.FromDelta(10d, 0d, 1f), 0.5f);

        Assert.Equal((1f - (10f / 30f)) * 0.5f, lane.CreatedInstances[0].Volume, 5);
    }

    [Fact]
    public void Created_instance_history_is_bounded_without_losing_the_creation_count()
    {
        var lane = new ShadowCreatureSfxOwnerLane(
            new ShadowCreatureSfxOwnerKey("session", "history"),
            ShadowCreatureSpecies.CreeperFear,
            Pools(ShadowCreatureSfxCue.Idle, 1),
            new SequenceShadowCreatureSfxRandom(0)
        );

        for (var index = 0; index < 80; index++)
        {
            lane.Request(
                ShadowCreatureSfxCue.Idle,
                $"idle-{index}",
                ShadowCreatureSfxSpatial.AtOrigin(1f)
            );
            lane.Stop();
        }

        Assert.Equal(80, lane.TotalCreatedInstanceCount);
        Assert.InRange(lane.CreatedInstances.Count, 1, 64);
    }

    [Fact]
    public void Zero_volume_does_not_create_a_pending_or_physical_voice()
    {
        var lane = new ShadowCreatureSfxOwnerLane(
            new ShadowCreatureSfxOwnerKey("session", "1"),
            ShadowCreatureSpecies.CreeperFear,
            Pools(ShadowCreatureSfxCue.Idle, 1),
            new SequenceShadowCreatureSfxRandom(0)
        );

        var result = lane.Request(
            ShadowCreatureSfxCue.Idle,
            "idle-1",
            ShadowCreatureSfxSpatial.AtOrigin(0f)
        );

        Assert.Equal(ShadowCreatureSfxRequestStatus.SkippedSilent, result.Status);
        Assert.Equal(0, lane.PhysicalInstanceCount);
        Assert.Empty(lane.CreatedInstances);
    }

    [Fact]
    public void Coordinator_retries_a_due_cadence_after_it_was_silent()
    {
        var coordinator = new ShadowCreatureSfxCoordinator(
            species => AllPools(species),
            new SequenceShadowCreatureSfxRandom(0d)
        );
        var owner = new ShadowCreatureSfxOwnerKey("session", "silent-cadence");

        coordinator.ObserveHarmless(new ShadowCreatureSfxHarmlessObservation(
            owner,
            ShadowCreatureSpecies.Terrorbeak,
            ShadowCreatureSfxProjectionState.Idle,
            ShadowCreatureSfxSpatial.AtOrigin(0f),
            0d,
            1
        ));
        coordinator.Tick(2d, 0f);

        coordinator.UpdateSpatial(owner, ShadowCreatureSfxSpatial.AtOrigin(1f));
        coordinator.Tick(2d, 1f);

        var lane = Assert.IsType<ShadowCreatureSfxOwnerLane>(coordinator.GetLane(owner));
        Assert.Single(lane.CreatedInstances);
        Assert.Equal(ShadowCreatureSfxCue.Idle, lane.CurrentCue);
    }

    [Fact]
    public void Coordinator_triggers_attack_once_and_distinguishes_nonlethal_hurt_from_death()
    {
        var coordinator = new ShadowCreatureSfxCoordinator(
            species => AllPools(species),
            new SequenceShadowCreatureSfxRandom(0)
        );
        var owner = new ShadowCreatureSfxOwnerKey("session", "attack-1");
        var spatial = ShadowCreatureSfxSpatial.AtOrigin(1f);

        coordinator.ObserveHostile(new ShadowCreatureSfxHostileObservation(
            owner,
            ShadowCreatureSpecies.CreeperFear,
            ShadowCreatureSfxObservedState.Attack,
            spatial,
            0d,
            0.49d,
            "attack-42",
            10
        ));
        coordinator.ObserveHostile(new ShadowCreatureSfxHostileObservation(
            owner,
            ShadowCreatureSpecies.CreeperFear,
            ShadowCreatureSfxObservedState.Attack,
            spatial,
            0.1d,
            0.49d,
            "attack-42",
            10
        ));
        coordinator.NotifyHostileHit(owner, ShadowCreatureSpecies.CreeperFear, spatial, 11, false);
        coordinator.NotifyHostileHit(owner, ShadowCreatureSpecies.CreeperFear, spatial, 12, true);
        coordinator.NotifyConfirmedDeath(owner, ShadowCreatureSpecies.CreeperFear, spatial, 13);
        coordinator.NotifyConfirmedDeath(owner, ShadowCreatureSpecies.CreeperFear, spatial, 13);

        var lane = Assert.IsType<ShadowCreatureSfxOwnerLane>(coordinator.GetLane(owner));
        Assert.Equal(3, lane.CreatedInstances.Count);
        Assert.Equal(ShadowCreatureSfxCue.Death, lane.CurrentCue);
        Assert.Contains(
            lane.CreatedInstances,
            instance => ((FakeInstance)instance).PlayCount == 1
        );
    }

    [Fact]
    public void Coordinator_ignores_observations_from_an_older_revision()
    {
        var coordinator = new ShadowCreatureSfxCoordinator(
            species => AllPools(species),
            new SequenceShadowCreatureSfxRandom(0d)
        );
        var owner = new ShadowCreatureSfxOwnerKey("session", "revision");

        coordinator.ObserveHostile(new ShadowCreatureSfxHostileObservation(
            owner,
            ShadowCreatureSpecies.CreeperFear,
            ShadowCreatureSfxObservedState.Chase,
            ShadowCreatureSfxSpatial.AtOrigin(1f),
            0d,
            1d,
            string.Empty,
            5
        ));
        coordinator.ObserveHostile(new ShadowCreatureSfxHostileObservation(
            owner,
            ShadowCreatureSpecies.CreeperFear,
            ShadowCreatureSfxObservedState.Idle,
            ShadowCreatureSfxSpatial.AtOrigin(1f),
            100d,
            1d,
            string.Empty,
            4
        ));

        coordinator.Tick(1d, 1f);

        var lane = Assert.IsType<ShadowCreatureSfxOwnerLane>(coordinator.GetLane(owner));
        Assert.Equal(ShadowCreatureSfxCue.Chase, lane.CurrentCue);
    }

    [Fact]
    public void Coordinator_reports_pool_provider_failures()
    {
        var diagnostics = new RecordingShadowCreatureSfxDiagnostics();
        var coordinator = new ShadowCreatureSfxCoordinator(
            _ => throw new InvalidOperationException("pool-broken"),
            new SequenceShadowCreatureSfxRandom(0d),
            diagnostics
        );

        coordinator.ObserveHostile(new ShadowCreatureSfxHostileObservation(
            new ShadowCreatureSfxOwnerKey("session", "diagnostics"),
            ShadowCreatureSpecies.Terrorbeak,
            ShadowCreatureSfxObservedState.Attack,
            ShadowCreatureSfxSpatial.AtOrigin(1f),
            0d,
            1d,
            "attack-1",
            1
        ));

        Assert.Contains(diagnostics.Messages, message =>
            message.Contains("pool-provider", StringComparison.Ordinal));
    }

    [Fact]
    public void Coordinator_maps_harmless_states_and_cleanup_stops_the_owner()
    {
        var coordinator = new ShadowCreatureSfxCoordinator(
            species => AllPools(species),
            new SequenceShadowCreatureSfxRandom(0d, 0d, 0d, 0d)
        );
        var owner = new ShadowCreatureSfxOwnerKey("session", "projection-1");
        var spatial = ShadowCreatureSfxSpatial.AtOrigin(1f);

        coordinator.ObserveHarmless(new ShadowCreatureSfxHarmlessObservation(
            owner,
            ShadowCreatureSpecies.Terrorbeak,
            ShadowCreatureSfxProjectionState.Spawning,
            spatial,
            0d,
            1
        ));
        coordinator.ObserveHarmless(new ShadowCreatureSfxHarmlessObservation(
            owner,
            ShadowCreatureSpecies.Terrorbeak,
            ShadowCreatureSfxProjectionState.Idle,
            spatial,
            0d,
            2
        ));
        coordinator.Tick(2d, 1f);
        coordinator.ObserveHarmless(new ShadowCreatureSfxHarmlessObservation(
            owner,
            ShadowCreatureSpecies.Terrorbeak,
            ShadowCreatureSfxProjectionState.Fleeing,
            spatial,
            4d,
            3
        ));
        coordinator.Tick(4.25d, 1f);

        var lane = Assert.IsType<ShadowCreatureSfxOwnerLane>(coordinator.GetLane(owner));
        Assert.Contains(lane.CreatedInstances, instance => ((FakeInstance)instance).PlayCount > 0);
        Assert.All(lane.CreatedInstances, instance => Assert.True(((FakeInstance)instance).IsLooped == false));

        coordinator.RemoveOwner(owner);
        Assert.Null(coordinator.GetLane(owner));
        Assert.All(lane.CreatedInstances, instance => Assert.True(((FakeInstance)instance).StopCount > 0));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    public void Harmless_projection_only_allows_idle_and_chase(
        int cueRaw,
        bool expected
    )
    {
        Assert.Equal(expected, ShadowCreatureSfxPolicy.IsCueAllowedForHarmlessProjection((ShadowCreatureSfxCue)cueRaw));
    }

    private static IReadOnlyDictionary<ShadowCreatureSfxCue, IReadOnlyList<IShadowCreatureSfxEffect>> Pools(
        params object[] values
    )
    {
        var result = new Dictionary<ShadowCreatureSfxCue, IReadOnlyList<IShadowCreatureSfxEffect>>();
        for (var i = 0; i < values.Length; i += 2)
        {
            var cue = (ShadowCreatureSfxCue)values[i];
            var count = (int)values[i + 1];
            result[cue] = Enumerable.Range(0, count)
                .Select(index => (IShadowCreatureSfxEffect)new FakeEffect($"{cue}-{index}"))
                .ToArray();
        }
        return result;
    }

    private static IReadOnlyDictionary<ShadowCreatureSfxCue, IReadOnlyList<IShadowCreatureSfxEffect>> AllPools(
        ShadowCreatureSpecies species
    )
    {
        var result = new Dictionary<ShadowCreatureSfxCue, IReadOnlyList<IShadowCreatureSfxEffect>>();
        foreach (var cue in Enum.GetValues<ShadowCreatureSfxCue>())
        {
            if (!ShadowCreatureSfxPolicy.IsCueAvailableForSpecies(species, cue))
                continue;
            result[cue] = new[] { (IShadowCreatureSfxEffect)new FakeEffect(cue.ToString()) };
        }
        return result;
    }

    private sealed class SequenceShadowCreatureSfxRandom : IShadowCreatureSfxRandom
    {
        private readonly double[] values;
        private int index;

        internal SequenceShadowCreatureSfxRandom(params double[] values)
        {
            this.values = values.Length == 0 ? new[] { 0d } : values;
        }

        public int NextIndex(int exclusiveUpperBound)
        {
            var value = NextDouble(0d, 1d);
            return Math.Min(exclusiveUpperBound - 1, (int)(value * exclusiveUpperBound));
        }

        public double NextDouble(double inclusiveMinimum, double exclusiveMaximum)
        {
            var value = values[Math.Min(index++, values.Length - 1)];
            return inclusiveMinimum + (exclusiveMaximum - inclusiveMinimum) * value;
        }
    }

    private sealed class FakeEffect : IShadowCreatureSfxEffect
    {
        internal FakeEffect(string id) => ResourceId = id;
        public string ResourceId { get; }
        public FakeInstance Instance { get; } = new();
        public IShadowCreatureSfxInstance CreateInstance() => Instance;
    }

    private sealed class RecordingShadowCreatureSfxDiagnostics : IShadowCreatureSfxDiagnostics
    {
        internal List<string> Messages { get; } = new();

        public void Report(string code, string reason)
        {
            Messages.Add(string.Concat(code, ":", reason));
        }
    }

    private sealed class FakeInstance : IShadowCreatureSfxInstance
    {
        public ShadowCreatureSfxPlaybackState State { get; set; } = ShadowCreatureSfxPlaybackState.Stopped;
        public float Volume { get; set; }
        public float Pan { get; set; }
        public int StopCount { get; private set; }
        public int PlayCount { get; private set; }
        public bool IsLooped { get; set; }
        public void Play() { PlayCount++; State = ShadowCreatureSfxPlaybackState.Playing; }
        public void Stop() { StopCount++; State = ShadowCreatureSfxPlaybackState.Stopped; }
        public void Dispose() { }
    }
}
