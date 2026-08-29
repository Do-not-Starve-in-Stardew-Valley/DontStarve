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
/// provider. Each owner lane may have multiple overlapping one-shot voices; cadence due times are
/// retained only when a request cannot start for a concrete reason such as silence or a missing
/// pool.
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

    private sealed class DetachedVoiceState
    {
        internal DetachedVoiceState(
            ShadowCreatureSfxOwnerKey owner,
            ShadowCreatureSfxDetachedVoice voice,
            ShadowCreatureSfxSpatial spatial
        )
        {
            Owner = owner;
            Voice = voice;
            Spatial = spatial;
        }

        internal ShadowCreatureSfxOwnerKey Owner { get; }
        internal ShadowCreatureSfxDetachedVoice Voice { get; }
        internal ShadowCreatureSfxSpatial Spatial { get; set; }
    }

    private readonly Func<
        ShadowCreatureSpecies,
        IReadOnlyDictionary<ShadowCreatureSfxCue, IReadOnlyList<IShadowCreatureSfxEffect>>
    > poolProvider;
    private readonly IShadowCreatureSfxRandom random;
    private readonly IShadowCreatureSfxDiagnostics? diagnostics;
    private readonly Action<ShadowCreatureSfxPlaybackStarted>? playbackStarted;
    private readonly Dictionary<ShadowCreatureSfxOwnerKey, OwnerState> owners = new();
    private readonly List<DetachedVoiceState> detachedVoices = new();
    private readonly HashSet<string> reportedDiagnosticKeys = new(StringComparer.Ordinal);
    private bool newSoundsAllowed = true;
    private bool disposed;

    internal ShadowCreatureSfxCoordinator(
        Func<
            ShadowCreatureSpecies,
            IReadOnlyDictionary<ShadowCreatureSfxCue, IReadOnlyList<IShadowCreatureSfxEffect>>
        > poolProvider,
        IShadowCreatureSfxRandom random,
        IShadowCreatureSfxDiagnostics? diagnostics = null,
        Action<ShadowCreatureSfxPlaybackStarted>? playbackStarted = null
    )
    {
        this.poolProvider = poolProvider ?? throw new ArgumentNullException(nameof(poolProvider));
        this.random = random ?? throw new ArgumentNullException(nameof(random));
        this.diagnostics = diagnostics;
        this.playbackStarted = playbackStarted;
    }

    internal int OwnerCount => owners.Count;

    internal ShadowCreatureSfxOwnerLane? GetLane(ShadowCreatureSfxOwnerKey owner) =>
        owners.TryGetValue(owner, out var state) ? state.Lane : null;

    internal bool NewSoundsAllowed => newSoundsAllowed;

    /// <summary>
    /// Gates only new voice creation. Existing instances continue through their normal XNA
    /// lifetime; cadence due times are shifted on resume so focus loss does not create a burst.
    /// </summary>
    internal void SetNewSoundsAllowed(bool allowed, double nowSeconds)
    {
        if (disposed || newSoundsAllowed == allowed)
            return;

        newSoundsAllowed = allowed;
        foreach (var state in owners.Values)
        {
            if (allowed)
                state.Cadence.ResumeScheduling(nowSeconds);
            else
                state.Cadence.PauseScheduling(nowSeconds);
        }
    }

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
                if (newSoundsAllowed)
                {
                    state.Lane.Request(
                        ShadowCreatureSfxCue.Taunt,
                        DedupKey("taunt", observation.StateRevision),
                        observation.Spatial
                    );
                }
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
            var cue = ShadowCreatureSfxPolicy.SelectAttackCue(observation.Species);
            if (newSoundsAllowed)
            {
                state.Lane.Request(
                    cue,
                    string.Concat("attack:", observation.AttackInstanceId),
                    observation.Spatial
                );
                state.LastAttackInstanceId = observation.AttackInstanceId;
            }
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
        bool lethal,
        ShadowCreatureSfxHitSource source = ShadowCreatureSfxHitSource.Other
    )
    {
        if (disposed || lethal || !newSoundsAllowed)
            return;
        var state = GetOrCreate(owner, species, isProjection: false);
        if (hitRevision < state.Revision)
            return;
        state.Revision = Math.Max(state.Revision, hitRevision);
        state.Spatial = spatial;
        state.Lane.Request(
            ShadowCreatureSfxPolicy.SelectHurtCue(species, source),
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
        if (disposed || !newSoundsAllowed)
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
            if (newSoundsAllowed && soundVolume > 0f)
                TryStartDueCadence(state, nowSeconds);
        }
        for (var index = detachedVoices.Count - 1; index >= 0; index--)
        {
            var detached = detachedVoices[index];
            if (detached.Voice.Tick(detached.Spatial, soundVolume))
                continue;
            detachedVoices.RemoveAt(index);
        }
    }

    internal void RemoveOwner(ShadowCreatureSfxOwnerKey owner)
    {
        RemoveOwnerCore(owner, retainDeathVoice: true);
    }

    /// <summary>
    /// Normal harmless-projection removal. Projection Idle/Chase one-shots may outlive the visual
    /// owner and are reaped by the coordinator after their natural playback ends. If a caller ever
    /// passes a hostile owner by mistake, preserve the established normal-owner Death behavior.
    /// </summary>
    internal void RemoveProjectionOwner(ShadowCreatureSfxOwnerKey owner)
    {
        if (owners.TryGetValue(owner, out var state) && state.IsProjection)
        {
            RemoveOwnerCore(owner, retainDeathVoice: false, retainAllVoices: true);
            return;
        }

        RemoveOwnerCore(owner, retainDeathVoice: true);
    }

    /// <summary>World/title/mod cleanup path. It always interrupts retained Death voices.</summary>
    internal void ForceRemoveOwner(ShadowCreatureSfxOwnerKey owner)
    {
        RemoveOwnerCore(owner, retainDeathVoice: false);
    }

    /// <summary>Clears every live and detached voice at a world/resource boundary.</summary>
    internal void ForceRemoveAll()
    {
        foreach (var state in owners.Values)
            state.Lane.Dispose();
        owners.Clear();
        foreach (var detached in detachedVoices)
            detached.Voice.Dispose();
        detachedVoices.Clear();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        ForceRemoveAll();
    }

    private void RemoveOwnerCore(
        ShadowCreatureSfxOwnerKey owner,
        bool retainDeathVoice,
        bool retainAllVoices = false
    )
    {
        if (owners.Remove(owner, out var state))
        {
            if (retainAllVoices)
            {
                foreach (var voice in state.Lane.DetachAll())
                    detachedVoices.Add(new DetachedVoiceState(owner, voice, state.Spatial));
            }
            else if (retainDeathVoice)
            {
                foreach (var voice in state.Lane.Detach(ShadowCreatureSfxCue.Death))
                    detachedVoices.Add(new DetachedVoiceState(owner, voice, state.Spatial));
            }
            state.Lane.Dispose();
        }

        if (!retainDeathVoice && !retainAllVoices)
        {
            for (var index = detachedVoices.Count - 1; index >= 0; index--)
            {
                if (detachedVoices[index].Owner != owner)
                    continue;
                detachedVoices[index].Voice.Dispose();
                detachedVoices.RemoveAt(index);
            }
        }
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
            lane = new ShadowCreatureSfxOwnerLane(owner, species, pools, random, playbackStarted);
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
                random,
                playbackStarted
            );
        }
        var created = new OwnerState(owner, species, lane, random)
        {
            IsProjection = isProjection,
        };
        owners.Add(owner, created);
        return created;
    }

    private void EnterCadence(
        OwnerState state,
        ShadowCreatureSfxCadenceState cadenceState,
        double nowSeconds
    )
    {
        var revision = state.Revision == 0 ? 1 : state.Revision;
        state.Cadence.Enter(cadenceState, nowSeconds, revision);
        if (!newSoundsAllowed)
            state.Cadence.PauseScheduling(nowSeconds);
    }

    private static bool SameCadence(
        ShadowCreatureSfxCadenceState? left,
        ShadowCreatureSfxCadenceState right
    ) => left.HasValue && left.Value == right;

    private static string DedupKey(string kind, long revision) =>
        string.Concat(kind, ":", revision.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private void TryStartDueCadence(OwnerState state, double nowSeconds)
    {
        if (
            !newSoundsAllowed
            ||
            !state.Cadence.IsDue(nowSeconds)
            || !state.Spatial.IsAudible
            || state.Spatial.Volume <= 0f
        )
            return;
        var cue = state.Cadence.State == ShadowCreatureSfxCadenceState.Chase
            ? ShadowCreatureSfxCue.Chase
            : ShadowCreatureSfxCue.Idle;
        if (
            cue == ShadowCreatureSfxCue.Chase
            && state.Lane.HasActiveNonChaseVoice
        )
        {
            state.Cadence.DeferBecauseVoiceBusy();
            return;
        }
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
