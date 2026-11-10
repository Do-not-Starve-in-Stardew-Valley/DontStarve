#nullable enable

using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.Visual;

/// <summary>
/// SMAPI adapter for the low-sanity idle panic loop. When the local player's Sanity ratio is
/// below 50% and they have been idle for one second, the loop alternates two vanilla reactions:
/// the passout collapse motion with an Angry emote bubble, then the vanilla "surprised" startle
/// animation (jump + jitter + Surprised bubble). Any real input immediately interrupts the loop
/// and returns control; the genuine vanilla 2:00/exhaustion passout always takes priority.
/// </summary>
internal sealed class SanityIdlePanicLoopService : IDisposable
{
    private const string AngryEmoteName = "angry";
    private const string SurprisedEmoteName = "surprised";

    // Farmer.EMOTES contract (1.6.15): "angry" is icon 12 with no animation frames; "surprised"
    // is icon 16 whose frame-94 action performs the vanilla startle (batScreech +
    // jumpWithoutSound(4) + jitter). A mismatch disables this loop fail-closed.
    private const int AngryEmoteIconIndex = 12;
    private const int SurprisedEmoteIconIndex = 16;
    private const int VanillaPassOutAnimationToken =
        SanityIdlePresentationContract.VanillaPassOutAnimationToken;
    private const int MaximumLoggedDiagnostics = 32;

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly SanitySystemLifecycleCoordinator lifecycle;
    private readonly SanityIdleDetector idleDetector = new();
    private readonly SanityIdlePanicLoopRules rules = new();
    private readonly HashSet<string> loggedDiagnostics = new(StringComparer.Ordinal);
    private bool vanillaEmotesProbed;
    private bool vanillaEmotesAvailable;
    private bool disposed;

