using System;
using System.IO;
using DontStarve.Player.Sleep;
using Xunit;

namespace DontStarve.Tests.Player.Sleep;

public sealed class PendingDarknessWakePolicyTests
{
    [Theory]
    [InlineData(true, false, true, false, false, false, false, false, true, true)]
    [InlineData(false, false, true, false, false, false, false, false, true, false)]
    [InlineData(true, true, true, false, false, false, false, false, true, false)]
    [InlineData(true, false, false, false, false, false, false, false, true, false)]
    [InlineData(true, false, true, true, false, false, false, false, true, false)]
    [InlineData(true, false, true, false, true, false, false, false, true, false)]
    [InlineData(true, false, true, false, false, true, false, false, true, false)]
    [InlineData(true, false, true, false, false, false, true, false, true, false)]
    [InlineData(true, false, true, false, false, false, false, true, true, false)]
    [InlineData(true, false, true, false, false, false, false, false, false, false)]
    public void Death_fires_only_on_the_first_fully_stable_morning_tick(
        bool markerPresent,
        bool wakeSequenceBlockedThisSession,
        bool worldReady,
        bool isFading,
        bool eventUp,
        bool menuOpen,
        bool newDay,
        bool killScreen,
        bool messageAfterPauseEmpty,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            PendingDarknessWakePolicy.ShouldBeginDeath(
                markerPresent,
                wakeSequenceBlockedThisSession,
                worldReady,
                isFading,
                eventUp,
                menuOpen,
                newDay,
                killScreen,
                messageAfterPauseEmpty
            )
        );
    }

    [Theory]
    [InlineData(true, true, true, false, true)]
    [InlineData(false, true, true, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, true, true, true, false)]
    public void Zero_health_delegates_the_death_to_the_vanilla_morning_block(
        bool markerPresent,
        bool healthZero,
        bool vanillaDeathBlockWindow,
        bool wakeSequenceBlockedThisSession,
        bool expected
    )
    {
        // 修复2（血 0 边界）：原版清晨 0 血死亡块（CanMove+health<=0+!killScreen+timeOfDay<2600）
        // 已经或即将原生触发死亡；mod 序列不得叠加。委托判定刻意不按稳定门门控（原版死亡块
        // 不等世界稳定，淡入期间就触发），也不排除 killScreen=true（那是原版死亡链在途的信号）。
        Assert.Equal(
            expected,
            PendingDarknessWakePolicy.ShouldDelegateDeathToVanilla(
                markerPresent,
                healthZero,
                vanillaDeathBlockWindow,
                wakeSequenceBlockedThisSession
            )
        );
    }

    [Fact]
    public void Wake_sequence_block_combines_in_flight_state_with_the_retry_budget()
    {
        // 修复1（有界重试）：在途封锁 + 重试预算耗尽都构成会话内封锁；预算上限固定为 3，
        // 耗尽后标记保留给下次读档（Warn 一次），绝不每 tick 无限重试。
        Assert.Equal(3, PendingDarknessWakePolicy.MaximumAttempts);

        Assert.False(PendingDarknessWakePolicy.IsWakeSequenceBlockedThisSession(false, 0));
        Assert.False(PendingDarknessWakePolicy.IsWakeSequenceBlockedThisSession(false, 2));
        Assert.True(PendingDarknessWakePolicy.IsWakeSequenceBlockedThisSession(true, 0));
        Assert.True(PendingDarknessWakePolicy.IsWakeSequenceBlockedThisSession(false, 3));
        Assert.True(PendingDarknessWakePolicy.IsWakeSequenceBlockedThisSession(false, 4));
    }

    [Fact]
    public void The_gate_has_no_health_requirement_and_the_old_hidden_hospital_machine_is_gone()
    {
        // 医院直醒（2026-11-13 重写）：死亡重演不依赖 0 血，旧 HiddenHospital 稳定按住/假死
        // handoff 机器整体废弃；饿死 Natural 路径的门闩（WakeDeathLoadGatePolicy）不受影响。
        var policySource = ReadSource("DontStarve", "Player", "Sleep", "PendingDarknessWakePolicy.cs");
        var signatureStart = policySource.IndexOf(
            "internal static bool ShouldBeginDeath(",
            StringComparison.Ordinal
        );
        Assert.True(signatureStart >= 0);
        var signatureEnd = policySource.IndexOf(')', signatureStart);
        Assert.True(signatureEnd > signatureStart);
        // 稳定门签名不得再携带 health 参数（旧 HiddenHospital 门以 0 血为前置）。
        Assert.DoesNotContain("int health", policySource[signatureStart..signatureEnd], StringComparison.Ordinal);

        Assert.Throws<FileNotFoundException>(() => ReadSource(
            "DontStarve",
            "Player",
            "Sleep",
            "WakeDeathPresentationPolicy.cs"
        ));

        var adapterSource = ReadSource(
            "DontStarve",
            "Player",
            "Sleep",
            "SmapiSleepLifecycleAdapter.cs"
        );
        Assert.DoesNotContain("HiddenHospital", adapterSource, StringComparison.Ordinal);
        Assert.Contains("RegisterPendingDarknessDeath", adapterSource, StringComparison.Ordinal);
        Assert.Contains("PendingDarknessWakeReady", adapterSource, StringComparison.Ordinal);
        // 适配器内的标记容器是 camelCase 私有字段（PascalCase 属性在 SleepWakeEffectsData）。
        Assert.Contains("pendingDarknessDeaths", adapterSource, StringComparison.Ordinal);

        var entrySource = ReadSource("DontStarve", "ModEntry.cs");
        Assert.Contains(
            "PendingDarknessWakeReady += _sanityTwoAmPassOut.BeginPendingDarknessWakeDeath",
            entrySource,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("BeginHiddenHospitalHandoff", entrySource, StringComparison.Ordinal);
    }

    [Fact]
    public void Death_marker_capture_excludes_only_confirmed_completions()
    {
        // 修复1（阻断级）：排除时机必须是"死亡确认完成后"，不是"已触发"。旧行为在触发前把
        // 玩家记入 fired 集合并在落盘时无条件排除——四条 fail-open 路径后死亡未完成，下次
        // 存档却把标记从磁盘清掉，读档直接跳过死亡（逃课）。新语义：
        // · fired-且-完成 → 完成上报把玩家移出 pendingDarknessDeaths，下次存档覆盖为"已完成"；
        // · fired-但-失败 → fail-open 上报只解除在途封锁，pendingDarknessDeaths 保留落盘。
        var adapterSource = ReadSource(
            "DontStarve",
            "Player",
            "Sleep",
            "SmapiSleepLifecycleAdapter.cs"
        );

        var markersStart = adapterSource.IndexOf(
            "private IReadOnlyDictionary<string, int> PendingDarknessWakeDeathMarkers()",
            StringComparison.Ordinal
        );
        Assert.True(markersStart >= 0);
        var markersEnd = adapterSource.IndexOf(
            "internal void SendPendingDarknessWakeSnapshot",
            markersStart,
            StringComparison.Ordinal
        );
        Assert.True(markersEnd > markersStart);
        var markersMethod = adapterSource[markersStart..markersEnd];

        // 旧偏离必须彻底移除：落盘不得再按会话触发集合过滤。
        Assert.DoesNotContain("firedPendingDarknessWakes", markersMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("!firedPendingDarknessWakes.Contains(pair.Key)", markersMethod, StringComparison.Ordinal);
        // 落盘集合就是"未完成"在册标记本身（完成者已被移出）。
        Assert.Contains("new Dictionary<string, int>(pendingDarknessDeaths", markersMethod, StringComparison.Ordinal);

        var completedStart = adapterSource.IndexOf(
            "internal void OnPendingDarknessWakeCompleted",
            StringComparison.Ordinal
        );
        Assert.True(completedStart >= 0);
        var completedEnd = adapterSource.IndexOf(
            "internal void OnPendingDarknessWakeFailed",
            completedStart,
            StringComparison.Ordinal
        );
        Assert.True(completedEnd > completedStart);
        var completedMethod = adapterSource[completedStart..completedEnd];
        // 完成上报：移出在途 + 移出 pendingDarknessDeaths（落盘随之排除）。
        Assert.Contains("inFlightPendingDarknessWakes.Remove(playerKey)", completedMethod, StringComparison.Ordinal);
        Assert.Contains("pendingDarknessDeaths.Remove(playerKey)", completedMethod, StringComparison.Ordinal);

        var failedEnd = adapterSource.IndexOf(
            "/// <summary>存档用标记",
            completedEnd,
            StringComparison.Ordinal
        );
        Assert.True(failedEnd > completedEnd);
        var failedMethod = adapterSource[completedEnd..failedEnd];
        // 失败上报：只解除在途封锁（允许有界重试），绝不移出 pendingDarknessDeaths。
        Assert.Contains("inFlightPendingDarknessWakes.Remove(playerKey)", failedMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("pendingDarknessDeaths.Remove", failedMethod, StringComparison.Ordinal);
        // 预算耗尽要留 Warn（标记保留给下次读档）。
        Assert.Contains("LogLevel.Warn", failedMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void Delegation_beats_the_stability_gate_and_a_new_marker_resets_the_budget()
    {
        // 修复2 的适配器侧时序契约：血 0 委托判定必须先于稳定门（ShouldBeginDeath）执行，
        // 否则淡入帧里原版死亡先完成、稳定门事后看到 health=10 会再补一刀。
        // 修复1：新死亡登记（RegisterPendingDarknessDeath）必须重置在途与重试预算。
        var adapterSource = ReadSource(
            "DontStarve",
            "Player",
            "Sleep",
            "SmapiSleepLifecycleAdapter.cs"
        );

        var loopStart = adapterSource.IndexOf(
            "private void AdvancePendingDarknessWake()",
            StringComparison.Ordinal
        );
        Assert.True(loopStart >= 0);
        var loopEnd = adapterSource.IndexOf(
            "private void BeginPendingDarknessWakeAttempt",
            loopStart,
            StringComparison.Ordinal
        );
        Assert.True(loopEnd > loopStart);
        var loop = adapterSource[loopStart..loopEnd];

        var delegateIndex = loop.IndexOf(
            "ShouldDelegateDeathToVanilla(",
            StringComparison.Ordinal
        );
        var stabilityIndex = loop.IndexOf(
            "ShouldBeginDeath(",
            StringComparison.Ordinal
        );
        Assert.True(delegateIndex >= 0, "the zero-health delegation rule must run in the wake loop");
        Assert.True(stabilityIndex > delegateIndex, "the delegation must be evaluated before the stability gate");
        Assert.Contains("Game1.timeOfDay < 2600", loop, StringComparison.Ordinal);
        Assert.Contains("PendingDarknessWakeReady?.Invoke(farmer)", loop, StringComparison.Ordinal);

        var registerStart = adapterSource.IndexOf(
            "internal bool RegisterPendingDarknessDeath(Farmer farmer)",
            StringComparison.Ordinal
        );
        Assert.True(registerStart >= 0);
        var registerEnd = adapterSource.IndexOf("/// <summary>", registerStart, StringComparison.Ordinal);
        Assert.True(registerEnd > registerStart);
        var registerMethod = adapterSource[registerStart..registerEnd];
        Assert.Contains("inFlightPendingDarknessWakes.Remove(playerKey)", registerMethod, StringComparison.Ordinal);
        Assert.Contains("pendingDarknessWakeAttempts.Remove(playerKey)", registerMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void Wake_death_load_gate_keeps_the_natural_hunger_replay_untouched()
    {
        // 饿死 Natural 醒死路径（0 血前置门 + 淡入门闩）是独立功能，本轮重写不许破坏。
        var adapterSource = ReadSource(
            "DontStarve",
            "Player",
            "Sleep",
            "SmapiSleepLifecycleAdapter.cs"
        );
        Assert.Contains("AdvanceWakeDeathLoadGate", adapterSource, StringComparison.Ordinal);
        Assert.Contains("TryArmWakeDeathLoadGate", adapterSource, StringComparison.Ordinal);
        Assert.Contains("ShouldEarlyArm", adapterSource, StringComparison.Ordinal);

        var gateSource = ReadSource("DontStarve", "Player", "Sleep", "WakeDeathLoadGatePolicy.cs");
        Assert.Contains("health <= 0", gateSource, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = directory.FullName;
            foreach (var part in parts)
                candidate = Path.Combine(candidate, part);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
