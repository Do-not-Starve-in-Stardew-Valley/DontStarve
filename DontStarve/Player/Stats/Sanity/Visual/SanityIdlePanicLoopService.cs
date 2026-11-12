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
/// animation (shock pose + screech + visible hop + jitter + Surprised bubble). Semantics:
/// operations that change the character's animation state interrupt (moving/tool/eating/warp/
/// riding/sitting/swimming/events); menus, game pause and window focus SUSPEND the loop in
/// place and resume afterwards; a mouse click pre-releases the sprite before the vanilla input
/// pass so the very first click eats/swings (the vanilla right-click eat gate requires
/// !PauseForSingleAnimation). The genuine vanilla 2:00/exhaustion passout always takes priority.
/// </summary>
internal sealed class SanityIdlePanicLoopService : IDisposable
{
    private const string AngryEmoteName = "angry";
    private const string SurprisedEmoteName = "surprised";
    private const string LogPrefix = "[sanity-panic-loop]";

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
    private readonly SanityIdlePanicLoopRules rules = new();
    private readonly HashSet<string> loggedDiagnostics = new(StringComparer.Ordinal);
    private bool vanillaEmotesProbed;
    private bool vanillaEmotesAvailable;
    private bool sessionDisabled;
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
        // 仅订阅鼠标点击用于"抢跑让位"（见 OnButtonPressed）；键盘/滚轮不订阅——打开菜单的
        // 按键必须保持"挂起-恢复"语义，滚轮不改变角色动画状态。
        helper.Events.Input.ButtonPressed += OnButtonPressed;
        monitor.Log($"{LogPrefix} service ready.", LogLevel.Info);
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
        if (disposed || sessionDisabled || !rules.IsActive)
            return;
        // 只抢跑鼠标点击：键盘按键（打开背包等）不在此处让位，菜单挂起-恢复语义得以保留；
        // 挂起期间的点击属于界面点击，同样忽略。
        if (e.Button is not (SButton.MouseLeft or SButton.MouseRight))
            return;
        if (IsGameInteractionSuspended())
            return;
        var player = Game1.player;
        if (player is null || !player.IsLocalPlayer)
            return;
        // 原版右键进食入口（Game1 输入轮询）明确要求 !FarmerSprite.PauseForSingleAnimation，
        // 工具/武器路径无此门——这正是"能挥刀、不能吃东西"的根因。SMAPI 按键事件先于原版
        // 同帧输入处理：此刻让出精灵，原版本帧看到的已是干净角色，点击正常生效。此刻
        // UsingTool/isEating 尚未置位，completelyStop 只会清掉自有倒地/受惊动画。
        CancelAndRecover(player, "panic.interrupted.mouse-click");
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (disposed || sessionDisabled)
            return;

