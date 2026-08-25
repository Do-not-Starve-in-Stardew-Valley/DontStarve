#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity.Audio;

/// <summary>Gameplay state exposed to the audio coordinator without coupling it to SMAPI types.</summary>
internal enum ShadowCreatureSfxObservedState
{
    Spawning,
    Idle,
    Chase,
    Taunt,
    Attack,
    HitTeleport,
    Dying,
    Despawn,
}

/// <summary>Harmless projection states which are allowed to acquire a voice.</summary>
internal enum ShadowCreatureSfxProjectionState
{
    Spawning,
    Idle,
    Wander,
    Fleeing,
    FadingOut,
}

internal readonly record struct ShadowCreatureSfxHostileObservation(
    ShadowCreatureSfxOwnerKey Owner,
    ShadowCreatureSpecies Species,
    ShadowCreatureSfxObservedState State,
    ShadowCreatureSfxSpatial Spatial,
    double NowSeconds,
    double HealthRatio,
    string AttackInstanceId,
    long StateRevision
);

internal readonly record struct ShadowCreatureSfxHarmlessObservation(
    ShadowCreatureSfxOwnerKey Owner,
    ShadowCreatureSpecies Species,
    ShadowCreatureSfxProjectionState State,
    ShadowCreatureSfxSpatial Spatial,
    double NowSeconds,
    long StateRevision
);

/// <summary>
/// Pure event-to-voice coordinator. Runtime adapters feed it state facts and spatial samples;
/// this class owns no SMAPI or XNA objects beyond the effect instances supplied by its pool
/// provider. A due cadence is deliberately retained while a lane is busy, so it never becomes a
/// delayed queue.
/// </summary>
internal sealed class ShadowCreatureSfxCoordinator : IDisposable
{
    private sealed class OwnerState
    {
        internal OwnerState(
            ShadowCreatureSfxOwnerKey owner,
            ShadowCreatureSpecies species,
            ShadowCreatureSfxOwnerLane lane,
            IShadowCreatureSfxRandom random
        )
        {
            Owner = owner;
            Species = species;
            Lane = lane;
            Cadence = new ShadowCreatureSfxCadence(species, random);
        }

        internal ShadowCreatureSfxOwnerKey Owner { get; }
        internal ShadowCreatureSpecies Species { get; }
        internal ShadowCreatureSfxOwnerLane Lane { get; }
        internal ShadowCreatureSfxCadence Cadence { get; }
        internal ShadowCreatureSfxSpatial Spatial { get; set; } = ShadowCreatureSfxSpatial.AtOrigin(0f);
        internal ShadowCreatureSfxObservedState? LastHostileState { get; set; }
        internal ShadowCreatureSfxProjectionState? LastProjectionState { get; set; }
        internal string LastAttackInstanceId { get; set; } = string.Empty;
        internal long Revision { get; set; }
        internal bool IsProjection { get; set; }
    }

    private readonly Func<
        ShadowCreatureSpecies,
        IReadOnlyDictionary<ShadowCreatureSfxCue, IReadOnlyList<IShadowCreatureSfxEffect>>
    > poolProvider;
    private readonly IShadowCreatureSfxRandom random;
    private readonly IShadowCreatureSfxDiagnostics? diagnostics;
    private readonly Dictionary<ShadowCreatureSfxOwnerKey, OwnerState> owners = new();
    private readonly HashSet<string> reportedDiagnosticKeys = new(StringComparer.Ordinal);
    private bool disposed;

    internal ShadowCreatureSfxCoordinator(
        Func<
            ShadowCreatureSpecies,
            IReadOnlyDictionary<ShadowCreatureSfxCue, IReadOnlyList<IShadowCreatureSfxEffect>>
        > poolProvider,
        IShadowCreatureSfxRandom random,
        IShadowCreatureSfxDiagnostics? diagnostics = null
    )
    {
        this.poolProvider = poolProvider ?? throw new ArgumentNullException(nameof(poolProvider));
        this.random = random ?? throw new ArgumentNullException(nameof(random));
        this.diagnostics = diagnostics;
    }

    internal int OwnerCount => owners.Count;

    internal ShadowCreatureSfxOwnerLane? GetLane(ShadowCreatureSfxOwnerKey owner) =>
        owners.TryGetValue(owner, out var state) ? state.Lane : null;

