#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.Audio;

internal static class SanityGameMusicCapabilityGate
{
    internal const string ExpectedGameVersion = "1.6.15";
    internal const string ExpectedTargetSignature =
        "StardewValley.Game1.updateMusic():System.Void";

    internal static string? ValidateVersion(string? actualGameVersion)
    {
        return string.Equals(
            actualGameVersion,
            ExpectedGameVersion,
            StringComparison.Ordinal
        )
            ? null
            : "music.patch.game-version-mismatch";
    }

    internal static string? ValidateTarget(
        bool methodFound,
        bool isStatic,
        bool returnsVoid,
        int parameterCount
    )
    {
        return methodFound
            && isStatic
            && returnsVoid
            && parameterCount == 0
                ? null
                : "music.patch.target-signature-mismatch";
    }
}

internal enum SanityGameMusicCapabilityStatus
{
    Available,
    Unavailable,
}

internal enum SanityGameMusicDecisionKind
{
    NoEligibleOwner,
    WinnerOutsideMusicSuppression,
    EventSuspended,
    ProcessPaused,
    MiniJukeboxExempt,
    Suppressed,
    AdapterUnavailable,
    PhysicalFailure,
    IndependentAudioOutOfScope,
    Disposed,
}

internal readonly record struct SanityGameMusicCapability(
    string Capability,
    SanityGameMusicCapabilityStatus Status,
    string Reason,
    string Fallback,
    string PatchOwnerId,
    string TargetGameVersion,
    string TargetSignature,
    bool PatchInstalled,
    bool MiniJukeboxIsAlwaysExempt,
    bool IslandIsBlanketExempt,
    bool ControlsIndependentAudio,
    bool OwnsDawnDuskLifecycle,
    bool RestoresInterruptedTrack,
    string ReleasePolicy
)
{
    internal static SanityGameMusicCapability Available(
        string patchOwnerId,
        string targetGameVersion,
        string targetSignature
    ) => new(
        "sanity.game-music-suppression",
        SanityGameMusicCapabilityStatus.Available,
        "music.suppression.adapter-installed",
        "fail-closed-version-or-signature-mismatch",
        patchOwnerId,
        targetGameVersion,
        targetSignature,
        PatchInstalled: true,
        MiniJukeboxIsAlwaysExempt: true,
        IslandIsBlanketExempt: false,
        ControlsIndependentAudio: false,
        OwnsDawnDuskLifecycle: true,
        RestoresInterruptedTrack: false,
        "original-reselect-current-state"
    );

    internal static SanityGameMusicCapability Unavailable(
        string patchOwnerId,
        string targetGameVersion,
        string targetSignature,
        string reason
    ) => new(
        "sanity.game-music-suppression",
        SanityGameMusicCapabilityStatus.Unavailable,
        reason,
        "fail-closed-version-or-signature-mismatch",
        patchOwnerId,
        targetGameVersion,
        targetSignature,
        PatchInstalled: false,
        MiniJukeboxIsAlwaysExempt: true,
        IslandIsBlanketExempt: false,
        ControlsIndependentAudio: false,
        OwnsDawnDuskLifecycle: true,
        RestoresInterruptedTrack: false,
        "original-reselect-current-state"
    );
}

internal readonly record struct SanityGameMusicPhysicalResult(
    bool Success,
    bool SuppressionApplied,
    string Reason
);

/// <summary>
/// Version-gated process output. It owns only the Sanity prefix and physical suppression state;
/// owner selection remains exclusively in <see cref="SanityProcessAudioCoordinator"/>.
/// </summary>
internal interface ISanityGameMusicPhysicalAdapter : IDisposable
{
    SanityGameMusicCapability Capability { get; }

    bool SuppressionApplied { get; }

    SanityGameMusicPhysicalResult SetSuppression(bool suppress);

    SanityGameMusicPhysicalResult Tick();
}

internal readonly record struct SanityGameMusicDecision(
    SanityGameMusicDecisionKind Kind,
    string Reason,
    bool WantsSuppression,
    bool SuppressionApplied,
    bool MiniJukeboxPlaying,
    bool IslandContext,
    SanityAudioOwnerClaim? Winner
);

internal sealed class SanityGameMusicSnapshot
{
    internal SanityGameMusicSnapshot(
        long generation,
        long processAudioGeneration,
        SanityGameMusicDecision decision,
        bool disposed,
        SanityGameMusicCapability capability
    )
    {
        Generation = generation;
        ProcessAudioGeneration = processAudioGeneration;
        Decision = decision;
        Disposed = disposed;
        Capability = capability;
    }