        try
        {
            TickCore();
        }
        catch (Exception exception)
        {
            // 表现层绝不能反复抛错拖垮游戏：首次异常记录完整堆栈并本会话自禁用。
            sessionDisabled = true;
            HardCancel("panic.crashed");
            monitor.Log(
                $"{LogPrefix} crashed and disabled for this session. {exception}",
                LogLevel.Error
            );
        }
    }

    private void TickCore()
    {
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

        // 挂起：菜单（背包/设置/对话）、游戏暂停、窗口失焦期间冻结相位机与待机计时——
        // 不取消、不清理、不推进，解除后原地恢复（用户口径：暂停后循环也暂停）。
        if (IsGameInteractionSuspended())
            return;

        var playerKey = SanityPlayerKey.FromUniqueMultiplayerId(player.UniqueMultiplayerID);
        SanityTierOwnerStateSnapshot? tier = null;
        var playerKeyCanonical = SanityPlayerKey.IsCanonical(playerKey);
        var tierRegistered = playerKeyCanonical
            && lifecycle.TryGetTierState(playerKey, out tier);
        if (
            !tierRegistered
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
            rules.ResetIdle();
            LogOnce(
                "panic.vanilla-passout-standby",
                $"{LogPrefix} vanilla passout took over the sprite; loop stood down.",
                LogLevel.Info
            );
            return;
        }

        var elapsed = Game1.currentGameTime?.ElapsedGameTime ?? TimeSpan.Zero;
        var interruption = DescribePanicInterruption(player);
        rules.ObserveIdle(elapsed, interrupted: interruption is not null);

        if (!rules.IsActive)
        {
            ObserveIdleStart(player, interruption, tier.Current, tier.Maximum);
            return;
        }

        if (interruption is not null)
        {
            CancelAndRecover(player, interruption);
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
                monitor.Log(
                    $"{LogPrefix} phase=faint (sanity={tier.Current:0.#}/{tier.Maximum:0.#}).",
                    LogLevel.Info
                );
                break;
            case SanityIdlePanicLoopCommand.BeginStartled:
                BeginStartled(player);
                monitor.Log(
                    $"{LogPrefix} phase=startled (sanity={tier.Current:0.#}/{tier.Maximum:0.#}).",
                    LogLevel.Info
                );
                break;
        }
    }

    private void ObserveIdleStart(
        Farmer player,
        string? interruption,
        double currentSanity,
        double maximumSanity
    )
    {
        if (interruption is not null)
            return;
        if (!rules.IdleThresholdReached)
            return;
        if (!SanityIdlePanicLoopRules.IsSanityEligible(currentSanity, maximumSanity))
        {
            rules.ResetIdle();
            return;
        }
        if (!TryVerifyVanillaEmotes())
        {
            // 原版表情契约不匹配时 fail-closed（探测内部已记 Warn），不进入循环。
            rules.ResetIdle();
            return;
        }

        rules.Start();
        BeginFaint(player);
        monitor.Log(
            $"{LogPrefix} started (sanity={currentSanity:0.#}/{maximumSanity:0.#}).",
            LogLevel.Info
        );
    }

    /// <summary>
    /// 惊恐循环专用的"操作中"判定。按用户口径只保留会改变角色动画状态的操作信号：
    /// 移动、使用工具/弹弓、进食、传送与画面过渡、事件/小游戏、骑马、坐家具、游泳、
    /// 引擎控制锁（freezePause/CanMove）。菜单开关、窗口失焦走"挂起"（见
    /// IsGameInteractionSuspended），不在此打断；受击无敌与原始按键输入同样不算操作。
    /// 基础动画/foreign single-animation 姿势不在判定内：原版 IsPlayingBasicAnimation 的
    /// 静立判定要求呼吸帧，不能当空闲信号；循环是接管式表现，启动时自己 completelyStop
    /// 精灵。
    /// </summary>
    /// <summary>
    /// 挂起判定：存在可交互菜单（背包/设置/确认对话）、游戏暂停或窗口失焦时，惊恐循环整体
    /// 冻结——相位与待机计时都不推进，解除后原地恢复。与打断不同：打断会取消循环并重置
    /// 计时，挂起只冻结现场。
    /// </summary>
    private static bool IsGameInteractionSuspended()
    {
        return Game1.activeClickableMenu is not null
            || Game1.paused
            || (Game1.game1 is not null && !Game1.game1.IsActiveNoOverlay);
    }

    private static string? DescribePanicInterruption(Farmer player)
    {
        if (!Context.IsWorldReady)
            return "world-not-ready";
        if (Game1.eventUp || Game1.CurrentEvent is not null || Game1.farmEvent is not null)
            return "event-active";
        if (Game1.currentMinigame is not null)
            return "minigame";
        if (Game1.isWarping)
            return "warping";
        if (Game1.fadeToBlack)
            return "fade-to-black";
        if (Game1.killScreen)
            return "kill-screen";
        if (player.UsingTool)
            return SanityIdlePanicInterruptionPolicy.UsingToolReason;
        if (player.usingSlingshot)
            return SanityIdlePanicInterruptionPolicy.UsingSlingshotReason;
        if (player.isEating)
            return SanityIdlePanicInterruptionPolicy.EatingReason;
        if (player.freezePause > 0)
            return $"freezePause={player.freezePause}";
        if (!player.CanMove)
            return "cannot-move";
        if (player.isMoving() || player.movementDirections.Count > 0)
            return "moving";
        if (player.isRidingHorse())
            return "riding-horse";
        if (player.IsSitting())
            return "sitting";
        if (player.swimming.Value)
            return "swimming";
        return null;
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
        // batScreech + 帧首跳跃 + 抖动 + Surprised(16) 气泡。先停掉昏迷动画，否则 CanEmote
        // 的 PauseForSingleAnimation 门会拒绝表演。
        player.completelyStopAnimatingOrDoingAction();
        player.performPlayerEmote(SurprisedEmoteName);
        // 实机反馈：原版惊讶帧动作自带的是 4f 微跳（约 10px/0.15s），肉眼几乎不可见。
        // 这里按 Character.jumpWithoutSound 的原版默认力度 8f（约半格、0.6s）显式补跳，
        // 让"跳起惊吓"真正可读；后调用覆盖帧动作的 4f，jumpWithoutSound 本身无守卫。
        player.jumpWithoutSound(8f);
        player.jitterStrength = 1f;
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
        rules.ResetIdle();
        if (SanityIdlePanicInterruptionPolicy.ShouldReleasePlayerAnimation(reason))
        {
            try
            {
                // 没有原版动作接管精灵时才清理自有动画；移动/边界/理智恢复必须立即交还控制。
                player.completelyStopAnimatingOrDoingAction();
            }
            catch (Exception exception)
            {
                LogOnce(
                    "panic.recover-failed",
                    $"{LogPrefix} recovery failed ({exception.GetType().Name}: {exception.Message}).",
                    LogLevel.Error
                );
            }
        }
        monitor.Log($"{LogPrefix} interrupted ({reason}).", LogLevel.Info);
    }

    private void HardCancel(string reason)
    {
        rules.Cancel();
        rules.ResetIdle();
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
                $"{LogPrefix} could not read the vanilla emote table ({exception.GetType().Name}: {exception.Message}).",
                LogLevel.Warn
            );
        }

        if (!vanillaEmotesAvailable)
        {
            LogOnce(
                "panic.vanilla-emotes-mismatch",
                $"{LogPrefix} disabled fail-closed: the vanilla angry/surprised emote contract did not match.",
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