    internal void ObserveHostile(ShadowCreatureSfxHostileObservation observation)
    {
        if (disposed)
            return;

        if (
            owners.TryGetValue(observation.Owner, out var existing)
            && observation.StateRevision < existing.Revision
        )
        {
            return;
        }

        var state = GetOrCreate(observation.Owner, observation.Species, isProjection: false);
        state.IsProjection = false;
        state.Spatial = observation.Spatial;
        state.Revision = Math.Max(state.Revision, observation.StateRevision);

        var changed = state.LastHostileState != observation.State;
        if (changed)
        {
            if (observation.State is ShadowCreatureSfxObservedState.Idle)
                EnterCadence(state, ShadowCreatureSfxCadenceState.Idle, observation.NowSeconds);
            else if (observation.State is ShadowCreatureSfxObservedState.Chase)
                EnterCadence(state, ShadowCreatureSfxCadenceState.Chase, observation.NowSeconds);
            else
                state.Cadence.Leave();

            if (observation.State == ShadowCreatureSfxObservedState.Taunt)
            {
                state.Lane.Request(
                    ShadowCreatureSfxCue.Taunt,
                    DedupKey("taunt", observation.StateRevision),
                    observation.Spatial
                );
            }
        }

        if (
            observation.State == ShadowCreatureSfxObservedState.Attack
            && !string.IsNullOrWhiteSpace(observation.AttackInstanceId)
            && !string.Equals(
                state.LastAttackInstanceId,
                observation.AttackInstanceId,
                StringComparison.Ordinal
            )
        )
        {
            var cue = ShadowCreatureSfxPolicy.SelectAttackCue(
                observation.Species,
                observation.HealthRatio
            );
            state.Lane.Request(
                cue,
                string.Concat("attack:", observation.AttackInstanceId),
                observation.Spatial
            );
            state.LastAttackInstanceId = observation.AttackInstanceId;
        }

        state.LastHostileState = observation.State;
        TryStartDueCadence(state, observation.NowSeconds);
    }

    internal void ObserveHarmless(ShadowCreatureSfxHarmlessObservation observation)
    {
        if (disposed)
            return;

        if (
            owners.TryGetValue(observation.Owner, out var existing)
            && observation.StateRevision < existing.Revision
        )
        {
            return;
        }

        var state = GetOrCreate(observation.Owner, observation.Species, isProjection: true);
        state.IsProjection = true;
        state.Spatial = observation.Spatial;
        state.Revision = Math.Max(state.Revision, observation.StateRevision);

        var cadenceState = observation.State switch
        {
            ShadowCreatureSfxProjectionState.Idle
                or ShadowCreatureSfxProjectionState.Wander =>
                ShadowCreatureSfxCadenceState.Idle,
            ShadowCreatureSfxProjectionState.Fleeing =>
                ShadowCreatureSfxCadenceState.Chase,
            _ => (ShadowCreatureSfxCadenceState?)null,
        };

        if (!cadenceState.HasValue)
        {
            state.Cadence.Leave();
            if (
                observation.State is ShadowCreatureSfxProjectionState.Spawning
                    or ShadowCreatureSfxProjectionState.FadingOut
            )
            {
                state.Lane.Stop();
            }
        }
        else if (
            state.LastProjectionState is null
            || !SameCadence(state.Cadence.State, cadenceState.Value)
        )
        {
            EnterCadence(state, cadenceState.Value, observation.NowSeconds);
        }

        state.LastProjectionState = observation.State;
        TryStartDueCadence(state, observation.NowSeconds);
    }

    /// <summary>Updates a live owner's spatial sample without changing its behavior state.</summary>
    internal void UpdateSpatial(ShadowCreatureSfxOwnerKey owner, ShadowCreatureSfxSpatial spatial)
    {
        if (!disposed && owners.TryGetValue(owner, out var state))
            state.Spatial = spatial;
    }

    /// <summary>Call only after the hit policy has accepted positive non-lethal damage.</summary>
    internal void NotifyHostileHit(
        ShadowCreatureSfxOwnerKey owner,
        ShadowCreatureSpecies species,
        ShadowCreatureSfxSpatial spatial,
        long hitRevision,
        bool lethal
    )
    {
        if (disposed || lethal)
            return;
        var state = GetOrCreate(owner, species, isProjection: false);
        if (hitRevision < state.Revision)
            return;
        state.Revision = Math.Max(state.Revision, hitRevision);
        state.Spatial = spatial;
        state.Lane.Request(
            ShadowCreatureSfxCue.Hurt,
            DedupKey("hurt", hitRevision),
            spatial
        );
    }

