#nullable enable

using System;
using System.Reflection;
using DontStarve.Music;
using HarmonyLib;
using Microsoft.Xna.Framework.Audio;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Audio;

/// <summary>
/// Stardew 1.6.15 process-music adapter. The prefix blocks only <c>Game1.updateMusic()</c> while
/// suppression is owned; game requests continue to update, so release can ask vanilla to select
/// from current state without preserving or resuming an interrupted ICue.
/// </summary>
internal sealed class SanityGameMusicRuntimeAdapter : ISanityGameMusicPhysicalAdapter
{
    internal const string ExpectedGameVersion =
        SanityGameMusicCapabilityGate.ExpectedGameVersion;
    internal const string ExpectedTargetSignature =
        SanityGameMusicCapabilityGate.ExpectedTargetSignature;

    private static SanityGameMusicRuntimeAdapter? activeAdapter;

    private readonly string patchOwnerId;
    private readonly Action<SanityAudioDiagnostic> diagnosticSink;
    private readonly Harmony? harmony;
    private readonly MethodInfo? targetMethod;
    private bool suppressionRequested;
    private bool vanillaReselectPending;
    private bool disposed;

    internal SanityGameMusicRuntimeAdapter(
        string manifestId,
        Action<SanityAudioDiagnostic> diagnosticSink
    )
    {
        if (string.IsNullOrWhiteSpace(manifestId))
            throw new ArgumentException("A manifest ID is required.", nameof(manifestId));

        patchOwnerId = string.Concat(manifestId, ".Sanity4.MusicSuppression");
        this.diagnosticSink = diagnosticSink
            ?? throw new ArgumentNullException(nameof(diagnosticSink));

        var gateReason = SanityGameMusicCapabilityGate.ValidateVersion(Game1.version);
        if (gateReason is not null)
        {
            Capability = SanityGameMusicCapability.Unavailable(
                patchOwnerId,
                ExpectedGameVersion,
                ExpectedTargetSignature,
                gateReason
            );
            return;
        }

        targetMethod = AccessTools.DeclaredMethod(
            typeof(Game1),
            nameof(Game1.updateMusic),
            Type.EmptyTypes
        );
        gateReason = SanityGameMusicCapabilityGate.ValidateTarget(
            targetMethod is not null,
            targetMethod?.IsStatic == true,
            targetMethod?.ReturnType == typeof(void),
            targetMethod?.GetParameters().Length ?? -1
        );
        if (gateReason is not null)
        {
            Capability = SanityGameMusicCapability.Unavailable(
                patchOwnerId,
                ExpectedGameVersion,
                ExpectedTargetSignature,
                gateReason
            );
            return;
        }
        if (activeAdapter is not null)
        {
            Capability = SanityGameMusicCapability.Unavailable(
                patchOwnerId,
                ExpectedGameVersion,
                ExpectedTargetSignature,
                "music.patch.process-owner-conflict"
            );
            return;
        }

        try
        {
            harmony = new Harmony(patchOwnerId);
            var prefixMethod = AccessTools.DeclaredMethod(
                typeof(SanityGameMusicRuntimeAdapter),
                nameof(BeforeGameUpdateMusic),
                Type.EmptyTypes
            );
            if (prefixMethod is null)
            {
                Capability = SanityGameMusicCapability.Unavailable(
                    patchOwnerId,
                    ExpectedGameVersion,
                    ExpectedTargetSignature,
                    "music.patch.prefix-signature-mismatch"
                );
                return;
            }

            var prefix = new HarmonyMethod(prefixMethod)
            {
                priority = Priority.First,
            };
            harmony.Patch(targetMethod, prefix: prefix);
            if (!IsOwnedPrefixInstalled(targetMethod!, patchOwnerId))
            {
                harmony.Unpatch(targetMethod, HarmonyPatchType.Prefix, patchOwnerId);
                Capability = SanityGameMusicCapability.Unavailable(
                    patchOwnerId,
                    ExpectedGameVersion,
                    ExpectedTargetSignature,
                    "music.patch.install-verification-failed"
                );
                return;
            }

            activeAdapter = this;
            Capability = SanityGameMusicCapability.Available(
                patchOwnerId,
                ExpectedGameVersion,
                ExpectedTargetSignature
            );
        }
        catch (Exception exception)
        {
            if (harmony is not null && targetMethod is not null)
            {
                try
                {
                    harmony.Unpatch(
                        targetMethod,
                        HarmonyPatchType.Prefix,
                        patchOwnerId
                    );
                }
                catch (Exception cleanupException)
                {
                    Report(
                        "music.patch.install-cleanup-failed",
                        $"A partially-installed game-music prefix could not be removed ({cleanupException.GetType().Name}: {cleanupException.Message})."
                    );
                }
            }
            Capability = SanityGameMusicCapability.Unavailable(
                patchOwnerId,
                ExpectedGameVersion,
                ExpectedTargetSignature,
                "music.patch.install-failed"
            );
            Report(
                "music.patch.install-failed",
                $"The exact game-music prefix could not be installed ({exception.GetType().Name}: {exception.Message})."
            );
        }
    }

