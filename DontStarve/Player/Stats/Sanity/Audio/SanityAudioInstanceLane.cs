#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity.Audio;

/// <summary>
/// Owns at most one physical instance for one lane. Effects are borrowed and are deliberately
/// never disposed here. Every instance operation is guarded by the captured XNA owning thread.
/// </summary>
internal sealed class SanityAudioInstanceLane : IDisposable
{
    private const int MaximumDiagnosticCodes = 8;

    private readonly SanityAudioLaneKind lane;
    private readonly ISanityAudioEffect[] effects;
    private readonly bool continuous;
    private readonly ISanityAudioRandom random;
    private readonly ISanityAudioThreadContext threadContext;
    private readonly Action<SanityAudioDiagnostic>? diagnosticSink;
    private readonly HashSet<string> diagnosticCodes = new(StringComparer.Ordinal);
    private ISanityAudioInstance? current;
    private bool desiredContinuousActive;
    private bool paused;
    private bool? pendingPauseState;
    private bool failed;
    private bool stopRequested;
    private bool disposeRequested;
    private bool disposed;
    private float volume = 1f;

    internal SanityAudioInstanceLane(
        SanityAudioLaneKind lane,
        IReadOnlyList<ISanityAudioEffect> effects,
        bool continuous,
        ISanityAudioRandom random,
        ISanityAudioThreadContext threadContext,
        Action<SanityAudioDiagnostic>? diagnosticSink = null
    )
    {
        if (effects is null || effects.Count == 0)
            throw new ArgumentException("An audio lane requires at least one borrowed effect.", nameof(effects));

        this.lane = lane;
        this.effects = new ISanityAudioEffect[effects.Count];
        for (var index = 0; index < effects.Count; index++)
        {
            this.effects[index] = effects[index]
                ?? throw new ArgumentException("Borrowed effects must be non-null.", nameof(effects));
        }
        this.continuous = continuous;
        this.random = random ?? throw new ArgumentNullException(nameof(random));
        this.threadContext = threadContext ?? throw new ArgumentNullException(nameof(threadContext));
        this.diagnosticSink = diagnosticSink;
    }

    internal int PhysicalInstanceCount => current is null ? 0 : 1;

    internal bool IsFailed => failed;

    internal bool IsDisposed => disposed;

    internal void SetContinuousActive(bool active, float targetVolume)
    {
        if (disposed || !continuous)
            return;

        desiredContinuousActive = active;
        SetVolume(targetVolume);
        if (!active)
        {
            pendingPauseState = null;
            StopAndDispose();
            return;
        }
        if (!paused && !failed)
            EnsurePlaying();
    }

    internal bool TriggerOneShot(float targetVolume, int? effectIndex = null)
    {
        if (disposed || continuous || failed || paused)
            return false;

        SetVolume(targetVolume);
        StopAndDispose();
        if (!failed)
            return EnsurePlaying(effectIndex);
        return false;
    }

    internal void SetVolume(float targetVolume)
    {
        volume = Math.Clamp(targetVolume, 0f, 1f);
        if (current is null || failed || disposed)
            return;
        if (!EnsureOwningThread("volume"))
            return;

        try
        {
            current.Volume = volume;
        }
        catch (Exception exception)
        {
            Fail("audio.lane.volume-failed", exception);
            StopAndDisposeCore();
        }
    }

    internal void Pause()
    {
        if (disposed || paused)
            return;
        paused = true;
        if (current is null || failed)
            return;

        if (!threadContext.IsOnOwningThread)
        {
            // Focus callbacks normally arrive on the XNA thread, but keep the request alive if a
            // platform raises one elsewhere. Do not mark the lane failed or turn a pause into a
            // deferred stop; the owning thread will apply the same pause before its next tick.
            pendingPauseState = true;
            return;
        }

        pendingPauseState = null;
        PauseCurrent();
    }

    internal void Resume()
    {
        if (disposed || !paused)
            return;
        paused = false;
        if (failed)
            return;

        if (!threadContext.IsOnOwningThread)
        {
            pendingPauseState = false;
            return;
        }

        pendingPauseState = null;
        ResumeCurrent();
    }

    internal void StopPlayback()
    {
        desiredContinuousActive = false;
        if (current is not null && !threadContext.IsOnOwningThread)
        {
            stopRequested = true;
            Fail(
                "audio.lane.wrong-thread",
                "Stop was requested outside the captured SMAPI/XNA owning thread."
            );
            return;
        }
        StopAndDispose();
    }