    /// <summary>Call only after the authority has confirmed Dying with health zero.</summary>
    internal void NotifyConfirmedDeath(
        ShadowCreatureSfxOwnerKey owner,
        ShadowCreatureSpecies species,
        ShadowCreatureSfxSpatial spatial,
        long dyingRevision
    )
    {
        if (disposed)
            return;
        var state = GetOrCreate(owner, species, isProjection: false);
        if (dyingRevision < state.Revision)
            return;
        state.Revision = Math.Max(state.Revision, dyingRevision);
        state.Spatial = spatial;
        state.Lane.Request(
            ShadowCreatureSfxCue.Death,
            DedupKey("death", dyingRevision),
            spatial
        );
    }

    internal void Tick(double nowSeconds, float soundVolume)
    {
        if (disposed)
            return;
        foreach (var state in owners.Values)
        {
            state.Lane.Tick(state.Spatial, soundVolume);
            if (soundVolume > 0f)
                TryStartDueCadence(state, nowSeconds);
        }
    }

    internal void RemoveOwner(ShadowCreatureSfxOwnerKey owner)
    {
        if (!owners.Remove(owner, out var state))
            return;
        state.Lane.Dispose();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        foreach (var state in owners.Values)
            state.Lane.Dispose();
        owners.Clear();
    }

    private OwnerState GetOrCreate(
        ShadowCreatureSfxOwnerKey owner,
        ShadowCreatureSpecies species,
        bool isProjection
    )
    {
        if (owners.TryGetValue(owner, out var existing))
        {
            if (existing.Species == species)
                return existing;
            existing.Lane.Dispose();
            owners.Remove(owner);
        }

        IReadOnlyDictionary<ShadowCreatureSfxCue, IReadOnlyList<IShadowCreatureSfxEffect>> pools;
        try
        {
            pools = poolProvider(species)
                ?? throw new InvalidOperationException("sfx-pool-provider-returned-null");
        }
        catch (Exception exception)
        {
            ReportOnce(
                string.Concat("pool-provider|", species),
                "sfx.pool-provider",
                string.Concat(species, ":", exception.GetType().Name)
            );
            pools = new Dictionary<ShadowCreatureSfxCue, IReadOnlyList<IShadowCreatureSfxEffect>>();
        }

        ShadowCreatureSfxOwnerLane lane;
        try
        {
            lane = new ShadowCreatureSfxOwnerLane(owner, species, pools, random);
        }
        catch (Exception exception)
        {
            ReportOnce(
                string.Concat("pool-invalid|", species),
                "sfx.pool-invalid",
                string.Concat(species, ":", exception.GetType().Name)
            );
            lane = new ShadowCreatureSfxOwnerLane(
                owner,
                species,
                new Dictionary<ShadowCreatureSfxCue, IReadOnlyList<IShadowCreatureSfxEffect>>(),
                random
            );
        }
        var created = new OwnerState(owner, species, lane, random)
        {
            IsProjection = isProjection,
        };
        owners.Add(owner, created);
        return created;
    }

    private static void EnterCadence(
        OwnerState state,
        ShadowCreatureSfxCadenceState cadenceState,
        double nowSeconds
    )
    {
        var revision = state.Revision == 0 ? 1 : state.Revision;
        state.Cadence.Enter(cadenceState, nowSeconds, revision);
    }

    private static bool SameCadence(
        ShadowCreatureSfxCadenceState? left,
        ShadowCreatureSfxCadenceState right
    ) => left.HasValue && left.Value == right;

    private static string DedupKey(string kind, long revision) =>
        string.Concat(kind, ":", revision.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static void TryStartDueCadence(OwnerState state, double nowSeconds)
    {
        if (
            !state.Cadence.IsDue(nowSeconds)
            || state.Lane.HasActiveInstance
            || !state.Spatial.IsAudible
            || state.Spatial.Volume <= 0f
        )
            return;
        var cue = state.Cadence.State == ShadowCreatureSfxCadenceState.Chase
            ? ShadowCreatureSfxCue.Chase
            : ShadowCreatureSfxCue.Idle;
        var key = string.Concat(
            "cadence:",
            cue.ToString(),
            ":",
            state.Cadence.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ":",
            state.Cadence.NextDueAtSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
        );
        var result = state.Lane.Request(cue, key, state.Spatial);
        if (result.Status == ShadowCreatureSfxRequestStatus.Started)
            state.Cadence.MarkStarted(nowSeconds);
    }

    private void ReportOnce(string key, string code, string reason)
    {
        if (diagnostics is null || !reportedDiagnosticKeys.Add(key))
            return;
        try
        {
            diagnostics.Report(code, reason);
        }
        catch
        {
            // Diagnostics must never affect gameplay or owner creation.
        }
    }
}