    internal SanityIdlePanicLoopService(
        IModHelper helper,
        IMonitor monitor,
        SanitySystemLifecycleCoordinator lifecycle
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));

        lifecycle.StateEventPublished += OnStateEventPublished;
        lifecycle.WorldBoundaryStarting += OnWorldBoundaryStarting;
        lifecycle.SessionClearing += OnSessionClearing;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
        helper.Events.Input.ButtonPressed += OnButtonPressed;
        helper.Events.Input.MouseWheelScrolled += OnMouseWheelScrolled;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        lifecycle.StateEventPublished -= OnStateEventPublished;
        lifecycle.WorldBoundaryStarting -= OnWorldBoundaryStarting;
        lifecycle.SessionClearing -= OnSessionClearing;
        helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        helper.Events.GameLoop.DayEnding -= OnDayEnding;
        helper.Events.Input.ButtonPressed -= OnButtonPressed;
        helper.Events.Input.MouseWheelScrolled -= OnMouseWheelScrolled;
        CancelIfActive("panic.interrupted.disposed");
    }

    private void OnStateEventPublished(SanityStateEvent stateEvent)
    {
        if (disposed)
            return;

        switch (stateEvent.Kind)
        {
            case SanityStateEventKind.SystemDisabled:
            case SanityStateEventKind.SystemEnabled:
            case SanityStateEventKind.OwnerInvalidated:
                // 本循环只服务本地玩家；系统或 owner 边界一律按打断处理，避免失联状态
                // 继续占用精灵。
                CancelIfActive("panic.interrupted.system-boundary");
                break;
        }
    }

    private void OnWorldBoundaryStarting(SanityWorldBoundary boundary)
    {
        if (!disposed)
            CancelIfActive("panic.interrupted.world-boundary");
    }

    private void OnSessionClearing(SanitySessionBoundary boundary)
    {
        if (!disposed)
            CancelIfActive("panic.interrupted.session-clearing");
    }

    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        if (!disposed)
            CancelIfActive("panic.interrupted.day-ending");
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (disposed)
            return;

        // 任何真实输入都视为玩家接管操作：循环中立刻打断，等待中重置 1 秒计时。
        CancelIfActive("panic.interrupted.real-input");
    }

    private void OnMouseWheelScrolled(object? sender, MouseWheelScrolledEventArgs e)
    {
        if (disposed)
            return;

        CancelIfActive("panic.interrupted.real-input");
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (disposed)
            return;

        if (!lifecycle.IsEnabled || !Context.IsWorldReady)
        {
            HardCancel("panic.system-unavailable");
            return;
        }

        var player = Game1.player;
        if (player is null || !player.IsLocalPlayer || player.FarmerSprite is null)
        {
            HardCancel("panic.local-player-unavailable");
            return;
        }

        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(player.UniqueMultiplayerID);
        if (
            !SanityPlayerKey.IsCanonical(playerKey)
            || !lifecycle.TryGetTierState(playerKey, out var tier)
            || tier is null
            || !tier.IsAvailable
            || tier.Maximum <= 0d
        )
        {
            HardCancel("panic.tier-state-unavailable");
            return;
        }

        // 真实原版昏睡（凌晨 2 点/力竭）接管了 293 token。那条流程会结束当天，必须让位，
        // 也不能在下一个相位边界用 completelyStop 打断它。
        if (
            rules.IsActive
            && player.FarmerSprite.currentSingleAnimation == VanillaPassOutAnimationToken
        )
        {
            rules.Cancel();
            idleDetector.Reset("panic.interrupted.vanilla-passout");
            return;
        }

        var elapsed = Game1.currentGameTime?.ElapsedGameTime ?? TimeSpan.Zero;
        var idle = idleDetector.Observe(elapsed, BuildObservation(player, rules.IsActive));

        if (!rules.IsActive)
        {
            ObserveIdleStart(player, idle, tier.Current, tier.Maximum);
            return;
        }

        if (!idle.ThresholdReached)
        {
            CancelAndRecover(player, idle.Reason);
            return;
        }
        if (!SanityIdlePanicLoopRules.IsSanityEligible(tier.Current, tier.Maximum))
        {
            CancelAndRecover(player, "panic.interrupted.sanity-recovered");
            return;
        }

        switch (rules.Tick(elapsed))
        {
            case SanityIdlePanicLoopCommand.BeginFaint:
                BeginFaint(player);
                break;
            case SanityIdlePanicLoopCommand.BeginStartled:
                BeginStartled(player);
                break;
        }
    }

    private void ObserveIdleStart(
        Farmer player,
        SanityIdleSnapshot idle,
        double currentSanity,
        double maximumSanity
    )
    {
        if (!idle.ThresholdReached)
            return;
        if (!SanityIdlePanicLoopRules.IsSanityEligible(currentSanity, maximumSanity))
        {
            idleDetector.Reset("panic.sanity-ineligible");
            return;
        }
        if (!TryVerifyVanillaEmotes() || IsStartBlocked(player))
        {
            idleDetector.Reset("panic.start-blocked");
            return;
        }

        rules.Start();
        BeginFaint(player);
    }

    private static bool IsStartBlocked(Farmer player)
    {
        // 原版力竭昏睡前会先下马/起身/停止进食；本循环不复刻这些状态迁移，等姿势结束
        // 再开始，避免倒地帧覆盖坐骑/座位姿态。freezePause 期间输入被引擎锁定（含原版
        // 2 点/力竭昏睡的 7000ms 冻结），同样不开始。
        return player.isRidingHorse()
            || player.IsSitting()
            || player.isEating
            || player.swimming.Value
            || player.usingSlingshot
            || player.freezePause > 0;
    }

    private static void BeginFaint(Farmer player)
    {
        // performPassOut 原版三步：faceDirection(2) → completelyStopAnimatingOrDoingAction →
        // animateOnce(293)。这里用同一帧序复刻倒地动作，但刻意不挂原版第 5 帧链式的
        // doSleepEmote/passOutFromTired 帧动作——它们会真的触发上床结算并结束当天。
        player.faceDirection(2);
        player.completelyStopAnimatingOrDoingAction();
        player.FarmerSprite.animateOnce(BuildFaintFrames());
        player.doEmote(AngryEmoteIconIndex);
    }

    private static void BeginStartled(Farmer player)
    {
        // performPlayerEmote("surprised") 一次调用复刻原版受惊链：94 帧受惊姿势 +
        // batScreech + jumpWithoutSound(4) + 抖动 + Surprised(16) 气泡。先停掉昏迷动画，
        // 否则 CanEmote 的 PauseForSingleAnimation 门会拒绝表演。
        player.completelyStopAnimatingOrDoingAction();
        player.performPlayerEmote(SurprisedEmoteName);
    }

    private static FarmerSprite.AnimationFrame[] BuildFaintFrames()
    {
        // 每次新建帧数组：AnimationFrame 实例可变（armOffset/flip 等），不能跨次复用。
        var frameIds = SanityIdlePanicLoopRules.FaintFrameIds;
        var durations = SanityIdlePanicLoopRules.FaintFrameMilliseconds;
        var frames = new FarmerSprite.AnimationFrame[durations.Count];
        for (var index = 0; index < durations.Count; index++)
            frames[index] = new FarmerSprite.AnimationFrame(frameIds[index], durations[index]);
        return frames;
    }

    private static SanityIdleObservation BuildObservation(
        Farmer player,
        bool ownLoopActive
    )
    {
        var sprite = player.FarmerSprite;
        var isBusy = ownLoopActive
            ? IsInterruptedOutsidePanicLoop(player)
            : player.IsBusyDoingSomething();
        var interactionWorldReady =
            Context.IsWorldReady
            && !Game1.paused
            && (Game1.game1 is null || Game1.game1.IsActiveNoOverlay);
        return new SanityIdleObservation(
            interactionWorldReady,
            player.IsLocalPlayer,
            player.CanMove,
            isBusy,
            player.isMoving(),
            player.movementDirections.Count > 0,
            player.UsingTool,
            player.temporarilyInvincible,
            sprite.PauseForSingleAnimation,
            sprite.IsPlayingBasicAnimation(player.FacingDirection, player.IsCarrying()),
            Game1.activeClickableMenu is not null,
            Game1.CurrentEvent is not null || Game1.eventUp,
            Game1.isWarping,
            ownLoopActive
        );
    }

    private static bool IsInterruptedOutsidePanicLoop(Farmer player)
    {
        // Farmer.IsBusyDoingSomething 会把我们自己的 single-animation 也判成 busy；循环
        // 占用精灵期间只保留这些硬中断，其余边界由现有 idle 观测字段判断。freezePause
        // 覆盖原版 2 点/力竭昏睡的强制冻结：循环立刻让位，保证原版重试时精灵是干净的。
        return Game1.eventUp
            || Game1.paused
            || (Game1.game1 is not null && !Game1.game1.IsActiveNoOverlay)
            || Game1.fadeToBlack
            || Game1.currentMinigame is not null
            || Game1.activeClickableMenu is not null
            || Game1.isWarping
            || Game1.killScreen
            || player.UsingTool
            || player.freezePause > 0
            || !player.CanMove;
    }

    private void CancelIfActive(string reason)
    {
        if (rules.IsActive)
        {
            var player = Game1.player;
            if (player is not null && player.IsLocalPlayer)
            {
                CancelAndRecover(player, reason);
                return;
            }
        }
        HardCancel(reason);
    }

    private void CancelAndRecover(Farmer player, string reason)
    {
        rules.Cancel();
        idleDetector.Reset(reason);
        try
        {
            // 立即清掉昏迷/受惊动画并交还操作；completelyStop 会把 CanMove 恢复为非事件状态。
            player.completelyStopAnimatingOrDoingAction();
        }
        catch (Exception exception)
        {
            LogOnce(
                "panic.recover-failed",
                $"Low-sanity panic loop recovery failed ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Error
            );
        }
    }

    private void HardCancel(string reason)
    {
        rules.Cancel();
        idleDetector.Reset(reason);
    }

    private bool TryVerifyVanillaEmotes()
    {
        if (vanillaEmotesProbed)
            return vanillaEmotesAvailable;

        vanillaEmotesProbed = true;
        var angryVerified = false;
        var surprisedVerified = false;
        try
        {
            foreach (var emote in Farmer.EMOTES)
            {
                if (emote is null)
                    continue;
                if (
                    !angryVerified
                    && string.Equals(emote.emoteString, AngryEmoteName, StringComparison.Ordinal)
                )
                {
                    angryVerified = emote.emoteIconIndex == AngryEmoteIconIndex;
                }
                else if (
                    !surprisedVerified
                    && string.Equals(
                        emote.emoteString,
                        SurprisedEmoteName,
                        StringComparison.Ordinal
                    )
                )
                {
                    surprisedVerified =
                        emote.emoteIconIndex == SurprisedEmoteIconIndex
                        && emote.animationFrames is { Length: > 0 };
                }
            }
            vanillaEmotesAvailable = angryVerified && surprisedVerified;
        }
        catch (Exception exception)
        {
            vanillaEmotesAvailable = false;
            LogOnce(
                "panic.emote-probe-failed",
                $"Low-sanity panic loop could not read the vanilla emote table ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Warn
            );
        }

        if (!vanillaEmotesAvailable)
        {
            LogOnce(
                "panic.vanilla-emotes-mismatch",
                "Low-sanity panic loop disabled fail-closed: the vanilla angry/surprised emote contract did not match.",
                LogLevel.Warn
            );
        }
        return vanillaEmotesAvailable;
    }

    private void LogOnce(string code, string message, LogLevel level)
    {
        if (loggedDiagnostics.Count >= MaximumLoggedDiagnostics || !loggedDiagnostics.Add(code))
            return;
        monitor.Log(message, level);
    }
}
