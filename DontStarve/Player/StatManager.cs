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
using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using DontStarve.Player.Stats.Food.Category;
using DontStarve.Player.Stats.Health;
using DontStarve.Player.Stats.Hunger;
using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.PassOut;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player;

/// <summary>
/// Initialize 的组合结果。ModEntry 需要 Sanity/Hunger 实例仅为订阅状态反馈音效的
/// 结算/回放事件；Health 实例仅为装配黑暗袭击结算服务的「饥荒风格生命值」零头链路
/// （门控读取 + 零头交付，2026-11-13 决议）；除此之外的玩法接线不得借道本结果。
/// </summary>
internal sealed record StatManagerInitializeResult(
    SanitySystemLifecycleCoordinator Lifecycle,
    Sanity Sanity,
    Hunger Hunger,
    Health Health
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
        IHungerStatusFeedbackSink? hungerStatusFeedbackSink = null,
        Func<EnvironmentLightService?>? sanityLightServiceProvider = null,
        Func<int>? sanityScreenIdProvider = null
    )
    {
        // 食物分类层（20261113 设计稿阶段1）：foodtype + 度两张只读表，启动时加载一次。
        // 与 FoodRuleResolver 数值层（三张老 food.json）平行，互不覆盖；当前还没有消费点，
        // 后续烹饪/腐败/喂食轮直接查 CategoryTableLoader.IngredientTable / FoodTable。
        CategoryTableLoader.Load(helper, monitor);

        // 进食恢复弹窗提交器：Hunger/Sanity 共用同一个实例（无内部状态），在装配点
        // 统一创建，保持 TaggedHudMessageService 的注入模式一致。
        var foodRestoreHud = new FoodRestoreHudMessenger(helper, hudMessages);
        var sanity = new Sanity(
            monitor,
            modId,
            configResolver,
            passOutReasonLedger,
            sanityStatusFeedbackSink,
            foodRestoreHud,
            sanityLightServiceProvider,
            sanityScreenIdProvider
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

        return new StatManagerInitializeResult(sanity.Lifecycle, sanity, hunger, health);
    }
}