    public SanityGameMusicCapability Capability { get; }

    public bool SuppressionApplied =>
        !disposed
        && Capability.Status == SanityGameMusicCapabilityStatus.Available
        && suppressionRequested;

    public SanityGameMusicPhysicalResult SetSuppression(bool suppress)
    {
        if (disposed)
            return Result(false, "music.adapter.disposed");
        if (Capability.Status != SanityGameMusicCapabilityStatus.Available)
            return suppress
                ? Result(false, Capability.Reason)
                : Result(true, Capability.Reason);
        if (suppressionRequested == suppress)
            return Result(true, suppress
                ? "music.suppression.already-applied"
                : "music.suppression.already-released");

        if (!suppress)
        {
            suppressionRequested = false;
            vanillaReselectPending = true;
            try
            {
                MusicManager.SetSanityMusicSuppressed(false);
            }
            catch (Exception exception)
            {
                Report(
                    "music.dawn-dusk.release-failed",
                    $"Dawn/dusk suppression could not be released ({exception.GetType().Name}: {exception.Message})."
                );
                return Result(false, "music.dawn-dusk.release-failed");
            }
            return Result(true, "music.suppression.released-for-vanilla-reselect");
        }

        suppressionRequested = true;
        vanillaReselectPending = false;
        try
        {
            MusicManager.SetSanityMusicSuppressed(true);
        }
        catch (Exception exception)
        {
            return FailAppliedSuppression(
                "music.dawn-dusk.suppression-failed",
                exception
            );
        }

        if (!TryStopCurrentGameMusic(out var reason))
        {
            suppressionRequested = false;
            vanillaReselectPending = true;
            TryReleaseProjectMusicAfterFailure();
            return Result(false, reason);
        }
        return Result(true, "music.suppression.applied");
    }

    public SanityGameMusicPhysicalResult Tick()
    {
        if (disposed)
            return Result(false, "music.adapter.disposed");
        if (Capability.Status != SanityGameMusicCapabilityStatus.Available)
            return Result(true, Capability.Reason);

        if (suppressionRequested)
        {
            if (!TryStopCurrentGameMusic(out var reason))
            {
                suppressionRequested = false;
                vanillaReselectPending = true;
                TryReleaseProjectMusicAfterFailure();
                return Result(false, reason);
            }
            return Result(true, "music.suppression.applied");
        }

        // updateMusic is already called by the exact 1.6.15 main update loop. Marking the current
        // vanilla request dirty is enough; calling changeMusicTrack here would overwrite ownership.
        if (
            vanillaReselectPending
            && Game1.game1 is not null
            && Game1.game1.IsMainInstance
            && Game1.hasLoadedGame
        )
        {
            Game1.requestedMusicDirty = true;
            vanillaReselectPending = false;
        }
        return Result(true, "music.suppression.released");
    }

