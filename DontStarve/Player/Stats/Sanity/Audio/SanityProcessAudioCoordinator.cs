#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DontStarve.Player.Stats.Sanity.Audio;

/// <summary>
/// Process-wide logical claim aggregator. Claim mutation is revision-gated per owner/screen while
/// the cached counters and ordered winner keep the per-tick path free of scans and allocations.
/// </summary>
internal sealed class SanityProcessAudioCoordinator : IDisposable
{
    internal const int MaximumLocalClaims = 16;
    internal const int MaximumPhysicalInstances = 5;

    private const int MaximumDiagnosticCodes = 32;

    private readonly Dictionary<SanityAudioClaimKey, SanityAudioOwnerClaim> claims = new();
    // A removed owner/screen claim keeps its last accepted revision until the day/session is
    // cleared. This prevents an old tier snapshot from immediately resurrecting stale audio.
    private readonly Dictionary<SanityAudioClaimKey, long> revisionReceipts = new();
    // Local menus remove only their owner from physical eligibility. Claims and tier-edge
    // receipts remain so another split-screen owner can continue and menu close can't replay
    // an already-consumed danger edge.
    private readonly HashSet<SanityAudioClaimKey> locallyPausedClaims = new();
    private readonly Dictionary<SanityAudioClaimKey, SanityDarknessWarningClaim>
        darknessWarningClaims = new();
    private readonly Dictionary<SanityAudioClaimKey, DarknessWarningReceipt>
        darknessWarningReceipts = new();
    private readonly HashSet<SanityAudioClaimKey> locallyPausedDarknessWarnings = new();
    private readonly SortedSet<SanityAudioOwnerClaim> orderedClaims =
        new(SanityAudioWinnerComparer.Instance);
    private readonly HashSet<string> diagnosticCodes = new(StringComparer.Ordinal);
    private readonly ISanityProcessAudioOutput output;
    private readonly Action<SanityAudioDiagnostic>? diagnosticSink;
    private int ambienceCount;
    private int whispersCount;
    private int dangerCount;
    private int aggregateDangerClaimCount;
    private bool dangerArmed = true;
    private bool processPaused;
    private bool darknessWarningProcessPaused;
    private bool eventSuspended;
    private bool specialEventAudioAllowed;
    private bool disposed;
    private long generation;

    internal SanityProcessAudioCoordinator(
        ISanityProcessAudioOutput output,
        Action<SanityAudioDiagnostic>? diagnosticSink = null
    )
    {
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.diagnosticSink = diagnosticSink;
    }

    internal SanityAudioClaimUpdateResult SubmitClaim(
        SanityAudioOwnerClaim claim,
        bool playbackPaused = false
    )
    {
        if (disposed)
            return Result(SanityAudioClaimUpdateStatus.Disposed, "audio.coordinator.disposed");

        var validationReason = ValidateClaim(claim);
        if (validationReason is not null)
        {
            Report(null, validationReason, "The owner/screen audio claim was rejected.");
            return Result(SanityAudioClaimUpdateStatus.Invalid, validationReason);
        }

        var key = claim.Key;
        if (revisionReceipts.TryGetValue(key, out var acceptedRevision))
        {
            if (claim.Revision < acceptedRevision)
                return Result(SanityAudioClaimUpdateStatus.IgnoredStale, "audio.claim.stale");
            if (claim.Revision == acceptedRevision)
                return Result(SanityAudioClaimUpdateStatus.IgnoredDuplicate, "audio.claim.duplicate");
        }

        var previousAmbience = ambienceCount > 0;
        var previousWhispers = whispersCount > 0;
        var previousDanger = dangerCount > 0;
        var previousAggregateDanger = aggregateDangerClaimCount > 0;
        if (claims.TryGetValue(key, out var existing))
        {
            RemoveClaimFromCounters(existing, locallyPausedClaims.Contains(key));
        }
        else if (!revisionReceipts.ContainsKey(key) && revisionReceipts.Count >= MaximumLocalClaims)
        {
            const string reason = "audio.claim.capacity-exceeded";
            Report(null, reason, "The bounded local owner/screen claim capacity was exceeded.");
            return Result(SanityAudioClaimUpdateStatus.Invalid, reason);
        }

        revisionReceipts[key] = claim.Revision;
        claims[key] = claim;
        if (playbackPaused)
            locallyPausedClaims.Add(key);
        else
            locallyPausedClaims.Remove(key);
        AddClaimToCounters(claim, playbackPaused);
        generation++;
        ApplyAggregateTransitions(previousAmbience, previousWhispers, previousDanger);
        ApplyDangerEdge(previousAggregateDanger);
        return Result(SanityAudioClaimUpdateStatus.Applied, "audio.claim.applied");
    }