    internal long Generation { get; }

    internal long ProcessAudioGeneration { get; }

    internal SanityGameMusicDecision Decision { get; }

    internal SanityAudioOwnerClaim? Winner => Decision.Winner;

    internal bool WantsSuppression => Decision.WantsSuppression;

    internal bool SuppressionApplied => Decision.SuppressionApplied;

    internal bool Disposed { get; }

    internal SanityGameMusicCapability Capability { get; }
}

/// <summary>
/// Process music policy consuming the allocation-free winner state from the one audio
/// coordinator. It has no claim dictionary and no comparer of its own.
/// </summary>
internal sealed class SanityGameMusicCoordinator : IDisposable
{
    private readonly ISanityGameMusicPhysicalAdapter adapter;
    private SanityGameMusicInput lastInput;
    private SanityGameMusicDecision lastDecision;
    private bool hasInput;
    private bool disposed;
    private long generation;

    internal SanityGameMusicCoordinator(ISanityGameMusicPhysicalAdapter adapter)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        lastDecision = Decision(
            SanityGameMusicDecisionKind.NoEligibleOwner,
            "music.owner.none",
            wantsSuppression: false,
            suppressionApplied: false,
            miniJukeboxPlaying: false,
            islandContext: false,
            winner: null
        );
    }

    internal SanityGameMusicDecision Reconcile(
        SanityProcessAudioMusicState processAudio,
        bool miniJukeboxPlaying,
        bool islandContext
    )
    {
        if (disposed)
        {
            return Decision(
                SanityGameMusicDecisionKind.Disposed,
                "music.coordinator.disposed",
                wantsSuppression: false,
                suppressionApplied: false,
                miniJukeboxPlaying,
                islandContext,
                processAudio.Winner
            );
        }

        var input = new SanityGameMusicInput(
            processAudio,
            miniJukeboxPlaying,
            islandContext
        );
        if (
            hasInput
            && input == lastInput
            && lastDecision.SuppressionApplied == adapter.SuppressionApplied
        )
        {
            return lastDecision;
        }

        hasInput = true;
        lastInput = input;
        var decision = SelectPolicy(processAudio, miniJukeboxPlaying, islandContext);
        if (
            decision.WantsSuppression
            && adapter.Capability.Status == SanityGameMusicCapabilityStatus.Unavailable
        )
        {
            return UpdateDecision(
                decision with
                {
                    Kind = SanityGameMusicDecisionKind.AdapterUnavailable,
                    Reason = adapter.Capability.Reason,
                    SuppressionApplied = false,
                }
            );
        }

        var physical = adapter.SetSuppression(decision.WantsSuppression);
        if (!physical.Success)
        {
            return UpdateDecision(
                decision with
                {
                    Kind = SanityGameMusicDecisionKind.PhysicalFailure,
                    Reason = physical.Reason,
                    SuppressionApplied = physical.SuppressionApplied,
                }
            );
        }

        if (decision.WantsSuppression)
        {
            decision = decision with
            {
                Kind = SanityGameMusicDecisionKind.Suppressed,
                Reason = physical.Reason,
                SuppressionApplied = physical.SuppressionApplied,
            };
        }
        else
        {
            decision = decision with
            {
                SuppressionApplied = physical.SuppressionApplied,
            };
        }
        return UpdateDecision(decision);
    }

    internal SanityGameMusicDecision Tick()
    {
        if (disposed)
            return lastDecision;

        var physical = adapter.Tick();
        if (!physical.Success)
        {
            return UpdateDecision(
                lastDecision with
                {
                    Kind = SanityGameMusicDecisionKind.PhysicalFailure,
                    Reason = physical.Reason,
                    SuppressionApplied = physical.SuppressionApplied,
                }
            );
        }
        if (lastDecision.SuppressionApplied != physical.SuppressionApplied)
        {
            return UpdateDecision(
                lastDecision with { SuppressionApplied = physical.SuppressionApplied }
            );
        }
        return lastDecision;
    }

    internal SanityGameMusicDecision EvaluateIndependentAudio()
    {
        return Decision(
            SanityGameMusicDecisionKind.IndependentAudioOutOfScope,
            "music.independent-audio.out-of-scope",
            wantsSuppression: false,
            suppressionApplied: adapter.SuppressionApplied,
            miniJukeboxPlaying: false,
            islandContext: false,
            winner: lastDecision.Winner
        );
    }

    internal SanityGameMusicSnapshot Snapshot()
    {
        return new SanityGameMusicSnapshot(
            generation,
            hasInput ? lastInput.ProcessAudio.Generation : 0,
            lastDecision,
            disposed,
            adapter.Capability
        );
    }

    internal void Clear()
    {
        if (disposed)
            return;

        hasInput = false;
        var physical = adapter.SetSuppression(false);
        UpdateDecision(
            Decision(
                physical.Success
                    ? SanityGameMusicDecisionKind.NoEligibleOwner
                    : SanityGameMusicDecisionKind.PhysicalFailure,
                physical.Reason,
                wantsSuppression: false,
                suppressionApplied: physical.SuppressionApplied,
                miniJukeboxPlaying: false,
                islandContext: false,
                winner: null
            )
        );
    }

    public void Dispose()
    {
        if (disposed)
            return;

        Clear();
        adapter.Dispose();
        disposed = true;
        generation++;
        lastDecision = Decision(
            SanityGameMusicDecisionKind.Disposed,
            "music.coordinator.disposed",
            wantsSuppression: false,
            suppressionApplied: false,
            miniJukeboxPlaying: false,
            islandContext: false,
            winner: null
        );
    }

    private SanityGameMusicDecision SelectPolicy(
        SanityProcessAudioMusicState processAudio,
        bool miniJukeboxPlaying,
        bool islandContext
    )
    {
        if (processAudio.Winner is null)
        {
            return Decision(
                SanityGameMusicDecisionKind.NoEligibleOwner,
                "music.owner.none",
                wantsSuppression: false,
                adapter.SuppressionApplied,
                miniJukeboxPlaying,
                islandContext,
                winner: null
            );
        }
        if (!processAudio.Winner.Value.MusicSuppressionRequested)
        {
            return Decision(
                SanityGameMusicDecisionKind.WinnerOutsideMusicSuppression,
                "music.owner.outside-suppression-tier",
                wantsSuppression: false,
                adapter.SuppressionApplied,
                miniJukeboxPlaying,
                islandContext,
                processAudio.Winner
            );
        }
        // DIAG-20260806: 事件覆盖期间放行音乐——星露谷事件有专属音乐（剧情/过场），
        // 不应被低理智压制（主策划确认：进事件后不压事件音乐是正确行为）。
        // 事件期间 claim 保持不变（见 SanitySmapiAudioService.SubmitSnapshot 的
        // IsEventCoverageActive 保留逻辑），故事件结束后 EventSuspended=false 立即恢复
        // 正常决策（<=50% 的独立 music request 即压制），原版音乐不会趁机响起。
        if (processAudio.EventSuspended)
        {
            return Decision(
                SanityGameMusicDecisionKind.EventSuspended,
                "music.owner.event-suspended",
                wantsSuppression: false,
                adapter.SuppressionApplied,
                miniJukeboxPlaying,
                islandContext,
                processAudio.Winner
            );
        }
        // DIAG-20260806: 只要 <=50% 的 music request 激活，就禁用原版音乐（不影响音效）。
        // 窗口失焦（ProcessPaused）不阻断抑制——失焦时原版音乐同样不允许响起。
        // 点唱机豁免保留（玩家主动播放）。
        if (miniJukeboxPlaying)
        {
            return Decision(
                SanityGameMusicDecisionKind.MiniJukeboxExempt,
                "music.jukebox.exempt",
                wantsSuppression: false,
                adapter.SuppressionApplied,
                miniJukeboxPlaying,
                islandContext,
                processAudio.Winner
            );
        }

        return Decision(
            SanityGameMusicDecisionKind.Suppressed,
            "music.suppression.requested",
            wantsSuppression: true,
            adapter.SuppressionApplied,
            miniJukeboxPlaying,
            islandContext,
            processAudio.Winner
        );
    }

    private SanityGameMusicDecision UpdateDecision(SanityGameMusicDecision decision)
    {
        if (decision != lastDecision)
        {
            lastDecision = decision;
            generation++;
        }
        return lastDecision;
    }

    private static SanityGameMusicDecision Decision(
        SanityGameMusicDecisionKind kind,
        string reason,
        bool wantsSuppression,
        bool suppressionApplied,
        bool miniJukeboxPlaying,
        bool islandContext,
        SanityAudioOwnerClaim? winner
    ) => new(
        kind,
        reason,
        wantsSuppression,
        suppressionApplied,
        miniJukeboxPlaying,
        islandContext,
        winner
    );

    private readonly record struct SanityGameMusicInput(
        SanityProcessAudioMusicState ProcessAudio,
        bool MiniJukeboxPlaying,
        bool IslandContext
    );
}
