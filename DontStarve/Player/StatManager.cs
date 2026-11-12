#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using DontStarve.Audio.StatusFeedback;
using DontStarve.Config;
using DontStarve.Display;
using DontStarve.Interface;
using DontStarve.Player.Death;
using DontStarve.Player.Stats;
using DontStarve.Player.Stats.Health;
using DontStarve.Player.Stats.Hunger;
using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.PassOut;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player;

/// <summary>
/// Initialize 的组合结果。ModEntry 需要 Sanity/Hunger 实例仅为订阅状态反馈音效的
/// 结算/回放事件；除此之外的玩法接线不得借道本结果。
/// </summary>
internal sealed record StatManagerInitializeResult(
    SanitySystemLifecycleCoordinator Lifecycle,
    Sanity Sanity,
    Hunger Hunger
);

internal static class StatManager
{
    internal static StatManagerInitializeResult Initialize(
        IModHelper helper,
        ITimeAPI timeApi,
        IMonitor monitor,
        string modId,
        TaggedHudMessageService hudMessages,
        TypedConfigResolver configResolver,
        PassOutReasonLedger passOutReasonLedger,
        PlayerDeathCauseLedger playerDeathCauseLedger,
        Func<HungerDamageMode> hungerDamageModeProvider,
        out HungerDebugController hungerDebugController,
        out SurvivalStateTimeOwner<Farmer> survivalTime,
        ISanityStatusFeedbackSink? sanityStatusFeedbackSink = null,
        IHungerStatusFeedbackSink? hungerStatusFeedbackSink = null
    )
    {
        // 进食恢复弹窗提交器：Hunger/Sanity 共用同一个实例（无内部状态），在装配点
        // 统一创建，保持 TaggedHudMessageService 的注入模式一致。
        var foodRestoreHud = new FoodRestoreHudMessenger(helper, hudMessages);
        var sanity = new Sanity(
            monitor,
            modId,
            configResolver,
            passOutReasonLedger,
            sanityStatusFeedbackSink,
            foodRestoreHud
        );
        var hunger = new Hunger(
            monitor,
            modId,
            hudMessages,
            playerDeathCauseLedger,
            hungerDamageModeProvider,
            hungerStatusFeedbackSink,
            foodRestoreHud
        );
        // 新生命值系统（20261112 设计方案）：依赖从现有参数取，不改 ModEntry。
        var health = new Health(monitor, modId, configResolver);
        hungerDebugController = hunger.DebugController;
        survivalTime = new SurvivalStateTimeOwner<Farmer>(
            timeApi,
            Game1.getOnlineFarmers,
            player => player.UniqueMultiplayerID == 0
                ? string.Empty
                : player.UniqueMultiplayerID.ToString(CultureInfo.InvariantCulture),
            message => monitor.Log(message, LogLevel.Error)
        );
        var stats = new List<IStat>
        {
            hunger,
            sanity,
            health,
        };
        foreach (var s in stats)
            s.Init(helper, survivalTime);

        return new StatManagerInitializeResult(sanity.Lifecycle, sanity, hunger);
    }
}