    internal SanityAudioClaimUpdateResult RemoveClaim(string playerKey, int screenId)
    {
        if (disposed)
            return Result(SanityAudioClaimUpdateStatus.Disposed, "audio.coordinator.disposed");

        var key = new SanityAudioClaimKey(playerKey, screenId);
        if (!claims.Remove(key, out var existing))
            return Result(SanityAudioClaimUpdateStatus.NoChange, "audio.claim.not-found");

        var previousAmbience = ambienceCount > 0;
        var previousWhispers = whispersCount > 0;
        var previousDanger = dangerCount > 0;
        var previousAggregateDanger = aggregateDangerClaimCount > 0;
        var wasLocallyPaused = locallyPausedClaims.Remove(key);
        RemoveClaimFromCounters(existing, wasLocallyPaused);
        generation++;
        ApplyAggregateTransitions(previousAmbience, previousWhispers, previousDanger);
        ApplyDangerEdge(previousAggregateDanger);
        return Result(SanityAudioClaimUpdateStatus.Removed, "audio.claim.removed");
    }

    internal SanityAudioClaimUpdateResult SetClaimPlaybackPaused(
        string playerKey,
        int screenId,
        bool paused
    )
    {
        if (disposed)
            return Result(SanityAudioClaimUpdateStatus.Disposed, "audio.coordinator.disposed");

        var key = new SanityAudioClaimKey(playerKey, screenId);
        if (!claims.TryGetValue(key, out var claim))
            return Result(SanityAudioClaimUpdateStatus.NoChange, "audio.claim.not-found");

        var changed = paused
            ? locallyPausedClaims.Add(key)
            : locallyPausedClaims.Remove(key);
        if (!changed)
            return Result(SanityAudioClaimUpdateStatus.NoChange, "audio.claim.pause-unchanged");

        var previousAmbience = ambienceCount > 0;
        var previousWhispers = whispersCount > 0;
        var previousDanger = dangerCount > 0;
        if (paused)
            RemoveFromEligibleAggregate(claim);
        else
            AddToEligibleAggregate(claim);
        generation++;
        ApplyAggregateTransitions(previousAmbience, previousWhispers, previousDanger);
        return Result(
            SanityAudioClaimUpdateStatus.Applied,
            paused ? "audio.claim.locally-paused" : "audio.claim.locally-resumed"
        );
    }

