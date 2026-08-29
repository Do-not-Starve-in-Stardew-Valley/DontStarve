#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity.Audio;

internal enum SanityAudioLaneKind
{
    Ambience,
    Whispers,
    Danger,
    DarknessWarning,
    DarknessAttack,
}

internal enum SanityAudioPlaybackState
{
    Playing,
    Paused,
    Stopped,
}

internal enum SanityAudioClaimUpdateStatus
{
    Applied,
    Removed,
    NoChange,
    IgnoredDuplicate,
    IgnoredStale,
    Invalid,
    Disposed,
}

internal readonly record struct SanityAudioClaimKey(string PlayerKey, int ScreenId);

/// <summary>
/// A logical owner/screen observation. The coordinator consumes the already-evaluated tier flags;
/// it must not establish a second threshold authority.
/// </summary>
internal readonly record struct SanityAudioOwnerClaim(
    string PlayerKey,
    int ScreenId,
    long Revision,
    double EffectiveRatio,
    bool AmbienceActive,
    bool WhispersActive,
    bool DangerActive,
    bool MusicSuppressionRequested
)
{
    internal SanityAudioClaimKey Key => new(PlayerKey, ScreenId);
}

internal readonly record struct SanityAudioClaimUpdateResult(
    SanityAudioClaimUpdateStatus Status,
    string Reason
);

/// <summary>
/// A cancellable darkness-warning claim is independent from the nested Sanity tier claim. The
/// request ID prevents an old cancellation from stopping a newer cycle for the same owner/screen.
/// </summary>
internal readonly record struct SanityDarknessWarningClaim(
    string PlayerKey,
    int ScreenId,
    string SessionId,
    string RequestId,
    long Revision,
    string WarningClipId = "",
    double WarningDurationSeconds = 0d
)
{
    internal SanityAudioClaimKey Key => new(PlayerKey, ScreenId);
}

internal readonly record struct SanityAudioDiagnostic(
    SanityAudioLaneKind? Lane,
    string Code,
    string Reason
);

internal sealed class SanityProcessAudioSnapshot
{
    internal SanityProcessAudioSnapshot(
        long generation,
        IReadOnlyList<SanityAudioOwnerClaim> claims,
        IReadOnlyList<SanityDarknessWarningClaim> darknessWarningClaims,
        SanityAudioOwnerClaim? winner,
        bool ambienceActive,
        bool whispersActive,
        bool dangerActive,
        bool dangerArmed,
        bool darknessWarningActive,
        bool processPaused,
        int locallyPausedClaimCount,
        bool eventSuspended,
        int physicalInstanceCount,
        bool disposed
    )
    {
        Generation = generation;
        Claims = claims;
        DarknessWarningClaims = darknessWarningClaims;
        Winner = winner;
        AmbienceActive = ambienceActive;
        WhispersActive = whispersActive;
        DangerActive = dangerActive;
        DangerArmed = dangerArmed;
        DarknessWarningActive = darknessWarningActive;
        ProcessPaused = processPaused;
        LocallyPausedClaimCount = locallyPausedClaimCount;
        EventSuspended = eventSuspended;
        PhysicalInstanceCount = physicalInstanceCount;
        Disposed = disposed;
    }

    internal long Generation { get; }

    internal IReadOnlyList<SanityAudioOwnerClaim> Claims { get; }

    internal IReadOnlyList<SanityDarknessWarningClaim> DarknessWarningClaims { get; }

    internal SanityAudioOwnerClaim? Winner { get; }

    internal bool AmbienceActive { get; }

    internal bool WhispersActive { get; }

    internal bool DangerActive { get; }

    internal bool DangerArmed { get; }

    internal bool DarknessWarningActive { get; }

    internal bool ProcessPaused { get; }

    internal int LocallyPausedClaimCount { get; }

    // Kept as a read-only compatibility alias for older diagnostics. Local menus no longer set
    // this process-wide state; only a true game pause or focus loss does.
    internal bool MenuPaused => ProcessPaused;

    internal bool EventSuspended { get; }

    internal int PhysicalInstanceCount { get; }

    internal bool Disposed { get; }
}

/// <summary>
/// Allocation-free view consumed by the game-music coordinator. The winner comes directly from
/// <see cref="SanityProcessAudioCoordinator"/>, so music never establishes a second comparer or
/// owner claim authority.
/// </summary>
internal readonly record struct SanityProcessAudioMusicState(
    long Generation,
    SanityAudioOwnerClaim? Winner,
    bool DangerActive,
    bool ProcessPaused,
    bool EventSuspended
);

/// <summary>
/// Physical output is process-scoped. Implementations own instances they create, but only borrow
/// the loader-owned effects from which those instances were created.
/// </summary>
internal interface ISanityProcessAudioOutput : IDisposable
{
    int PhysicalInstanceCount { get; }

    void SetPoolActive(SanityAudioLaneKind lane, bool active);

    void SetDangerActive(bool active);

    void TriggerDanger();

    void SetDarknessWarningActive(bool active);

    /// <summary>
    /// Selects the host-authorized warning clip before the shared warning lane is activated. An
    /// empty ID is the compatibility path used by older multiplayer messages.
    /// </summary>
    void SetDarknessWarningClip(string warningClipId) { }

    /// <summary>Pauses/resumes only the warning one-shot without touching the attack one-shot.</summary>
    void SetDarknessWarningPaused(bool paused) { }

    /// <summary>
    /// Plays the settled darkness-attack one-shot. This lane is deliberately independent from
    /// process pause, local menus, and window focus, so the current instance can finish naturally.
    /// </summary>
    void TriggerDarknessAttack() { }

    void SetPaused(bool paused);

    /// <summary>
    /// Pauses only the low-Sanity ambience and whispers pools. Window focus uses this narrow seam so
    /// one-shot threshold and darkness-warning lanes keep their existing lifecycle.
    /// </summary>
    void SetContinuousPoolsPaused(bool paused);

    void SetSuspended(bool suspended);

    /// <summary>
    /// Keeps only the dedicated warning lane eligible while an owned 2:00 event suspends the
    /// ordinary tier lanes. Implementations predating the special flow safely ignore this seam.
    /// </summary>
    void SetSpecialEventAudioAllowed(bool allowed) { }

    void InvalidateResources();

    void Tick();

    void Clear();
}

internal interface ISanityAudioThreadContext
{
    bool IsOnOwningThread { get; }
}

internal interface ISanityAudioRandom
{
    int NextIndex(int exclusiveUpperBound);
}

/// <summary>Borrowed effect facade. Disposing the effect remains the resource loader's job.</summary>
internal interface ISanityAudioEffect
{
    string ResourceId { get; }

    ISanityAudioInstance CreateInstance();
}

/// <summary>An instance is owned exclusively by one Sanity audio lane.</summary>
internal interface ISanityAudioInstance : IDisposable
{
    SanityAudioPlaybackState State { get; }

    float Volume { get; set; }

    void Play();

    void Pause();

    void Resume();

    void Stop();
}
