using System;
using System.Globalization;
using DontStarve.Player.Stats.Hunger;
using DontStarve.Player.Stats.Sanity;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Audio.StatusFeedback;

/// <summary>
/// 状态反馈音效的 SMAPI 装配点。职责边界：
/// - 实现 Sanity/Hunger 的多人观察 seam（主机 delta hint + 客户端回放观察）；
/// - 订阅 Sanity/Hunger 主机结算事件，对本机玩家的 discrete 变化播音；
/// - 每刻差分观察本机玩家健康值并播音（overtime 写入方通过
///   <see cref="StatusFeedbackHealthSuppression"/> 抑制）；
/// - <see cref="SetEnabled"/> 只控制"预加载 + 播音接线"，不影响任何存档或结算数据；
///   多人 delta hint 的解析始终生效（hint 是协议事实，不属于本机开关）。
/// </summary>
internal sealed class StatusFeedbackCoordinator
    : ISanityStatusFeedbackSink,
        IHungerStatusFeedbackSink,
        IDisposable
{
    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly StatusFeedbackAudioService audio;
    private readonly StatusFeedbackHealthWatchStore healthWatches = new();

    private SanityChangeService? sanityChange;
    private HungerChangeService? hungerChange;
    private bool playbackEnabled;
    private bool playbackWired;
    private bool disposed;

    internal StatusFeedbackCoordinator(IModHelper helper, IMonitor monitor)
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        audio = new StatusFeedbackAudioService(helper, monitor);
    }

    /// <summary>
    /// StatManager.Initialize 完成后调用：只保存服务引用。播音订阅由
    /// <see cref="SetEnabled"/> 统一挂载/摘除，保证 GMCM 开关可以往复切换。
    /// </summary>
    internal void AttachStatServices(
        SanityChangeService sanityChangeService,
        HungerChangeService hungerChangeService
    )
    {
        sanityChange = sanityChangeService ?? throw new ArgumentNullException(nameof(sanityChangeService));
        hungerChange = hungerChangeService ?? throw new ArgumentNullException(nameof(hungerChangeService));
        if (playbackEnabled)
            WirePlaybackLocked();
    }

    /// <summary>
    /// GMCM 开关对应的运行时状态。true：预解码全部实例并接线播音；
    /// false：卸载实例并摘除全部播音订阅（结算数据与存档不受影响）。
    /// </summary>
    internal void SetEnabled(bool enabled)
    {
        playbackEnabled = enabled;
        if (enabled)
        {
            audio.LoadAll();
            // 抑制 seam 与差分状态表同时绑定/解绑：关闭期间不接受抑制令牌，
            // 重新开启也不会被旧令牌误吞第一条离散音效。
            StatusFeedbackHealthSuppression.Bind(healthWatches);
            WirePlaybackLocked();
        }
        else
        {
            UnwirePlaybackLocked();
            StatusFeedbackHealthSuppression.Bind(null);
            audio.UnloadAll();
        }
    }

    private void WirePlaybackLocked()
    {
        if (playbackWired || disposed)
            return;
        if (sanityChange is null || hungerChange is null)
            return;

        sanityChange.HostStateChanged += OnSanityHostSettlement;
        hungerChange.HostStateChanged += OnHungerHostSettlement;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        playbackWired = true;
    }

    private void UnwirePlaybackLocked()
    {
        if (!playbackWired)
            return;
        if (sanityChange is not null)
            sanityChange.HostStateChanged -= OnSanityHostSettlement;
        if (hungerChange is not null)
            hungerChange.HostStateChanged -= OnHungerHostSettlement;
        helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle -= OnReturnedToTitle;
        playbackWired = false;
    }

    // —— ISanityStatusFeedbackSink（多人协议 seam，独立于本机开关）——

    StatusFeedbackTriggerKind ISanityStatusFeedbackSink.ResolveDeltaHint(
        SanityChangeSource source
    )
    {
        return StatusFeedbackTriggerPolicy.Classify(source);
    }

    void ISanityStatusFeedbackSink.ObserveClientDelta(
        SanityClientDeltaObservation observation
    )
    {
        // 客户端防双响：只对本机玩家、且主机明确标记为 Discrete 的增量回放播音。
        // Hint 不是 Discrete（完整快照/continuous/unknown）一律静音。
        if (observation.Hint != StatusFeedbackTriggerKind.Discrete)
            return;
        if (!IsLocalFarmerKey(observation.PlayerKey))
            return;

        PlaySanity(observation.PreviousCurrent, observation.Current, "client-snapshot-replay");
    }

    // —— IHungerStatusFeedbackSink（多人协议 seam，独立于本机开关）——

    StatusFeedbackTriggerKind IHungerStatusFeedbackSink.ResolveDeltaHint(
        HungerChangeKind kind
    )
    {
        return StatusFeedbackTriggerPolicy.Classify(kind);
    }

    void IHungerStatusFeedbackSink.ObserveClientDelta(
        HungerClientDeltaObservation observation
    )
    {
        if (observation.Hint != StatusFeedbackTriggerKind.Discrete)
            return;
        if (!IsLocalFarmerKey(observation.PlayerKey))
            return;

        PlayHunger(observation.PreviousCurrent, observation.Current, "client-snapshot-replay");
    }

    // —— 主机结算播音（本机玩家 only；远程帮手由其客户端经 ObserveClientDelta 播）——

    private void OnSanityHostSettlement(SanityStateChanged change)
    {
        if (
            StatusFeedbackTriggerPolicy.Classify(change.Source)
            != StatusFeedbackTriggerKind.Discrete
        )
        {
            return;
        }
        if (!IsLocalFarmerKey(change.Snapshot.PlayerKey))
            return;

        PlaySanity(change.PreviousCurrent, change.Snapshot.Current, $"host:{change.Source}");
    }

    private void OnHungerHostSettlement(HungerSettlementObservation observation)
    {
        if (
            StatusFeedbackTriggerPolicy.Classify(observation.Kind)
            != StatusFeedbackTriggerKind.Discrete
        )
        {
            return;
        }
        if (!IsLocalFarmerKey(observation.Snapshot.PlayerKey))
            return;

        PlayHunger(
            observation.PreviousCurrent,
            observation.Snapshot.Current,
            $"host:{observation.Kind}"
        );
    }

    // —— 健康差分 watcher（原版管理、无 mod 结算服务，只能差分观察）——

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (disposed)
            return;
        var player = Game1.player;
        if (player is null || !Context.IsWorldReady)
        {
            // 世界外（标题/过场）不保留跨世界基线；重新进世界后首刻只建立基线。
            healthWatches.Clear();
            return;
        }

        var watch = healthWatches.GetOrCreate(player.UniqueMultiplayerID);
        var decision = watch.Observe(player.health);
        if (decision.Action != StatusFeedbackHealthWatchAction.Play || decision.Direction is null)
            return;

        PlayCue(
            StatusFeedbackSystem.Health,
            decision.Direction.Value,
            "health-diff-watcher"
        );
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        healthWatches.Clear();
    }

    // —— 播音落地 ——

    private void PlaySanity(double previous, double current, string reason)
    {
        var playback = StatusFeedbackPlaybackResolver.Resolve(
            StatusFeedbackSystem.Sanity,
            previous,
            current,
            reason
        );
        if (playback is null)
            return;
        PlayCue(playback.Value.System, playback.Value.Direction, playback.Value.Reason);
    }

    private void PlayHunger(double previous, double current, string reason)
    {
        var playback = StatusFeedbackPlaybackResolver.Resolve(
            StatusFeedbackSystem.Hunger,
            previous,
            current,
            reason
        );
        if (playback is null)
            return;
        PlayCue(playback.Value.System, playback.Value.Direction, playback.Value.Reason);
    }

    private void PlayCue(
        StatusFeedbackSystem system,
        StatusFeedbackDirection direction,
        string reason
    )
    {
        foreach (var cue in StatusFeedbackCueCatalog.Cues)
        {
            if (cue.System != system || cue.Direction != direction)
                continue;
            audio.Play(cue.Id);
            return;
        }
        // 清单覆盖三系×两向；缺条目属于代码清单错误，记录一次即可定位。
        monitor.Log(
            $"Status feedback cue missing for {system}/{direction} ({reason}).",
            LogLevel.Error
        );
    }

    private bool IsLocalFarmerKey(string playerKey)
    {
        // Sanity/Hunger 的 player key 都是 UniqueMultiplayerID 的 invariant 十进制串。
        if (
            !long.TryParse(
                playerKey,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var playerId
            )
        )
        {
            return false;
        }

        return IsMachineLocalFarmer(playerId);
    }

    /// <summary>
    /// 判断农民是否由"本机"控制（含分屏的其它本机屏幕）。SDV 1.6 的 Farmer 没有可用的
    /// isRemotePlayer 成员，因此用两条事实组合：
    /// 1) 活动屏幕的 Game1.player 直接命中（客户端自己 / 主机当前屏）；
    /// 2) 其余在线农民若不出现在 SMAPI 远端 peer 列表里，即为本机农民（主机分屏屏、
    ///    主机本体）。远程农场帮手必然对应一个 peer，从而被排除——这是防双响的关键。
    /// 仅在 discrete 分类通过后调用，频率为一次性事件级别，peer 列表分配可接受。
    /// </summary>
    private bool IsMachineLocalFarmer(long playerId)
    {
        if (Game1.player is { } activePlayer && activePlayer.UniqueMultiplayerID == playerId)
            return true;

        foreach (var farmer in Game1.getOnlineFarmers())
        {
            if (farmer.UniqueMultiplayerID != playerId)
                continue;

            foreach (var peer in helper.Multiplayer.GetConnectedPlayers())
            {
                if (peer.PlayerID == playerId)
                    return false;
            }
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        UnwirePlaybackLocked();
        StatusFeedbackHealthSuppression.Bind(null);
        healthWatches.Clear();
        audio.Dispose();
    }
}