    internal SanityAudioClaimUpdateResult SubmitDarknessWarningClaim(
        SanityDarknessWarningClaim claim
    )
    {
        if (disposed)
            return Result(SanityAudioClaimUpdateStatus.Disposed, "audio.coordinator.disposed");

        var validationReason = ValidateDarknessWarningClaim(claim);
        if (validationReason is not null)
        {
            Report(
                SanityAudioLaneKind.DarknessWarning,
                validationReason,
                "The darkness warning claim was rejected."
            );
            return Result(SanityAudioClaimUpdateStatus.Invalid, validationReason);
        }

        var key = claim.Key;
        if (darknessWarningReceipts.TryGetValue(key, out var accepted))
        {
            if (!string.Equals(accepted.SessionId, claim.SessionId, StringComparison.Ordinal))
            {
                return Result(
                    SanityAudioClaimUpdateStatus.Invalid,
                    "audio.darkness-warning.session-conflict"
                );
            }
            if (claim.Revision < accepted.Revision)
            {
                return Result(
                    SanityAudioClaimUpdateStatus.IgnoredStale,
                    "audio.darkness-warning.stale"
                );
            }
            if (claim.Revision == accepted.Revision)
            {
                return Result(
                    string.Equals(accepted.RequestId, claim.RequestId, StringComparison.Ordinal)
                        ? SanityAudioClaimUpdateStatus.IgnoredDuplicate
                        : SanityAudioClaimUpdateStatus.Invalid,
                    string.Equals(accepted.RequestId, claim.RequestId, StringComparison.Ordinal)
                        ? "audio.darkness-warning.duplicate"
                        : "audio.darkness-warning.correlation-conflict"
                );
            }
        }
        else if (darknessWarningReceipts.Count >= MaximumLocalClaims)
        {
            const string reason = "audio.darkness-warning.capacity-exceeded";
            Report(
                SanityAudioLaneKind.DarknessWarning,
                reason,
                "The bounded darkness warning claim capacity was exceeded."
            );
            return Result(SanityAudioClaimUpdateStatus.Invalid, reason);
        }

        var wasActive = darknessWarningClaims.Count > 0;
        darknessWarningReceipts[key] = new DarknessWarningReceipt(
            claim.SessionId,
            claim.RequestId,
            claim.Revision
        );
        darknessWarningClaims[key] = claim;
        generation++;
        if (!wasActive)
        {
            SafeOutput(
                () =>
                {
                    output.SetDarknessWarningClip(claim.WarningClipId);
                    output.SetDarknessWarningActive(true);
                },
                "audio.output.darkness-warning-failed"
            );
            ApplyDarknessWarningPause();
        }
        return Result(
            SanityAudioClaimUpdateStatus.Applied,
            "audio.darkness-warning.applied"
        );
    }

    internal SanityAudioClaimUpdateResult RemoveDarknessWarningClaim(
        string playerKey,
        int screenId,
        string sessionId,
        string requestId
    )
    {
        if (disposed)
            return Result(SanityAudioClaimUpdateStatus.Disposed, "audio.coordinator.disposed");

        var key = new SanityAudioClaimKey(playerKey, screenId);
        if (
            !darknessWarningClaims.TryGetValue(key, out var existing)
            || !string.Equals(existing.SessionId, sessionId, StringComparison.Ordinal)
            || !string.Equals(existing.RequestId, requestId, StringComparison.Ordinal)
        )
        {
            return Result(
                SanityAudioClaimUpdateStatus.NoChange,
                "audio.darkness-warning.not-found"
            );
        }

        darknessWarningClaims.Remove(key);
        locallyPausedDarknessWarnings.Remove(key);
        generation++;
        if (darknessWarningClaims.Count == 0)
        {
            SafeOutput(
                () => output.SetDarknessWarningActive(false),
                "audio.output.darkness-warning-failed"
            );
        }
        ApplyDarknessWarningPause();
        return Result(
            SanityAudioClaimUpdateStatus.Removed,
            "audio.darkness-warning.removed"
        );
    }

    internal int RemoveDarknessWarningOwner(string playerKey)
    {
        if (disposed || string.IsNullOrWhiteSpace(playerKey))
            return 0;
        return RemoveDarknessWarningKeys(
            CollectDarknessWarningKeys(key =>
                string.Equals(key.PlayerKey, playerKey, StringComparison.Ordinal)
            )
        );
    }

    internal int RemoveInvalidDarknessWarningScreens(Func<int, bool> isValidScreen)
    {
        ArgumentNullException.ThrowIfNull(isValidScreen);
        if (disposed)
            return 0;
        return RemoveDarknessWarningKeys(
            CollectDarknessWarningKeys(key => !isValidScreen(key.ScreenId))
        );
    }