    internal void Tick()
    {
        if (disposed)
            return;
        if (disposeRequested || stopRequested)
        {
            if (!threadContext.IsOnOwningThread)
                return;
            StopAndDisposeCore();
            stopRequested = false;
            if (disposeRequested)
            {
                disposed = true;
                disposeRequested = false;
                return;
            }
        }

        if (pendingPauseState.HasValue)
        {
            if (!threadContext.IsOnOwningThread)
                return;

            var requestedPause = pendingPauseState.Value;
            pendingPauseState = null;
            if (requestedPause)
                PauseCurrent();
            else
                ResumeCurrent();
        }

        if (failed || paused || !EnsureOwningThread("tick"))
            return;

        if (current is not null)
        {
            try
            {
                if (current.State == SanityAudioPlaybackState.Stopped)
                    StopAndDisposeCore();
            }
            catch (Exception exception)
            {
                Fail("audio.lane.state-failed", exception);
                StopAndDisposeCore();
            }
        }

        if (current is null && continuous && desiredContinuousActive && !failed)
            EnsurePlaying();
    }

    public void Dispose()
    {
        if (disposed || disposeRequested)
            return;
        desiredContinuousActive = false;
        if (!threadContext.IsOnOwningThread)
        {
            disposeRequested = true;
            Fail(
                "audio.lane.wrong-thread",
                "Dispose was requested outside the captured SMAPI/XNA owning thread."
            );
            return;
        }

        StopAndDisposeCore();
        disposed = true;
    }

    private bool EnsurePlaying(int? effectIndex = null)
    {
        if (current is not null || failed || disposed || !EnsureOwningThread("play"))
            return false;

        ISanityAudioInstance? created = null;
        try
        {
            var index = effectIndex ?? random.NextIndex(effects.Length);
            if (index < 0 || index >= effects.Length)
            {
                Fail(
                    "audio.lane.random-index-invalid",
                    "The injected bounded random source returned an out-of-range effect index."
                );
                return false;
            }

            created = effects[index].CreateInstance();
            if (created is null)
            {
                Fail(
                    "audio.lane.instance-missing",
                    "The borrowed effect returned no owned SoundEffectInstance."
                );
                return false;
            }
            current = created;
            created.Volume = volume;
            created.Play();
            return true;
        }
        catch (Exception exception)
        {
            Fail("audio.lane.play-failed", exception);
            if (current is not null)
                StopAndDisposeCore();
            else if (created is not null)
                DisposeCreatedAfterFailure(created);
            return false;
        }
    }

    private void PauseCurrent()
    {
        if (current is null || failed)
            return;

        try
        {
            if (current.State == SanityAudioPlaybackState.Playing)
                current.Pause();
        }
        catch (Exception exception)
        {
            Fail("audio.lane.pause-failed", exception);
            StopAndDisposeCore();
        }
    }

    private void ResumeCurrent()
    {
        if (failed)
            return;

        try
        {
            if (current is null)
            {
                if (continuous && desiredContinuousActive)
                    EnsurePlaying();
                return;
            }

            var state = current.State;
            if (state == SanityAudioPlaybackState.Paused)
                current.Resume();
            else if (
                state == SanityAudioPlaybackState.Stopped
                && continuous
                && desiredContinuousActive
            )
            {
                // A platform can report Stopped if the focus callback raced the backend. Reuse the
                // same instance before Tick() gets a chance to dispose it, so focus recovery never
                // selects a different random clip for a continuous lane.
                current.Play();
            }
        }
        catch (Exception exception)
        {
            Fail("audio.lane.resume-failed", exception);
            StopAndDisposeCore();
        }
    }

    private void StopAndDispose()
    {
        if (current is null)
            return;
        if (!EnsureOwningThread("stop-dispose"))
            return;
        StopAndDisposeCore();
    }

    private void StopAndDisposeCore()
    {
        var instance = current;
        current = null;
        if (instance is null)
            return;

        try
        {
            if (instance.State != SanityAudioPlaybackState.Stopped)
                instance.Stop();
        }
        catch (Exception exception)
        {
            Fail("audio.lane.stop-failed", exception);
        }

        try
        {
            instance.Dispose();
        }
        catch (Exception exception)
        {
            Fail("audio.lane.dispose-failed", exception);
        }
    }

    private void DisposeCreatedAfterFailure(ISanityAudioInstance instance)
    {
        try
        {
            instance.Dispose();
        }
        catch (Exception exception)
        {
            Fail("audio.lane.dispose-failed", exception);
        }
    }

    private bool EnsureOwningThread(string operation)
    {
        if (threadContext.IsOnOwningThread)
            return true;

        if (current is not null)
            stopRequested = true;
        Fail(
            "audio.lane.wrong-thread",
            $"{operation} was rejected outside the captured SMAPI/XNA owning thread."
        );
        return false;
    }

    private void Fail(string code, Exception exception)
    {
        Fail(
            code,
            $"The {lane} lane failed with {exception.GetType().Name}: {exception.Message}"
        );
    }

    private void Fail(string code, string reason)
    {
        failed = true;
        if (
            diagnosticSink is null
            || diagnosticCodes.Count >= MaximumDiagnosticCodes
            || !diagnosticCodes.Add(code)
        )
        {
            return;
        }
        diagnosticSink(new SanityAudioDiagnostic(lane, code, reason));
    }
}
