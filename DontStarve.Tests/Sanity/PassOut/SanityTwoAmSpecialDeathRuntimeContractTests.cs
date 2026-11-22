using Xunit;

namespace DontStarve.Tests.Sanity.PassOut;

public sealed class SanityTwoAmSpecialDeathRuntimeContractTests
{
    [Fact]
    public void RuntimeOwnsThePostAnimationPassOutSeamAndFrozenPromptKeys()
    {
        var source = ReadContract("SmapiSanityTwoAmSpecialDeathService.cs");

        Assert.Contains("passOutFromTired", source, StringComparison.Ordinal);
        Assert.DoesNotContain("startToPassOut", source, StringComparison.Ordinal);
        Assert.Contains("darkness-attack.prompt.entered", source, StringComparison.Ordinal);
        Assert.Contains("darkness-attack.prompt.warning", source, StringComparison.Ordinal);
        Assert.Contains("darkness-attack.prompt.resolved", source, StringComparison.Ordinal);
        Assert.Contains("PlayerDeathCauseLedger", source, StringComparison.Ordinal);
        Assert.DoesNotContain("addMailForTomorrow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PassOutMail", source, StringComparison.Ordinal);
        Assert.DoesNotContain("passout.two-am.mail.body", source, StringComparison.Ordinal);
        Assert.DoesNotContain("warpFarmer(\"Hospital\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EventPathAttackSoundIsRestoredAtNormalVolumeAfterUserConfirmation()
    {
        // 2026-11-14 恢复：20261113 的临时静音措施（让用户实机确认 ow 是否被长音混音遮蔽）
        // 已完成使命，用户确认 ow 可听，Hit 拍恢复 audio.TriggerDarknessAttack() 正常音量。
        // 仅事件三拍路径受控；实机黑暗袭击路径（SmapiDarknessAttackService 等）的正常调用
        // 必须保持原样。
        var source = ReadContract("SmapiSanityTwoAmSpecialDeathService.cs");

        var hitStart = source.IndexOf(
            "SanityTwoAmDarknessEventCommand.Hit)",
            StringComparison.Ordinal
        );
        Assert.True(hitStart >= 0);
        var hitEnd = source.IndexOf("SendOrHandlePresentationStep(snapshot, \"hit\");", hitStart, StringComparison.Ordinal);
        Assert.True(hitEnd > hitStart);
        var hitBlock = source[hitStart..hitEnd];

        Assert.Contains("PlayOwnerLocalCue(\"ow\")", hitBlock, StringComparison.Ordinal);
        // Hit 拍必须存在一条活跃（未注释）的黑暗袭击长音调用。
        Assert.Contains(
            "audio.TriggerDarknessAttack();",
            hitBlock.Replace("// audio.TriggerDarknessAttack();", string.Empty, StringComparison.Ordinal),
            StringComparison.Ordinal
        );

        // 实机黑暗袭击路径的正常调用方不得被静音。
        var attackService = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", "DarknessAttack", "SmapiDarknessAttackService.cs")
        );
        Assert.Contains(
            "audio.TriggerDarknessAttack();",
            attackService,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void DarknessEventScriptReappliesCollapsePoseBeforeFade()
    {
        // 任务5-B（2026-11-13）：原版 InitializeEvent 的角色位置字段会把农夫弹回站立帧，
        // 导致 fade 全黑前短暂"正常站立"。脚本必须在第一个命令位重新定格昏迷第 5 帧，
        // 且该命令必须位于 fade 之前。
        var source = ReadContract("SanityTwoAmDarknessEvent.cs");

        Assert.Contains(
            "private const string CollapsePoseCommand = \"dontStarveDarknessCollapsePose\";",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains("CollapsePoseCommand,", source, StringComparison.Ordinal);
        Assert.Contains("\"fade\",", source, StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("CollapsePoseCommand,", StringComparison.Ordinal)
                < source.IndexOf("\"fade\",", StringComparison.Ordinal),
            "the collapse pose command must be emitted before fade"
        );
        Assert.Contains(
            "eventFarmer.FarmerSprite.setCurrentSingleFrame(",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "eventFarmer.FarmerSprite.PauseForSingleAnimation = true;",
            source,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void HitBeatCueIsOwnerLocalWithoutNetAudioBroadcast()
    {
        // C.1 决议（2026-11-13）：ow/死亡音只有该玩家听得见。playNearbySoundAll 的 PlayAll 分支
        // 在联机时走 location.netAudio.Fire 广播给全部 peer、分屏同地点也会同步到另一屏，
        // 两者都违规；必须走 PlayLocal（本机 sound bank 直出、不广播）。location/position
        // 传 null 让 GetVolumeForDistance 恒为 1——事件镜头冻结在屏外约 1000 格，带位置会被
        // 距离门压成 0（上一轮无声真因）。
        var source = ReadContract("SmapiSanityTwoAmSpecialDeathService.cs");
        var hitStart = source.IndexOf(
            "SanityTwoAmDarknessEventCommand.Hit)",
            StringComparison.Ordinal
        );
        Assert.True(hitStart >= 0);
        var hitEnd = source.IndexOf("SendOrHandlePresentationStep(snapshot, \"hit\");", hitStart, StringComparison.Ordinal);
        Assert.True(hitEnd > hitStart);
        var hitBlock = source[hitStart..hitEnd];

        Assert.Contains("PlayOwnerLocalCue(\"ow\")", hitBlock, StringComparison.Ordinal);
        Assert.Contains("PlayOwnerLocalCue(\"death\")", hitBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("playNearbySoundAll", hitBlock, StringComparison.Ordinal);

        var helperStart = source.IndexOf(
            "private void PlayOwnerLocalCue",
            StringComparison.Ordinal
        );
        Assert.True(helperStart >= 0);
        // 边界取 PlayOwnerLocalCue 之后定义的下一个方法（旧的 ShowPrompt 死代码已删除）。
        var helperEnd = source.IndexOf("private void OnLocalPresentationCommand", helperStart, StringComparison.Ordinal);
        if (helperEnd < 0)
            helperEnd = source.IndexOf("private void SendOrHandlePresentationStep", helperStart, StringComparison.Ordinal);
        Assert.True(helperEnd > helperStart);
        var helper = source[helperStart..helperEnd];
        Assert.Contains("Game1.sounds.PlayLocal(", helper, StringComparison.Ordinal);
        Assert.Contains("SoundContext.Default", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("netAudio", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void NightDeathChangesDayInPlaceWithoutHomeWarpAndLocksTheWakeTarget()
    {
        // 医院直醒（2026-11-13）：Default 夜里不回家、不 warp 床；原地无费用换日；换日后
        // 覆写 lastSleep 为原版复活目标并置 sleptInTemporaryBed；死亡登记由睡眠适配器持久化。
        var source = ReadContract("SmapiSanityTwoAmSpecialDeathService.cs");
        var start = source.IndexOf(
            "private void BeginPendingDarknessDeath(Farmer farmer)",
            StringComparison.Ordinal
        );
        Assert.True(start >= 0);
        var end = source.IndexOf(
            "internal void BeginPendingDarknessWakeDeath",
            start,
            StringComparison.Ordinal
        );
        Assert.True(end > start);
        var method = source[start..end];

        Assert.Contains("BeginNoFeeNewDayInPlace(farmer)", method, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginNoFeePassOut(", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.warpFarmer", method, StringComparison.Ordinal);
        Assert.DoesNotContain("GetPlayerBedSpot", method, StringComparison.Ordinal);
        Assert.Contains("DarknessRevivePolicy.Resolve", method, StringComparison.Ordinal);
        Assert.Contains("farmer.lastSleepLocation.Value = revive.Location", method, StringComparison.Ordinal);
        // ReviveTarget 是纯规则层类型（测试工程源码链接编译，禁 Xna 类型）；Point 在 SMAPI 适配层就地构造。
        Assert.Contains(
            "farmer.lastSleepPoint.Value = new Microsoft.Xna.Framework.Point(revive.TileX, revive.TileY)",
            method,
            StringComparison.Ordinal
        );
        Assert.Contains("farmer.sleptInTemporaryBed.Value = true", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.killScreen = true", method, StringComparison.Ordinal);
        Assert.DoesNotContain("farmer.health = 0", method, StringComparison.Ordinal);
    }

    [Fact]
    public void MorningWakeDeathMarksSameDayAndCallsCheckForEventsDirectly()
    {
        // B 决议（2026-11-13）：清晨同日 MarkDarkness（满足台词 seam）→ TimesUnconscious++ →
        // killScreen=true（pauseTime 保持 0，updatePause 的 pauseTime 门不触发）→ 直调
        // checkForEvents 原地建 PlayerKilled；事件迟迟未建要 fail-open（清 killScreen + Error）。
        var source = ReadContract("SmapiSanityTwoAmSpecialDeathService.cs");
        var start = source.IndexOf(
            "internal void BeginPendingDarknessWakeDeath(Farmer farmer)",
            StringComparison.Ordinal
        );
        Assert.True(start >= 0);
        var end = source.IndexOf(
            "private void AdvancePendingDarknessWakeDeadline",
            start,
            StringComparison.Ordinal
        );
        Assert.True(end > start);
        var method = source[start..end];

        var killScreenIndex = method.IndexOf("Game1.killScreen = true;", StringComparison.Ordinal);
        var checkEventsIndex = method.IndexOf("Game1.currentLocation.checkForEvents();", StringComparison.Ordinal);
        var markIndex = method.IndexOf("deathCauses.MarkDarkness(farmer);", StringComparison.Ordinal);
        Assert.True(markIndex >= 0 && killScreenIndex > markIndex, "MarkDarkness must precede killScreen");
        Assert.True(checkEventsIndex > killScreenIndex, "checkForEvents must run after killScreen is armed");
        Assert.Contains("Game1.stats.TimesUnconscious++", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.pauseTime", method, StringComparison.Ordinal);

        Assert.Contains("FailOpenPendingDarknessWakeDeath();", method, StringComparison.Ordinal);
        Assert.Contains("Game1.killScreen = false;", source, StringComparison.Ordinal);
        Assert.Contains("pending-darkness-wake-event-timeout", source, StringComparison.Ordinal);
        Assert.Contains("pending-darkness-wake-event-missing", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MorningWakeDeathDiscardsTheStaleNightPresentationWarpBeforeAnythingElse()
    {
        // 修复3（2026-11-14，实机根因）：夜里三拍演出 eventFinished 按原版语义发起
        // "回到事件前位置"收尾 warp（Game1.cs:7261），原地换日桥的 NewDay 置 nonWarpFade=true
        // （Game1.cs:10372）令该 warp 的 fade 被掠过，请求残留挂在 Game1.locationRequest/
        // _isWarping；换日 ApplyWakeUpPosition 直接赋值进医院、不消费它。死亡事件结束后的
        // 第一次 fade 完成把它落地（Game1.cs:6315），玩家被传送回昏迷地点。
        // 契约：清晨序列必须在血 0 委托分支与 killScreen/checkForEvents 之前丢弃陈旧请求——
        // 这样事件结束时 eventFinished 才会生成同地点（Hospital）收尾 LocationRequest，
        // 玩家稳在医院复活 tile（vanilla 同款 resetForPlayerEntry 由该收尾 warp 触发）。
        var source = ReadContract("SmapiSanityTwoAmSpecialDeathService.cs");
        var methodStart = source.IndexOf(
            "internal void BeginPendingDarknessWakeDeath(Farmer farmer)",
            StringComparison.Ordinal
        );
        Assert.True(methodStart >= 0);
        var methodEnd = source.IndexOf(
            "private void AdvancePendingDarknessWakeDeadline",
            methodStart,
            StringComparison.Ordinal
        );
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];

        var discardIndex = method.IndexOf("DiscardStaleNightPresentationWarp();", StringComparison.Ordinal);
        var delegateIndex = method.IndexOf("if (farmer.health <= 0)", StringComparison.Ordinal);
        var killScreenIndex = method.IndexOf("Game1.killScreen = true;", StringComparison.Ordinal);
        var checkEventsIndex = method.IndexOf("Game1.currentLocation.checkForEvents();", StringComparison.Ordinal);
        Assert.True(discardIndex >= 0, "the stale warp discard must run in the wake sequence");
        Assert.True(delegateIndex > discardIndex, "the discard must precede the zero-health delegation branch");
        Assert.True(killScreenIndex > discardIndex, "the discard must precede killScreen");
        Assert.True(checkEventsIndex > discardIndex, "the discard must precede checkForEvents");

        // 丢弃动作本体：清 locationRequest（public static）+ 反射清 _isWarping（protected static）。
        var discardStart = source.IndexOf("private void DiscardStaleNightPresentationWarp()", StringComparison.Ordinal);
        Assert.True(discardStart >= 0);
        var discardEnd = source.IndexOf("private static Farmer? ResolveFarmer(", discardStart, StringComparison.Ordinal);
        Assert.True(discardEnd > discardStart);
        var discardMethod = source[discardStart..discardEnd];
        Assert.Contains("Game1.locationRequest = null;", discardMethod, StringComparison.Ordinal);
        Assert.Contains("AccessTools.Field(typeof(Game1), \"_isWarping\")", source, StringComparison.Ordinal);
        Assert.Contains("isWarpingField.SetValue(null, false)", discardMethod, StringComparison.Ordinal);
        // 只在确实存在陈旧请求时才丢弃；空请求跳过丢弃但仍走对齐清理。
        Assert.Contains("if (Game1.locationRequest is not null)", discardMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void MorningWakeDeathAlignsVanillaWarpFadeStateAfterDiscardingTheStaleWarp()
    {
        // 修复4（2026-11-14，实机"诊所门全失灵"）：vanilla 死亡链的 killScreen warp 由
        // updatePause 发起，发起前 nonWarpFade 恒为 false、fade 周期刚被完整消费；本 mod 的
        // 直置 killScreen 序列绕过该通道，夜里 NewDay 置 true 的 nonWarpFade 若未被
        // AfterNewDay / fade 完成 #2 清理（一帧窗口，可被 end-of-night 停更/messagePause 冻结
        // 跳过），exitEvent→eventFinished→同地点收尾 warp 的落地 fade 会被 Game1.cs:6181 的
        // `!nonWarpFade` 门跳过，传送状态机滞留（11297 `!fadeToBlack` 总闸卡死 = 门全灭、
        // 走路不受影响的唯一结构吻合签名）。契约：丢弃陈旧请求之后同拍把 nonWarpFade/fade
        // 四件套归零（vanilla updatePause-warp 前的状态口径），eventUp 期间绝不触碰。
        var source = ReadContract("SmapiSanityTwoAmSpecialDeathService.cs");
        var discardStart = source.IndexOf("private void DiscardStaleNightPresentationWarp()", StringComparison.Ordinal);
        Assert.True(discardStart >= 0);
        var discardEnd = source.IndexOf("private static Farmer? ResolveFarmer(", discardStart, StringComparison.Ordinal);
        Assert.True(discardEnd > discardStart);
        var discardMethod = source[discardStart..discardEnd];

        var eventUpGuardIndex = discardMethod.IndexOf("if (Game1.eventUp)", StringComparison.Ordinal);
        Assert.True(eventUpGuardIndex >= 0, "the fade alignment must never touch a live event");

        Assert.Contains("Game1.nonWarpFade = false;", discardMethod, StringComparison.Ordinal);
        Assert.Contains("Game1.fadeToBlack = false;", discardMethod, StringComparison.Ordinal);
        Assert.Contains("Game1.fadeIn = false;", discardMethod, StringComparison.Ordinal);
        Assert.Contains("Game1.fadeToBlackAlpha = 0f;", discardMethod, StringComparison.Ordinal);

        // 归零必须在 eventUp 守卫之后（守护在播事件的 fade 周期）。
        var alignIndex = discardMethod.IndexOf("Game1.nonWarpFade = false;", StringComparison.Ordinal);
        Assert.True(alignIndex > eventUpGuardIndex, "the alignment must be gated behind the eventUp guard");
    }

    [Fact]
    public void WakeDeathWarpDiagnosticsAreNotInstalledByTheMod()
    {
        var entrySource = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", "NaturalDarkness", "ModEntry.cs")
        );

        Assert.DoesNotContain("SmapiWakeDeathWarpDiagnostics", entrySource, StringComparison.Ordinal);
        Assert.DoesNotContain("[TEMP-DIAG]", entrySource, StringComparison.Ordinal);
    }

    [Fact]
    public void RoutineSpecialPassOutDiagnosticsAreNotWrittenToSmapiConsole()
    {
        var source = ReadContract("SmapiSanityTwoAmSpecialDeathService.cs");

        Assert.DoesNotContain("LogRuntime(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Discarded the stale night presentation warp", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Aligned warp/fade state before the wake death sequence", source, StringComparison.Ordinal);

        // State repair and actionable failure warnings remain; only routine internal tracing is removed.
        Assert.Contains("Game1.nonWarpFade = false;", source, StringComparison.Ordinal);
        Assert.Contains("Game1.fadeToBlack = false;", source, StringComparison.Ordinal);
        Assert.Contains("passout.two-am.stale-warp-clear-failed", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviveHealthIsRegisteredIntoThePreSaveSeamAtPendingDarknessRegistration()
    {
        // 问题3（2026-11-14，用户决议）：复活血量（原版 revive=10）的设定时机必须与饥饿的
        // 保存前最终值一致——在 pending-darkness 登记点把 revive=10 写进
        // SpecialPassOutSanitySettlement，由 dayupdate postfix / SaveFarmhand prefix / OnSaving
        // 三个保存前缝写回；清晨 vanilla checkForEvents（GameLocation.cs:15614）的再次赋值
        // 成为无害幂等重复。血 0 双死守卫（ShouldDelegateDeathToVanilla）语义不变。
        Assert.Equal(10, DontStarve.Player.Sleep.PendingDarknessWakePolicy.VanillaReviveHealth);

        var adapterSource = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", "SleepLifecycle", "SmapiSleepLifecycleAdapter.cs")
        );
        var registerStart = adapterSource.IndexOf(
            "internal bool RegisterPendingDarknessDeath(Farmer farmer)",
            StringComparison.Ordinal
        );
        Assert.True(registerStart >= 0);
        var registerEnd = adapterSource.IndexOf("/// <summary>存档用标记", registerStart, StringComparison.Ordinal);
        Assert.True(registerEnd > registerStart);
        var registerMethod = adapterSource[registerStart..registerEnd];

        Assert.Contains(
            "RegisterPendingDarknessWakeHealth(",
            registerMethod,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "PendingDarknessWakePolicy.VanillaReviveHealth",
            registerMethod,
            StringComparison.Ordinal
        );
        // 登记只面向主机权威（客户端没有保存前写回缝）。
        var hostGateIndex = registerMethod.IndexOf(
            "if (Role != SleepAuthorityRole.Host || !Context.IsMainPlayer)",
            StringComparison.Ordinal
        );
        var healthRegisterIndex = registerMethod.IndexOf(
            "RegisterPendingDarknessWakeHealth(",
            StringComparison.Ordinal
        );
        Assert.True(hostGateIndex >= 0 && healthRegisterIndex > hostGateIndex);
    }

    [Fact]
    public void Guard_bail_fail_open_reports_failure_and_keeps_the_marker()
    {
        // 修复1（阻断级）fail-open 路径 4：守卫早退。旧行为静默 return——适配器已把玩家记入
        // fired、落盘排除，死亡未完成却把标记从磁盘清掉，读档跳过死亡。必须上报 Failed。
        var source = ReadContract("SmapiSanityTwoAmSpecialDeathService.cs");
        var methodStart = source.IndexOf(
            "internal void BeginPendingDarknessWakeDeath(Farmer farmer)",
            StringComparison.Ordinal
        );
        Assert.True(methodStart >= 0);
        var zeroHealthIndex = source.IndexOf("if (farmer.health <= 0)", methodStart, StringComparison.Ordinal);
        Assert.True(zeroHealthIndex > methodStart);
        var guardBlock = source[methodStart..zeroHealthIndex];

        Assert.Contains("passout.two-am.pending-darkness-wake-guard-bail", guardBlock, StringComparison.Ordinal);
        Assert.Contains("ReportPendingDarknessWakeOutcome(farmer, completed: false)", guardBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_for_events_exception_fail_open_reports_failure_and_keeps_the_marker()
    {
        // 修复1 fail-open 路径 2：直调 checkForEvents 抛异常。清 killScreen 放人之外必须上报
        // Failed，适配器保留 pendingDarknessDeaths（下次存档仍落盘、读档必重演）。
        var source = ReadContract("SmapiSanityTwoAmSpecialDeathService.cs");
        var methodStart = source.IndexOf(
            "internal void BeginPendingDarknessWakeDeath(Farmer farmer)",
            StringComparison.Ordinal
        );
        Assert.True(methodStart >= 0);
        var checkEventsIndex = source.IndexOf("Game1.currentLocation.checkForEvents();", methodStart, StringComparison.Ordinal);
        Assert.True(checkEventsIndex > methodStart);
        var deadlineResetIndex = source.IndexOf("pendingDarknessDeathDeadlineTick = -1;", checkEventsIndex, StringComparison.Ordinal);
        Assert.True(deadlineResetIndex > checkEventsIndex);
        var catchBlock = source[checkEventsIndex..deadlineResetIndex];

        Assert.Contains("catch (Exception exception)", catchBlock, StringComparison.Ordinal);
        Assert.Contains("FailOpenPendingDarknessWakeDeath();", catchBlock, StringComparison.Ordinal);
        Assert.Contains("ReportPendingDarknessWakeOutcome(farmer, completed: false)", catchBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_player_killed_event_fail_open_reports_failure_and_the_built_event_reports_completion()
    {
        // 修复1 fail-open 路径 3：checkForEvents 未建事件（原版已清 killScreen 补 10 血）。
        // 必须上报 Failed 保留标记；事件建成（同帧直调成功）则上报 Completed 清标记。
        var source = ReadContract("SmapiSanityTwoAmSpecialDeathService.cs");
        var methodStart = source.IndexOf(
            "internal void BeginPendingDarknessWakeDeath(Farmer farmer)",
            StringComparison.Ordinal
        );
        Assert.True(methodStart >= 0);
        var methodEnd = source.IndexOf("private void FailOpenPendingDarknessWakeDeath", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart);
        var deadlineResetIndex = source.IndexOf("pendingDarknessDeathDeadlineTick = -1;", methodStart, StringComparison.Ordinal);
        Assert.True(deadlineResetIndex > methodStart);
        var tail = source[deadlineResetIndex..methodEnd];

        var missingIndex = tail.IndexOf("pending-darkness-wake-event-missing", StringComparison.Ordinal);
        Assert.True(missingIndex >= 0);
        var failedReportIndex = tail.IndexOf("ReportPendingDarknessWakeOutcome(farmer, completed: false)", missingIndex, StringComparison.Ordinal);
        Assert.True(failedReportIndex > missingIndex);
        var completedIndex = tail.IndexOf("ReportPendingDarknessWakeOutcome(farmer, completed: true)", failedReportIndex, StringComparison.Ordinal);
        Assert.True(completedIndex > failedReportIndex);
    }

    [Fact]
    public void Deadline_timeout_and_external_clear_fail_open_report_failure_and_async_event_reports_completion()
    {
        // 修复1 fail-open 路径 1：180 tick 超时。以及同门的"killScreen 被外部清掉且事件未建"
        // 分支——两者都必须上报 Failed 保留标记；事件异步建成上报 Completed。
        var source = ReadContract("SmapiSanityTwoAmSpecialDeathService.cs");
        var start = source.IndexOf(
            "private void AdvancePendingDarknessWakeDeadline()",
            StringComparison.Ordinal
        );
        Assert.True(start >= 0);
        var end = source.IndexOf("private void AdvanceVanillaNativeDeathWatch", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var method = source[start..end];

        var eventUpIndex = method.IndexOf("if (Game1.eventUp)", StringComparison.Ordinal);
        Assert.True(eventUpIndex >= 0);
        var completedReportIndex = method.IndexOf("ReportPendingDarknessWakeOutcome(farmer, completed: true)", eventUpIndex, StringComparison.Ordinal);
        Assert.True(completedReportIndex > eventUpIndex);

        var clearedIndex = method.IndexOf("pending-darkness-wake-kill-screen-cleared", StringComparison.Ordinal);
        Assert.True(clearedIndex > completedReportIndex);
        var clearedReportIndex = method.IndexOf("ReportPendingDarknessWakeOutcome(farmer, completed: false)", clearedIndex, StringComparison.Ordinal);
        Assert.True(clearedReportIndex > clearedIndex);

        var timeoutIndex = method.IndexOf("pending-darkness-wake-event-timeout", StringComparison.Ordinal);
        Assert.True(timeoutIndex > clearedReportIndex);
        var timeoutTail = method[timeoutIndex..];
        Assert.Contains("FailOpenPendingDarknessWakeDeath();", timeoutTail, StringComparison.Ordinal);
        Assert.Contains("ReportPendingDarknessWakeOutcome(farmer, completed: false)", timeoutTail, StringComparison.Ordinal);
    }

    [Fact]
    public void Zero_health_morning_delegates_to_the_vanilla_death_block_exactly_once()
    {
        // 修复2（血 0 边界，恰一次）：夜间结算血量恰为 0 时，原版清晨 0 血死亡块
        // （Farmer.cs CanMove+health<=0+!killScreen+timeOfDay<2600）原生触发本次死亡。
        // mod 拍点先查 health<=0：命中则只做同日 MarkDarkness（台词 seam）+挂原版观察，
        // 不置 killScreen、不自增 TimesUnconscious——双扣费/双掉物/双计数不会发生。
        var source = ReadContract("SmapiSanityTwoAmSpecialDeathService.cs");
        var methodStart = source.IndexOf(
            "internal void BeginPendingDarknessWakeDeath(Farmer farmer)",
            StringComparison.Ordinal
        );
        Assert.True(methodStart >= 0);
        var methodEnd = source.IndexOf("private void FailOpenPendingDarknessWakeDeath", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];

        var zeroHealthIndex = method.IndexOf("if (farmer.health <= 0)", StringComparison.Ordinal);
        Assert.True(zeroHealthIndex >= 0);
        var countedIndex = method.IndexOf("if (wakeDeathCountedPlayers.Add(PlayerKey(farmer)))", StringComparison.Ordinal);
        Assert.True(countedIndex > zeroHealthIndex);
        var killScreenIndex = method.IndexOf("Game1.killScreen = true;", StringComparison.Ordinal);
        Assert.True(killScreenIndex > countedIndex);
        var markIndex = method.IndexOf("deathCauses.MarkDarkness(farmer);", StringComparison.Ordinal);
        Assert.True(markIndex >= 0 && markIndex < killScreenIndex, "MarkDarkness must precede killScreen in both branches");
        var checkEventsIndex = method.IndexOf("Game1.currentLocation.checkForEvents();", StringComparison.Ordinal);
        Assert.True(checkEventsIndex > killScreenIndex);

        // 血 0 分支本体：MarkDarkness + 原版观察，零 mod 死亡状态写入。
        var zeroHealthBlock = method[zeroHealthIndex..countedIndex];
        Assert.Contains("deathCauses.MarkDarkness(farmer);", zeroHealthBlock, StringComparison.Ordinal);
        Assert.Contains("vanillaDeathWatchDeadlineTick = Game1.ticks + VanillaDeathWatchTimeoutTicks", zeroHealthBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.killScreen = true", zeroHealthBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.stats.TimesUnconscious++", zeroHealthBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void Vanilla_death_watch_reports_completion_without_touching_the_vanilla_kill_screen()
    {
        // 修复2：原版接管观察——eventUp（PlayerKilled 事件建成）或 killScreen 链走完
        // （sawKillScreen+!killScreen+health>0，原版 checkForEvents 补 10 血）都上报 Completed；
        // 超时只报 Failed 保留标记。观察全程不得代管/清掉原版 killScreen。
        var source = ReadContract("SmapiSanityTwoAmSpecialDeathService.cs");
        var start = source.IndexOf(
            "private void AdvanceVanillaNativeDeathWatch()",
            StringComparison.Ordinal
        );
        Assert.True(start >= 0);
        var end = source.IndexOf("private void ReportPendingDarknessWakeOutcome", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var method = source[start..end];

        var eventUpIndex = method.IndexOf("if (Game1.eventUp)", StringComparison.Ordinal);
        Assert.True(eventUpIndex >= 0);
        var eventUpReportIndex = method.IndexOf("ReportPendingDarknessWakeOutcome(farmer, completed: true)", eventUpIndex, StringComparison.Ordinal);
        Assert.True(eventUpReportIndex > eventUpIndex);

        var resolvedIndex = method.IndexOf("vanillaDeathWatchSawKillScreen && !Game1.killScreen && farmer.health > 0", StringComparison.Ordinal);
        Assert.True(resolvedIndex > eventUpReportIndex);
        var resolvedReportIndex = method.IndexOf("ReportPendingDarknessWakeOutcome(farmer, completed: true)", resolvedIndex, StringComparison.Ordinal);
        Assert.True(resolvedReportIndex > resolvedIndex);

        var timeoutIndex = method.IndexOf("passout.two-am.pending-darkness-vanilla-watch-timeout", StringComparison.Ordinal);
        Assert.True(timeoutIndex > resolvedReportIndex);
        var timeoutTail = method[timeoutIndex..];
        Assert.Contains("ReportPendingDarknessWakeOutcome(farmer, completed: false)", timeoutTail, StringComparison.Ordinal);

        Assert.DoesNotContain("Game1.killScreen = false", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.killScreen = true", method, StringComparison.Ordinal);
    }

    [Fact]
    public void Manual_death_count_is_once_per_marker_instance()
    {
        // 修复1：手工 TimesUnconscious++ 只对同一在册标记实例生效一次——fail-open 有界重试
        // 重走 killScreen→checkForEvents 时不重复自增（计数恰+1）；新死亡登记与会话清理重置。
        var source = ReadContract("SmapiSanityTwoAmSpecialDeathService.cs");

        var methodStart = source.IndexOf(
            "internal void BeginPendingDarknessWakeDeath(Farmer farmer)",
            StringComparison.Ordinal
        );
        Assert.True(methodStart >= 0);
        var countedIndex = source.IndexOf("if (wakeDeathCountedPlayers.Add(PlayerKey(farmer)))", methodStart, StringComparison.Ordinal);
        Assert.True(countedIndex > methodStart);
        var killScreenIndex = source.IndexOf("Game1.killScreen = true;", countedIndex, StringComparison.Ordinal);
        Assert.True(killScreenIndex > countedIndex);
        var countedBlock = source[countedIndex..killScreenIndex];
        Assert.Contains("deathCauses.MarkDarkness(farmer);", countedBlock, StringComparison.Ordinal);
        Assert.Contains("Game1.stats.TimesUnconscious++", countedBlock, StringComparison.Ordinal);

        var nightStart = source.IndexOf(
            "private void BeginPendingDarknessDeath(Farmer farmer)",
            StringComparison.Ordinal
        );
        Assert.True(nightStart >= 0);
        var nightEnd = source.IndexOf("/// <summary>", nightStart, StringComparison.Ordinal);
        Assert.True(nightEnd > nightStart);
        Assert.Contains("wakeDeathCountedPlayers.Remove(PlayerKey(farmer))", source[nightStart..nightEnd], StringComparison.Ordinal);

        Assert.Contains("wakeDeathCountedPlayers.Clear();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Wake_replay_suppresses_the_vanilla_revive_health_differential_via_the_existing_seam()
    {
        // 2026-11-14 实机反馈回归锁（复活重置静音）：mod 直置 killScreen 的清晨重演路径里
        // 玩家从未处于 ≤0 死亡态，watcher 的"持续死亡态复活重置"规则前提不成立；读档重演
        // 旧档时血量基线可能是 100，原版 checkForEvents killScreen 块（GameLocation.cs:15614）
        // 平写 health=10 会被差分 watcher 当离散下降播音。重演序列必须在原版写值前按既有
        // overtime 抑制口径（StatusFeedbackHealthSuppression）发放令牌，且只覆盖 mod 直置
        // 分支——血 0 委托分支走原版死亡屏，仍由持续死亡态规则静音，不得多发令牌。
        var source = ReadContract("SmapiSanityTwoAmSpecialDeathService.cs");

        var methodStart = source.IndexOf(
            "internal void BeginPendingDarknessWakeDeath(Farmer farmer)",
            StringComparison.Ordinal
        );
        Assert.True(methodStart >= 0);
        var zeroHealthIndex = source.IndexOf("if (farmer.health <= 0)", methodStart, StringComparison.Ordinal);
        Assert.True(zeroHealthIndex > methodStart);
        var killScreenIndex = source.IndexOf("Game1.killScreen = true;", zeroHealthIndex, StringComparison.Ordinal);
        Assert.True(killScreenIndex > zeroHealthIndex);
        var modDirectedBlock = source[zeroHealthIndex..killScreenIndex];

        var suppressionIndex = modDirectedBlock.IndexOf(
            "StatusFeedbackHealthSuppression.SuppressNextHealthSound(",
            StringComparison.Ordinal
        );
        Assert.True(suppressionIndex >= 0);
        Assert.Contains(
            "DontStarve.Player.Sleep.PendingDarknessWakePolicy.VanillaReviveHealth",
            modDirectedBlock,
            StringComparison.Ordinal
        );

        // 血 0 委托分支（血量<=0 早退之前）不得出现抑制调用：原版真实死亡屏由
        // watcher 的持续死亡态复活重置规则静音，重复发令牌会误吞其后的真实音效。
        var guardBlock = source[methodStart..zeroHealthIndex];
        Assert.DoesNotContain(
            "StatusFeedbackHealthSuppression.SuppressNextHealthSound(",
            guardBlock,
            StringComparison.Ordinal
        );
    }

    private static string ReadContract(string fileName) => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Contracts", "PassOut", fileName)
    );
}