    internal void ClearDarknessWarningClaims()
    {
        if (disposed)
            return;
        var hadState = darknessWarningClaims.Count > 0 || darknessWarningReceipts.Count > 0;
        darknessWarningClaims.Clear();
        darknessWarningReceipts.Clear();
        locallyPausedDarknessWarnings.Clear();
        if (hadState)
        {
            generation++;
            SafeOutput(
                () => output.SetDarknessWarningActive(false),
                "audio.output.darkness-warning-failed"
            );
        }
        ApplyDarknessWarningPause();
    }

    internal SanityAudioClaimUpdateResult SetDarknessWarningClaimPlaybackPaused(
        string playerKey,
        int screenId,
        bool paused
    )
    {
        if (disposed)
            return Result(SanityAudioClaimUpdateStatus.Disposed, "audio.coordinator.disposed");

        var key = new SanityAudioClaimKey(playerKey, screenId);
        var changed = paused
            ? locallyPausedDarknessWarnings.Add(key)
            : locallyPausedDarknessWarnings.Remove(key);
        if (!changed)
            return Result(
                SanityAudioClaimUpdateStatus.NoChange,
                "audio.darkness-warning.pause-unchanged"
            );

        generation++;
        ApplyDarknessWarningPause();
        return Result(
            SanityAudioClaimUpdateStatus.Applied,
            paused
                ? "audio.darkness-warning.locally-paused"
                : "audio.darkness-warning.locally-resumed"
        );
    }

    internal int RemoveOwner(string playerKey)
    {
        if (disposed || string.IsNullOrWhiteSpace(playerKey))
            return 0;

        List<SanityAudioClaimKey>? removals = null;
        foreach (var pair in claims)
        {
            if (!string.Equals(pair.Key.PlayerKey, playerKey, StringComparison.Ordinal))
                continue;
            removals ??= new List<SanityAudioClaimKey>();
            removals.Add(pair.Key);
        }
        foreach (var pair in darknessWarningClaims)
        {
            if (!string.Equals(pair.Key.PlayerKey, playerKey, StringComparison.Ordinal))
                continue;
            removals ??= new List<SanityAudioClaimKey>();
            if (!removals.Contains(pair.Key))
                removals.Add(pair.Key);
        }
        return RemoveKeys(removals);
    }

    internal int RemoveInvalidScreens(Func<int, bool> isValidScreen)
    {
        ArgumentNullException.ThrowIfNull(isValidScreen);
        if (disposed)
            return 0;

        List<SanityAudioClaimKey>? removals = null;
        foreach (var pair in claims)
        {
            if (isValidScreen(pair.Key.ScreenId))
                continue;
            removals ??= new List<SanityAudioClaimKey>();
            removals.Add(pair.Key);
        }
        foreach (var pair in darknessWarningClaims)
        {
            if (isValidScreen(pair.Key.ScreenId))
                continue;
            removals ??= new List<SanityAudioClaimKey>();
            if (!removals.Contains(pair.Key))
                removals.Add(pair.Key);
        }
        return RemoveKeys(removals);
    }

    internal void SetProcessPaused(bool paused)
    {
        if (disposed || processPaused == paused)
            return;

        processPaused = paused;
        darknessWarningProcessPaused = paused;
        generation++;
        // Focus pause is intentionally narrower than the legacy all-lane SetPaused seam: the
        // low-Sanity ambience/whispers clips must pause in place, while threshold one-shots and
        // darkness warnings keep their existing behavior.
        SafeOutput(
            () => output.SetContinuousPoolsPaused(paused),
            "audio.output.continuous-pools-pause-failed"
        );
        ApplyDarknessWarningPause();
    }