    public void Dispose()
    {
        if (disposed)
            return;

        var shouldReselectVanilla = suppressionRequested || vanillaReselectPending;
        suppressionRequested = false;
        vanillaReselectPending = false;
        try
        {
            MusicManager.SetSanityMusicSuppressed(false);
        }
        catch (Exception exception)
        {
            Report(
                "music.dawn-dusk.release-failed",
                $"Dawn/dusk release failed during adapter disposal ({exception.GetType().Name}: {exception.Message})."
            );
        }

        if (ReferenceEquals(activeAdapter, this))
            activeAdapter = null;
        if (harmony is not null && targetMethod is not null)
        {
            try
            {
                harmony.Unpatch(
                    targetMethod,
                    HarmonyPatchType.Prefix,
                    patchOwnerId
                );
            }
            catch (Exception exception)
            {
                Report(
                    "music.patch.unpatch-failed",
                    $"The exact game-music prefix could not be removed ({exception.GetType().Name}: {exception.Message})."
                );
            }
        }

        if (
            shouldReselectVanilla
            && Game1.game1 is not null
            && Game1.game1.IsMainInstance
            && Game1.hasLoadedGame
        )
        {
            // Disposal may follow Clear before another update tick. Preserve the same no-resume
            // release contract by dirtying vanilla's current request immediately.
            Game1.requestedMusicDirty = true;
        }
        disposed = true;
    }

    private SanityGameMusicPhysicalResult FailAppliedSuppression(
        string code,
        Exception exception
    )
    {
        suppressionRequested = false;
        vanillaReselectPending = true;
        TryReleaseProjectMusicAfterFailure();
        Report(
            code,
            $"The game-music adapter failed with {exception.GetType().Name}: {exception.Message}."
        );
        return Result(false, code);
    }

    private void TryReleaseProjectMusicAfterFailure()
    {
        try
        {
            MusicManager.SetSanityMusicSuppressed(false);
        }
        catch (Exception releaseException)
        {
            Report(
                "music.dawn-dusk.failure-release-failed",
                $"Dawn/dusk suppression cleanup failed with {releaseException.GetType().Name}: {releaseException.Message}."
            );
        }
    }

    private bool TryStopCurrentGameMusic(out string reason)
    {
        reason = "music.suppression.applied";
        if (Game1.game1 is null || !Game1.game1.IsMainInstance)
            return true;

        ICue? current = Game1.currentSong;
        if (current is null)
            return true;

        Exception? failure = null;
        try
        {
            current.Stop(AudioStopOptions.Immediate);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            current.Dispose();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }
        finally
        {
            // Never retain an interrupted cue for later resume. Vanilla will rebuild from its
            // current request state after suppression is released.
            Game1.currentSong = null;
        }

        if (failure is null)
            return true;

        reason = "music.output.stop-current-cue-failed";
        Report(
            reason,
            $"The current game cue could not be stopped cleanly ({failure.GetType().Name}: {failure.Message})."
        );
        return false;
    }

    private SanityGameMusicPhysicalResult Result(bool success, string reason)
    {
        return new SanityGameMusicPhysicalResult(
            success,
            SuppressionApplied,
            reason
        );
    }

    private void Report(string code, string reason)
    {
        diagnosticSink(new SanityAudioDiagnostic(null, code, reason));
    }

    private static bool BeforeGameUpdateMusic()
    {
        var adapter = activeAdapter;
        return adapter is null || adapter.disposed || !adapter.suppressionRequested;
    }

    private static bool IsOwnedPrefixInstalled(MethodInfo method, string ownerId)
    {
        var patchInfo = Harmony.GetPatchInfo(method);
        if (patchInfo is null)
            return false;
        foreach (var prefix in patchInfo.Prefixes)
        {
            if (string.Equals(prefix.owner, ownerId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