    /// <summary>
    /// Game1.paused/dialogue and local menus have different effects on the ordinary tier lanes.
    /// They still share this process-level warning pause so the selected warning instance resumes
    /// in place instead of being recreated.
    /// </summary>
    internal void SetDarknessWarningProcessPaused(bool paused)
    {
        if (disposed || darknessWarningProcessPaused == paused)
            return;

        darknessWarningProcessPaused = paused;
        generation++;
        ApplyDarknessWarningPause();
    }

    internal void TriggerDarknessAttack()
    {
        if (disposed)
            return;
        SafeOutput(output.TriggerDarknessAttack, "audio.output.darkness-attack-failed");
    }

    // Compatibility entrypoint for older callers/tests. New production code uses
    // SetClaimPlaybackPaused for a local menu and reserves this for process-wide pause/focus.
    internal void SetMenuPaused(bool paused) => SetProcessPaused(paused);

    internal void SetEventSuspended(bool suspended)
    {
        if (disposed || eventSuspended == suspended)
            return;

        eventSuspended = suspended;
        generation++;
        // Output suspension stops and disposes instances but retains desired pool state. The
        // danger receipt stays consumed, so event exit can't replay an edge that never changed.
        SafeOutput(
            () => output.SetSuspended(suspended),
            "audio.output.suspend-failed"
        );
    }

    internal void SetSpecialEventAudioAllowed(bool allowed)
    {
        if (disposed || specialEventAudioAllowed == allowed)
            return;

        specialEventAudioAllowed = allowed;
        generation++;
        SafeOutput(
            () => output.SetSpecialEventAudioAllowed(allowed),
            "audio.output.special-event-audio-failed"
        );
    }

    internal void InvalidateResources()
    {
        if (disposed)
            return;

        generation++;
        SafeOutput(output.InvalidateResources, "audio.output.invalidate-failed");
    }

    internal void ClearClaims()
    {
        if (disposed)
            return;

        var hadState = claims.Count > 0
            || revisionReceipts.Count > 0
            || ambienceCount != 0
            || whispersCount != 0
            || dangerCount != 0
            || darknessWarningClaims.Count > 0
            || darknessWarningReceipts.Count > 0
            || locallyPausedClaims.Count > 0
            || processPaused
            || eventSuspended
            || specialEventAudioAllowed;
        claims.Clear();
        revisionReceipts.Clear();
        locallyPausedClaims.Clear();
        orderedClaims.Clear();
        ambienceCount = 0;
        whispersCount = 0;
        dangerCount = 0;
        aggregateDangerClaimCount = 0;
        darknessWarningClaims.Clear();
        darknessWarningReceipts.Clear();
        locallyPausedDarknessWarnings.Clear();
        dangerArmed = true;
        processPaused = false;
        darknessWarningProcessPaused = false;
        eventSuspended = false;
        specialEventAudioAllowed = false;
        if (hadState)
            generation++;
        SafeOutput(output.Clear, "audio.output.clear-failed");
    }

    internal void Tick()
    {
        if (!disposed)
            SafeOutput(output.Tick, "audio.output.tick-failed");
    }

    internal SanityProcessAudioSnapshot Snapshot()
    {
        var copy = new List<SanityAudioOwnerClaim>(claims.Values);
        copy.Sort(SanityAudioStableClaimComparer.Instance);
        var warningCopy = new List<SanityDarknessWarningClaim>(
            darknessWarningClaims.Values
        );
        warningCopy.Sort(SanityDarknessWarningStableComparer.Instance);
        var physicalCount = 0;
        if (!disposed)
        {
            try
            {
                physicalCount = output.PhysicalInstanceCount;
            }
            catch (Exception exception)
            {
                Report(
                    null,
                    "audio.output.snapshot-failed",
                    $"Physical instance count failed with {exception.GetType().Name}: {exception.Message}"
                );
            }
        }

        return new SanityProcessAudioSnapshot(
            generation,
            new ReadOnlyCollection<SanityAudioOwnerClaim>(copy),
            new ReadOnlyCollection<SanityDarknessWarningClaim>(warningCopy),
            orderedClaims.Count == 0 ? null : orderedClaims.Min,
            ambienceCount > 0,
            whispersCount > 0,
            dangerCount > 0,
            dangerArmed,
            darknessWarningClaims.Count > 0,
            processPaused,
            locallyPausedClaims.Count,
            eventSuspended,
            physicalCount,
            disposed
        );
    }

    internal SanityProcessAudioMusicState MusicState()
    {
        return new SanityProcessAudioMusicState(
            generation,
            orderedClaims.Count == 0 ? null : orderedClaims.Min,
            dangerCount > 0,
            processPaused,
            eventSuspended
        );
    }

    public void Dispose()
    {
        if (disposed)
            return;

        ClearClaims();
        disposed = true;
        generation++;
        try
        {
            output.Dispose();
        }
        catch (Exception exception)
        {
            Report(
                null,
                "audio.output.dispose-failed",
                $"Process audio output dispose failed with {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    private int RemoveKeys(List<SanityAudioClaimKey>? removals)
    {
        if (removals is null || removals.Count == 0)
            return 0;

        var previousAmbience = ambienceCount > 0;
        var previousWhispers = whispersCount > 0;
        var previousDanger = dangerCount > 0;
        var previousAggregateDanger = aggregateDangerClaimCount > 0;
        var previousDarknessWarning = darknessWarningClaims.Count > 0;
        foreach (var key in removals)
        {
            if (claims.Remove(key, out var claim))
            {
                var wasLocallyPaused = locallyPausedClaims.Remove(key);
                RemoveClaimFromCounters(claim, wasLocallyPaused);
            }
            darknessWarningClaims.Remove(key);
        }
        generation++;
        ApplyAggregateTransitions(previousAmbience, previousWhispers, previousDanger);
        ApplyDangerEdge(previousAggregateDanger);
        if (previousDarknessWarning && darknessWarningClaims.Count == 0)
        {
            SafeOutput(
                () => output.SetDarknessWarningActive(false),
                "audio.output.darkness-warning-failed"
            );
        }
        locallyPausedDarknessWarnings.RemoveWhere(key => !darknessWarningClaims.ContainsKey(key));
        ApplyDarknessWarningPause();
        return removals.Count;
    }

    private List<SanityAudioClaimKey>? CollectDarknessWarningKeys(
        Func<SanityAudioClaimKey, bool> predicate
    )
    {
        List<SanityAudioClaimKey>? removals = null;
        foreach (var key in darknessWarningClaims.Keys)
        {
            if (!predicate(key))
                continue;
            removals ??= new List<SanityAudioClaimKey>();
            removals.Add(key);
        }
        return removals;
    }

    private int RemoveDarknessWarningKeys(List<SanityAudioClaimKey>? removals)
    {
        if (removals is null || removals.Count == 0)
            return 0;
        foreach (var key in removals)
            darknessWarningClaims.Remove(key);
        foreach (var key in removals)
            locallyPausedDarknessWarnings.Remove(key);
        generation++;
        if (darknessWarningClaims.Count == 0)
        {
            SafeOutput(
                () => output.SetDarknessWarningActive(false),
                "audio.output.darkness-warning-failed"
            );
        }
        else
        {
            ApplyDarknessWarningPause();
        }
        return removals.Count;
    }

    private void AddClaimToCounters(SanityAudioOwnerClaim claim, bool locallyPaused)
    {
        if (claim.DangerActive)
            aggregateDangerClaimCount++;
        if (!locallyPaused)
            AddToEligibleAggregate(claim);
    }

    private void RemoveClaimFromCounters(SanityAudioOwnerClaim claim, bool locallyPaused)
    {
        if (claim.DangerActive)
            aggregateDangerClaimCount--;
        if (!locallyPaused)
            RemoveFromEligibleAggregate(claim);
    }

    private void AddToEligibleAggregate(SanityAudioOwnerClaim claim)
    {
        orderedClaims.Add(claim);
        if (claim.AmbienceActive)
            ambienceCount++;
        if (claim.WhispersActive)
            whispersCount++;
        if (claim.DangerActive)
            dangerCount++;
    }

    private void RemoveFromEligibleAggregate(SanityAudioOwnerClaim claim)
    {
        orderedClaims.Remove(claim);
        if (claim.AmbienceActive)
            ambienceCount--;
        if (claim.WhispersActive)
            whispersCount--;
        if (claim.DangerActive)
            dangerCount--;
    }

    private void ApplyAggregateTransitions(
        bool previousAmbience,
        bool previousWhispers,
        bool previousDanger
    )
    {
        var ambience = ambienceCount > 0;
        var whispers = whispersCount > 0;
        var danger = dangerCount > 0;
        if (previousAmbience != ambience)
        {
            SafeOutput(
                () => output.SetPoolActive(SanityAudioLaneKind.Ambience, ambience),
                "audio.output.ambience-failed"
            );
        }
        if (previousWhispers != whispers)
        {
            SafeOutput(
                () => output.SetPoolActive(SanityAudioLaneKind.Whispers, whispers),
                "audio.output.whispers-failed"
            );
        }
        if (previousDanger == danger)
            return;

        SafeOutput(() => output.SetDangerActive(danger), "audio.output.danger-failed");
    }

    private void ApplyDangerEdge(bool previousAggregateDanger)
    {
        var aggregateDanger = aggregateDangerClaimCount > 0;
        if (previousAggregateDanger == aggregateDanger)
            return;
        if (!aggregateDanger)
        {
            dangerArmed = true;
            return;
        }

        if (!dangerArmed)
            return;
        dangerArmed = false;
        // A locally-paused owner still consumes the aggregate entry receipt. This prevents a
        // menu close or winner switch from fabricating a new 15% edge. Process/focus pause only
        // pauses continuous pools; an actual Danger entry must still play its one-shot now.
        if (!eventSuspended && dangerCount > 0)
            SafeOutput(output.TriggerDanger, "audio.output.danger-trigger-failed");
    }

    private void ApplyDarknessWarningPause()
    {
        var shouldPause = darknessWarningProcessPaused;
        if (!shouldPause && darknessWarningClaims.Count > 0)
        {
            shouldPause = true;
            foreach (var key in darknessWarningClaims.Keys)
            {
                if (!locallyPausedDarknessWarnings.Contains(key))
                {
                    shouldPause = false;
                    break;
                }
            }
        }

        SafeOutput(
            () => output.SetDarknessWarningPaused(shouldPause),
            "audio.output.darkness-warning-pause-failed"
        );
    }

    private void SafeOutput(Action action, string code)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Report(
                null,
                code,
                $"Process audio output failed with {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    private void Report(SanityAudioLaneKind? lane, string code, string reason)
    {
        if (
            diagnosticSink is null
            || diagnosticCodes.Count >= MaximumDiagnosticCodes
            || !diagnosticCodes.Add(string.Concat(lane?.ToString() ?? "process", "|", code))
        )
        {
            return;
        }
        diagnosticSink(new SanityAudioDiagnostic(lane, code, reason));
    }

    private static string? ValidateClaim(SanityAudioOwnerClaim claim)
    {
        if (!SanityPlayerKey.IsCanonical(claim.PlayerKey))
            return "audio.claim.player-key-invalid";
        if (claim.ScreenId < 0)
            return "audio.claim.screen-id-invalid";
        if (claim.Revision < 0)
            return "audio.claim.revision-invalid";
        if (
            !double.IsFinite(claim.EffectiveRatio)
            || claim.EffectiveRatio < 0d
            || claim.EffectiveRatio > 1d
        )
        {
            return "audio.claim.ratio-invalid";
        }
        if (claim.WhispersActive && !claim.AmbienceActive)
            return "audio.claim.whispers-without-ambience";
        if (claim.DangerActive && (!claim.AmbienceActive || !claim.WhispersActive))
            return "audio.claim.danger-without-parent-lanes";
        if (claim.MusicSuppressionRequested && !claim.AmbienceActive)
            return "audio.claim.music-without-ambience";
        return null;
    }

    private static string? ValidateDarknessWarningClaim(
        SanityDarknessWarningClaim claim
    )
    {
        if (!SanityPlayerKey.IsCanonical(claim.PlayerKey))
            return "audio.darkness-warning.player-key-invalid";
        if (claim.ScreenId < 0)
            return "audio.darkness-warning.screen-id-invalid";
        if (!SanityProtocol.IsValidSessionId(claim.SessionId))
            return "audio.darkness-warning.session-id-invalid";
        if (string.IsNullOrWhiteSpace(claim.RequestId) || claim.RequestId.Length > 128)
            return "audio.darkness-warning.request-id-invalid";
        if (claim.Revision < 0)
            return "audio.darkness-warning.revision-invalid";
        if (
            !string.IsNullOrWhiteSpace(claim.WarningClipId)
            && claim.WarningClipId.Length > 256
        )
        {
            return "audio.darkness-warning.clip-id-invalid";
        }
        if (
            !string.IsNullOrWhiteSpace(claim.WarningClipId)
            && (
                !double.IsFinite(claim.WarningDurationSeconds)
                || claim.WarningDurationSeconds <= 0d
            )
        )
        {
            return "audio.darkness-warning.duration-invalid";
        }
        return null;
    }

    private static SanityAudioClaimUpdateResult Result(
        SanityAudioClaimUpdateStatus status,
        string reason
    )
    {
        return new SanityAudioClaimUpdateResult(status, reason);
    }

    private sealed class SanityAudioWinnerComparer : IComparer<SanityAudioOwnerClaim>
    {
        internal static readonly SanityAudioWinnerComparer Instance = new();

        public int Compare(SanityAudioOwnerClaim left, SanityAudioOwnerClaim right)
        {
            var comparison = left.EffectiveRatio.CompareTo(right.EffectiveRatio);
            if (comparison != 0)
                return comparison;
            comparison = left.ScreenId.CompareTo(right.ScreenId);
            if (comparison != 0)
                return comparison;
            return CompareCanonicalPlayerKeys(left.PlayerKey, right.PlayerKey);
        }
    }

    private sealed class SanityAudioStableClaimComparer : IComparer<SanityAudioOwnerClaim>
    {
        internal static readonly SanityAudioStableClaimComparer Instance = new();

        public int Compare(SanityAudioOwnerClaim left, SanityAudioOwnerClaim right)
        {
            var comparison = left.ScreenId.CompareTo(right.ScreenId);
            if (comparison != 0)
                return comparison;
            return CompareCanonicalPlayerKeys(left.PlayerKey, right.PlayerKey);
        }
    }

    private sealed class SanityDarknessWarningStableComparer
        : IComparer<SanityDarknessWarningClaim>
    {
        internal static readonly SanityDarknessWarningStableComparer Instance = new();

        public int Compare(
            SanityDarknessWarningClaim left,
            SanityDarknessWarningClaim right
        )
        {
            var comparison = left.ScreenId.CompareTo(right.ScreenId);
            if (comparison != 0)
                return comparison;
            return CompareCanonicalPlayerKeys(left.PlayerKey, right.PlayerKey);
        }
    }

    private readonly record struct DarknessWarningReceipt(
        string SessionId,
        string RequestId,
        long Revision
    );

    private static int CompareCanonicalPlayerKeys(string left, string right)
    {
        var comparison = left.Length.CompareTo(right.Length);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left, right);
    }
}
