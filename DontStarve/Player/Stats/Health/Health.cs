#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using DontStarve.Config;
using DontStarve.Player.Stats;
using DontStarve.Player.Stats.Food;
using DontStarve.Player.Stats.Health.HealthBehaviors;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using SObject = StardewValley.Object;

namespace DontStarve.Player.Stats.Health;

/// <summary>
/// 新生命值系统宿主（20261112 设计方案）。
/// 真值 = Farmer.health（原版 int，一字不改地当对外窗口）+ 余数（小数零头账本）。
///
/// 跟随者铁律（影怪兼容修复条款先例）：外部改动（怪物伤害、睡觉回血、升级、其它 mod）
/// 直接动 Farmer.health，不变量使真值自动吸收；本类绝不写「检测外部变化再折叠进账本」
/// 的代码（会双计）。UpdateTicking 只做 health vs lastSeen 的轻量对账游标维护：
/// 主机打去重 Debug 日志；客机把绝对值回报主机（见下）。
///
/// 我方效果走「事件落账」（决议 4）：效果 Δ 先 Truncate 取整落血、零头进余数、
/// |余数| ≥ 1 立即兑现；真值 ≤ 0 → 死亡只写 0 血、清 0 余数，然后什么都不调用，
/// 原版 Farmer.update 的 killScreen 链自己完成死亡（先例 SmapiHungerDamageExecutor 只写血），
/// 不新增任何死亡入口/界面/文案，不接入任何死亡原因台账。
///
/// 多人口径（决议 6 四轮定稿：主机权威）：结算只发生在主机——进食 doneEating 接缝
/// （vanilla 在主机对主机玩家与远程农场工都会跑，快照差值机制不变）+ 分钟装备结算
/// （SurvivalStateTimeOwner 逐在线玩家派发，此处仅加主机门）。余数持久化走 save 级
/// 抽屉 DontStarve.Health（HealthPersistenceStore，主机读写）。原版不同步 Farmer.health，
/// 血量底层状态长在各客机本地，因此主机对农场工的结算经 SmapiHealthMultiplayerAdapter
/// 下达余数/兑现（LedgerSync），客机执行落条并在本地判死；客机本地血量的外部变化经
/// ExternalReport 回报主机对账。健康客机从不结算（结构性无双计），v1 不引入饥饿的
/// 线上能力位（见 HealthMultiplayerProtocol 注释）。已知边界：split-screen 第二屏幕
/// 同为 IsMainPlayer，其下达同步会被协议门忽略（等同 v1 本地口径；多人时序整体
/// PendingRealMachine）。
///
/// 门控（决议 8）：「生命值效果平衡」门控负食扣血 + JSON 食物应用；「饥荒风格生命值」
/// 门控装备六槽效果 + 小数账本（关 = 整数模式）。resolver 不可用时按默认 true（默认开）。
/// 门控读取频率极低（进食 / 每游戏分钟），无性能压力。
/// </summary>
internal sealed class Health : IStat, ISurvivalStateTimeBehavior<Farmer>
{
    private const string FoodTablePath = "Asset/Health/food.json";

    private readonly IMonitor monitor;
    private readonly string modId;
    private readonly TypedConfigResolver? configResolver;
    private readonly Wearing wearing = new();
    // (delta, health) 二元组去重：只按 delta 去重会吞掉"同差额、不同绝对血量"的关键日志
    // （黑暗链取证时它把医院复活前后的外部变化合并成一条）。
    private readonly HashSet<(int Delta, int Health)> loggedExternalDeltas = new();
    private readonly HealthPersistenceStore persistenceStore = new();

    // 主机权威状态（决议 6）：所有玩家（含主机自己与所有农场工）的余数按
    // UniqueMultiplayerID 分键。客机不读存档，不持有该字典。
    private readonly Dictionary<string, double> remainderByPlayer = new(StringComparer.Ordinal);

    // 主机对各农场工的"血量视图"：客机回报的本地原版血量绝对值。原版不同步 health，
    // 主机上的农场工血量对象只是会漂移的投影，故农场工结算基准优先用该视图，缺失回退投影。
    private readonly Dictionary<string, int> lastKnownHealthByPlayer = new(StringComparer.Ordinal);

    // 客机只读影子：本地执行余数（等待主机 LedgerSync 下达），不落盘、不进存档。
    private double clientRemainder;

    private HealthPersistenceResult? persistenceSession;
    private SmapiHealthMultiplayerAdapter multiplayer = null!;
    private string loadedSaveIdentity = string.Empty;
    private string loggedPersistenceReason = string.Empty;
    private bool sessionLoaded;
    private int lastSeenHealth;
    private bool decimalModeActive;

    internal Health(IMonitor monitor, string modId, TypedConfigResolver? configResolver)
    {
        this.monitor = monitor;
        this.modId = modId;
        this.configResolver = configResolver;
    }

    public void Init(IModHelper helper, SurvivalStateTimeOwner<Farmer> survivalTime)
    {
        FoodRuleRuntime.SetHealth(LoadFoodTable(helper));
        wearing.Load(helper, monitor);
        survivalTime.Register(this);

        if (!SmapiHealthFoodCompletionPatch.TryInstall(modId, monitor, OnFoodSettled))
        {
            // 决议 6：主机补丁安装失败仅降级为主机自身效果缺失 + Warn，不静默；
            // 健康客机从不结算，不存在"客机提交请求"的旧路径，无需饥饿的线上能力位。
            monitor.Log(
                "健康进食结算不可用（Farmer.doneEating 补丁未安装）；负食扣血与 JSON 食物血量不生效。",
                LogLevel.Warn
            );
        }

        // 决议 10：tooltip 生命行小数显示，镜像进食补丁的安装点与失败纪律（Warn 不静默）。
        // 门控与结算同源：同一句 ShouldApplyFoodEffects(EnableHealthEffectBalance)，
        // 开关关 = tooltip 完全恢复原版行为（负食不补行、正食不注入小数）。
        if (
            !SmapiHealthTooltipPatch.TryInstall(
                modId,
                helper,
                monitor,
                () => HealthRules.ShouldApplyFoodEffects(ReadEnableHealthEffectBalance())
            )
        )
        {
            monitor.Log(
                "健康 tooltip 生命行补丁未安装；tooltip 维持原版显示（无小数、负食无生命行）。",
                LogLevel.Warn
            );
        }

        // 消息通道必须先于 SaveLoaded 订阅建立，保证读档播种时收发链路已就绪。
        multiplayer = new SmapiHealthMultiplayerAdapter(
            helper,
            monitor,
            modId,
            ApplyLedgerSyncFromHost,
            ApplyExternalReport
        );
        multiplayer.Initialize();

        helper.Events.GameLoop.SaveLoaded += (_, _) => Load(helper);
        helper.Events.GameLoop.Saving += (_, _) => Save(helper);
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => ClearSession();
        helper.Events.GameLoop.UpdateTicking += OnUpdateTicking;
        helper.Events.Multiplayer.PeerConnected += OnPeerConnected;
    }

    public void OnMinute(
        Farmer player,
        string playerKey,
        long minute,
        SurvivalStateTimePhase phase
    )
    {
        // 决议 6：分钟装备结算仅主机（镜像 Sanity.OnMinute 的角色门形态；Health 没有
        // changeService，SanityAuthorityRole.Host 本就由 Context.IsMainPlayer 派生，故直接
        // 用 Context.IsMainPlayer）。SurvivalStateTimeOwner 会逐在线玩家派发，这里不再只认
        // Game1.player。睡眠阶段与在床不结算（先例 Sanity.OnMinute）；health<=0 守卫保留
        // （农场工投影血量只是近似，误守卫的代价是漏一次结算而非错结算）。
        if (
            !HealthMultiplayerProtocol.ShouldHostSettleMinute(Context.IsMainPlayer)
            || phase != SurvivalStateTimePhase.Ordinary
            || player.isInBed.Value
            || player.health <= 0
        )
        {
            return;
        }

        SyncDecimalMode();

        // 决议 8 门控矩阵：装备六槽回血/掉血只由「饥荒风格生命值」门控。
        if (!HealthRules.ShouldApplyEquipmentEffects(ReadEnableDontStarveHealth()))
            return;

        SettleEvent(player, wearing.Update(player), "装备血量效果");
    }

    private void OnFoodSettled(Farmer who, Item? food, int appliedVanillaDelta)
    {
        // 决议 6：结算只发生在主机。vanilla doneEating 在主机对主机玩家与远程农场工都会跑，
        // 快照差值机制不变；在线校验镜像 HungerFoodCompletionPatch 形态。客机结构性不结算。
        // 死亡守卫：死亡后不再对尸体追加效果。
        if (
            !HealthMultiplayerProtocol.ShouldHostSettleFood(
                Context.IsMainPlayer,
                ReferenceEquals(Game1.GetPlayer(who.UniqueMultiplayerID, onlyOnline: true), who)
            )
            || who.health <= 0
        )
        {
            return;
        }
        if (food is not SObject obj)
            return;
        // 决议 8：负食扣血公式 + JSON 食物应用都由「生命值效果平衡」门控。
        if (!HealthRules.ShouldApplyFoodEffects(ReadEnableHealthEffectBalance()))
            return;

        // -300 = inedible：不能吃、不触发进食动画，天然豁免。edibility 一律走
        // SeedEdibilityRuntime 唯一权威（与种子守门同源，防重演 -750 能量事故）。
        var effectiveEdibility = SeedEdibilityRuntime.ResolveEffectiveEdibility(obj);
        if (effectiveEdibility == SeedEdibilityRules.NativeInedibleEdibility)
            return;

        SyncDecimalMode();

        // 优先级（决议 2）：特殊公式 > JSON 条目 > 默认。解析已抽到共享纯规则
        // HealthEffectResolver（决议 10 要求 A）：结算与 tooltip 从同一入口取同一
        // effectValue，永远说同一句话；事实抽取（含 JSON/特殊公式命中）见
        // HealthEffectFactsReader。体力镜像走 SeedEdibilityRuntime 唯一权威。
        var staminaMirror = SeedEdibilityRuntime.ResolveEffectiveStaminaRecovered(obj);
        var resolution = HealthEffectResolver.Resolve(
            HealthEffectFactsReader.Read(obj, effectiveEdibility, staminaMirror)
        );

        // 统一覆盖抵扣（决议 2）：correction = effectValue − 原版实际结算量（快照差值），
        // 公式单源在 HealthRules.CalculateFoodCorrection。快照差值天然覆盖 生命药水 (O)773 /
        // 星果 (O)434 / 滋补水 (O)349 / 虫肉块 (O)874 特例与 Math.Min 钳制，无需逐条复刻公式。
        // 负食分支下原版结算量恒为 0（Object.healthRecoveredOnConsumption 对负 edibility
        // 返回 0，Object.cs:3894-3896），故与旧「负食不减快照差值」写法数值完全一致；统一写法
        // 使四个来源共用同一契约。正食默认分支的刀前口径（体力侧 ceiling 不动、零头捡回账本）
        // 见 HealthRules 注释。
        var delta = HealthRules.CalculateFoodCorrection(
            resolution.EffectValue,
            appliedVanillaDelta
        );

        SettleEvent(who, delta, "食物血量效果");
    }

    /// <summary>
    /// 唯一结算出口（决议 6 落点分流）：
    /// - 主机自己：既有本地路径——写 who.health、更新权威字典自身条目、同步 lastSeenHealth。
    /// - 远程农场工（主机侧）：不写主机上的农场工血量对象（那只是投影）；以血量视图
    ///   （缺失回退投影）为基准算 NewHealth/NewRemainder，余数进权威字典，
    ///   healthDelta = 兑现的整数移动，经 LedgerSync 下达客机落条；死亡判定不在主机做。
    /// - 客机本地：只经 ApplyLedgerSyncFromHost 落条，从不主动结算。
    /// 「饥荒风格生命值」关 = 整数模式：效果向零取整直接落血/下达，余数恒 0。
    /// 死亡只标记，不调用任何死亡函数。
    /// </summary>
    private void SettleEvent(Farmer who, double delta, string sourceLabel)
    {
        if (!double.IsFinite(delta) || delta == 0)
            return;

        var isSelf = ReferenceEquals(who, Game1.player);
        var playerKey = KeyOf(who.UniqueMultiplayerID);

        if (!decimalModeActive)
        {
            if (isSelf)
            {
                var (integerHealth, integerLethal) = HealthLedgerRules.ApplyIntegerEvent(
                    who.health,
                    delta,
                    who.maxHealth
                );
                who.health = integerHealth;
                lastSeenHealth = integerHealth;
                if (integerLethal)
                    LogDeath(sourceLabel);
                return;
            }

            // 农场工整数模式：取整效果经消息下达（余数恒 0，延续权威字典双向清零语义）。
            var integerBase = ResolveFarmhandBaseHealth(playerKey, who);
            var (farmhandIntegerHealth, _) = HealthLedgerRules.ApplyIntegerEvent(
                integerBase,
                delta,
                who.maxHealth
            );
            multiplayer.SendLedgerSync(
                who.UniqueMultiplayerID,
                0d,
                farmhandIntegerHealth - integerBase
            );
            return;
        }

        if (isSelf)
        {
            var (newHealth, newRemainder, isLethal) = HealthLedgerRules.ApplyEvent(
                who.health,
                GetAuthorityRemainder(playerKey),
                delta,
                who.maxHealth
            );
            who.health = newHealth;
            SetAuthorityRemainder(playerKey, newRemainder);
            // 我方写入同步诊断游标，避免 UpdateTicking watcher 把自己的结算误报成外部改动。
            lastSeenHealth = newHealth;
            if (isLethal)
                LogDeath(sourceLabel);
            return;
        }

        // 农场工（主机权威）：基准优先用客机回报的血量视图而非投影（见字段注释）。
        var farmhandBase = ResolveFarmhandBaseHealth(playerKey, who);
        var (farmhandHealth, farmhandRemainder, farmhandLethal) = HealthLedgerRules.ApplyEvent(
            farmhandBase,
            GetAuthorityRemainder(playerKey),
            delta,
            who.maxHealth
        );
        SetAuthorityRemainder(playerKey, farmhandRemainder);
        lastKnownHealthByPlayer[playerKey] = farmhandHealth;
        multiplayer.SendLedgerSync(
            who.UniqueMultiplayerID,
            farmhandRemainder,
            farmhandHealth - farmhandBase
        );
        // farmhandLethal 只是主机视图的推断：农场工的死亡判定在客机本地执行（决议 6 冻结口径），
        // 主机不判死、不写投影血量、不打死亡日志，避免替客机触发死亡链路。
    }

    private void LogDeath(string sourceLabel)
    {
        // 决议 5：死亡不新增任何入口/界面/文案。血 0、余数 0 已写，这里不调用任何死亡函数，
        // 原版 Farmer.update 的 killScreen 链自己完成死亡；死因只进内部日志，玩家不可见，
        // 也刻意不接入任何死亡原因台账（避免牵动饥饿死亡台词替换）。
        monitor.Log($"生命值归零（来源：{sourceLabel}），交回原版死亡链路。", LogLevel.Info);
    }

    /// <summary>
    /// 「饥荒风格生命值」开关翻转检测（决议 8 / 设计 5.2）：开→关 清空余数（小数进度作废，
    /// 整数血量无损）；关→开 余数保持 0，真值以当前原版血量重新播种。主机清的是权威字典
    /// 整体（含所有农场工条目），客机清的是本地影子——延续既有双向清零语义。
    /// </summary>
    private void SyncDecimalMode()
    {
        var enabled = HealthRules.UseDecimalLedger(ReadEnableDontStarveHealth());
        if (enabled == decimalModeActive)
            return;

        // 开→关 清空余数；关→开 同样重播种为 0（设计 5.2：旧小数进度作废）——
        // 覆盖读档时可能残留的旧余数，防止中途重开开关时旧零头复活。
        remainderByPlayer.Clear();
        clientRemainder = 0;
        decimalModeActive = enabled;
    }

    /// <summary>
    /// 「饥荒风格生命值」小数账本门控（2026-11-13 决议）：供 ModEntry 装配黑暗袭击结算
    /// 服务时以闭包读取。先 SyncDecimalMode 刷新再返回，保证与 SettleEvent 的分流同源——
    /// 若两处各自读配置，开关翻转窗口内会出现"掷骰口径"与"落账口径"的分歧。
    /// </summary>
    internal bool ShouldUseDecimalLedger()
    {
        SyncDecimalMode();
        return decimalModeActive;
    }

    /// <summary>
    /// 黑暗袭击期望值零头交付（2026-11-13 决议）：主机在黑暗袭击结算后把 -0.05/次交给
    /// SettleEvent 统一落账——它已带主机自己/农场工分流、小数/整数模式分流、判死与
    /// LedgerSync 下达，这里不得重复实现。玩家不在线（取不到 Farmer）直接丢弃，代价
    /// ≤ 0.05。整数模式下 Truncate(-0.05)=0 天然无操作，这是防门控分歧的 fail-safe：
    /// darkness 门控与 Health 门控短暂不一致时不会产生整数血量抖动。
    /// </summary>
    internal void ApplyDecimalDamageFraction(long playerId, double fractionDelta)
    {
        SyncDecimalMode();
        var farmer = Game1.GetPlayer(playerId, onlyOnline: true);
        if (farmer is null)
            return;
        SettleEvent(farmer, fractionDelta, "黑暗袭击期望值零头");
    }

    internal (double Current, int Maximum) GetDebugSnapshot()
    {
        if (Game1.player is not { } player)
            return (0d, 0);

        SyncDecimalMode();
        var remainder = Context.IsMainPlayer
            ? GetAuthorityRemainder(KeyOf(player.UniqueMultiplayerID))
            : clientRemainder;
        return (player.health + remainder, player.maxHealth);
    }

    internal bool TrySetDebugValue(
        double requestedValue,
        out double currentValue,
        out int maximumValue,
        out bool isLethal
    )
    {
        currentValue = 0d;
        maximumValue = 0;
        isLethal = false;
        if (
            !Context.IsWorldReady
            || !Context.IsMainPlayer
            || !sessionLoaded
            || !double.IsFinite(requestedValue)
            || Game1.player is not { } player
        )
        {
            return false;
        }

        SyncDecimalMode();
        var result = HealthLedgerRules.SetDebugValue(
            requestedValue,
            player.maxHealth,
            decimalModeActive
        );
        player.health = result.NewHealth;
        SetAuthorityRemainder(KeyOf(player.UniqueMultiplayerID), result.NewRemainder);
        lastSeenHealth = result.NewHealth;
        if (result.IsLethal)
            LogDeath("控制台调试命令");

        currentValue = result.NewHealth + result.NewRemainder;
        maximumValue = player.maxHealth;
        isLethal = result.IsLethal;
        return true;
    }

    private void Load(IModHelper helper)
    {
        if (!TryGetSaveIdentity(out var saveIdentity))
        {
            ClearSession();
            return;
        }

        // Split-screen 的后续 screen 进入同一共享 mod 实例时只补播种，不重复读档
        //（镜像 Hunger.cs 的 saveIdentity 判断形态）。
        if (string.Equals(loadedSaveIdentity, saveIdentity, StringComparison.Ordinal))
        {
            SeedAfterLoad();
            return;
        }

        ClearSession();
        loadedSaveIdentity = saveIdentity;

        if (Context.IsMainPlayer)
        {
            persistenceSession = persistenceStore.Load(new SmapiHealthSaveDataAccess(helper.Data));
            LogPersistenceFailure();
            // 权威字典播种：读失败/解码失败 → 只读熔断（字典保持空、Saving 被跳过），
            // 绝不用默认值覆盖旧档。Missing（新档）→ 空字典：余数天然可从 0 播种、
            // 无旧数据可丢，故不熔断——与 hunger 熔断语义的差异及原因见 HealthPersistenceStore。
            if (persistenceSession.Remainders is not null)
            {
                foreach (var pair in persistenceSession.Remainders)
                    remainderByPlayer[pair.Key] = pair.Value;
            }

            if (persistenceSession.DroppedEntryKeys.Count > 0)
            {
                // 单条代价 ≤ 1 点血：丢弃为缺省 0，不熔断整个字典（决议 6 纪律，理由见 store）。
                monitor.Log(
                    $"Health save had unreadable remainder entries and they were dropped (keys: {string.Join(", ", persistenceSession.DroppedEntryKeys)}); each dropped entry costs at most 1 point of Health.",
                    LogLevel.Warn
                );
            }

            MigrateLegacyModDataRemainders();
        }
        else
        {
            // 客机不读主机存档，只建本地只读影子：余数等待主机 LedgerSync 下达。
            clientRemainder = 0;
        }

        decimalModeActive = HealthRules.UseDecimalLedger(ReadEnableDontStarveHealth());
        sessionLoaded = true;
        lastSeenHealth = Game1.player?.health ?? 0;
        loggedExternalDeltas.Clear();
        SeedAfterLoad();
    }

    private void Save(IModHelper helper)
    {
        // Saving 只由主机执行（决议 6；镜像 Hunger/Sanity 的主机写纪律）。
        if (!Context.IsMainPlayer || !sessionLoaded)
            return;

        // 整数模式（饥荒风格生命值关）：权威字典清空，Saving 写空字典
        //（决议 8：再开小数模式时余数从 0 重播种，防止旧零头复活）。
        if (!decimalModeActive)
            remainderByPlayer.Clear();

        if (persistenceSession is null)
        {
            monitor.Log(
                "Health save skipped because the persistence session was not loaded.",
                LogLevel.Error
            );
            return;
        }

        if (!persistenceSession.CanSave)
        {
            // 只读熔断：读失败/解码失败后的本次会话绝不写回，避免默认值覆盖旧档。
            LogPersistenceFailure();
            return;
        }

        persistenceSession = persistenceStore.Save(
            new SmapiHealthSaveDataAccess(helper.Data),
            persistenceSession,
            CaptureSaveData()
        );
        LogPersistenceFailure();
    }

    private HealthSaveData CaptureSaveData()
    {
        var data = new HealthSaveData();
        foreach (var pair in remainderByPlayer)
        {
            // 非有限值不写：宁可丢余数（代价 ≤ 1 点血），不把 NaN/Infinity 写进存档。
            if (double.IsFinite(pair.Value))
                data.Remainders[pair.Key] = HealthLedgerRules.FormatRemainder(pair.Value);
        }
        return data;
    }

    /// <summary>只读熔断 Warn（去重，任务纪律：读失败/解码失败可见、不静默、不写回）。</summary>
    private void LogPersistenceFailure()
    {
        if (persistenceSession is null)
            return;
        if (persistenceSession.Capability != HealthPersistenceCapability.ReadOnly)
            return;
        if (
            string.Equals(
                loggedPersistenceReason,
                persistenceSession.Reason,
                StringComparison.Ordinal
            )
        )
        {
            return;
        }

        loggedPersistenceReason = persistenceSession.Reason;
        monitor.Log(
            $"Health persistence entered read-only mode: {persistenceSession.Reason}",
            LogLevel.Warn
        );
    }

    private void SeedAfterLoad()
    {
        // 决议 6 播种：主机向每个在线农场工下达 healthDelta=0 的余数初值；
        // 客机把本地血量初值上报主机对账（血量不在存档通道里，只能双向补种）。
        if (!sessionLoaded)
            return;

        if (Context.IsMainPlayer)
        {
            var localId = Game1.player?.UniqueMultiplayerID ?? 0;
            foreach (var farmer in Game1.getOnlineFarmers())
            {
                if (farmer is null || farmer.UniqueMultiplayerID == localId)
                    continue;

                multiplayer.SendLedgerSync(
                    farmer.UniqueMultiplayerID,
                    GetAuthorityRemainder(KeyOf(farmer.UniqueMultiplayerID)),
                    0
                );
            }
        }
        else if (Game1.player is { } local)
        {
            multiplayer.SendExternalReport(local.UniqueMultiplayerID, local.health);
        }
    }

    private void OnPeerConnected(object? sender, PeerConnectedEventArgs e)
    {
        // 决议 6：主机与对端连接建立后播种——先尝试并入该玩家旧版 modData 余数
        //（新档 / 尚无 save 抽屉数据时避免静默丢数据），再下达 healthDelta=0 的余数初值。
        // 客机的血量初值由其 SaveLoaded 的 ExternalReport 上报。
        if (!sessionLoaded || !Context.IsMainPlayer)
            return;

        MigrateLegacyModDataRemainder(Game1.GetPlayer(e.Peer.PlayerID, onlyOnline: true));
        multiplayer.SendLedgerSync(
            e.Peer.PlayerID,
            GetAuthorityRemainder(KeyOf(e.Peer.PlayerID)),
            0
        );
    }

    private void MigrateLegacyModDataRemainders()
    {
        foreach (var farmer in Game1.getOnlineFarmers())
            MigrateLegacyModDataRemainder(farmer);
    }

    /// <summary>
    /// 一次性迁移旧版 modData 余数（HealthRemainderStore 时代，key DontStarve.Health.Remainder）
    /// 到权威字典：旧版把余数存在各自玩家的 modData（随存档且联机同步），主机 SaveLoaded /
    /// PeerConnected 时可读到。迁移纪律：
    /// - 权威字典已有该玩家条目（save 抽屉数据更新）时不并入，防止旧值覆盖新值；
    /// - 主机自己的 key 并入后移除；农场工的 key 只并入不移除——modData 是 NetStringDictionary，
    ///   主机写农场工对象有同步归属风险，残留 key 已无任何读取路径，属无害死数据。
    /// </summary>
    private void MigrateLegacyModDataRemainder(Farmer? farmer)
    {
        if (farmer is null)
            return;

        var playerKey = KeyOf(farmer.UniqueMultiplayerID);
        if (remainderByPlayer.ContainsKey(playerKey))
            return;

        if (
            !farmer.modData.TryGetValue(HealthPersistenceStore.LegacyModDataKey, out var raw)
            || string.IsNullOrWhiteSpace(raw)
        )
        {
            return;
        }

        if (!HealthLedgerRules.TryParseRemainder(raw, out var value) || !double.IsFinite(value))
        {
            monitor.Log(
                $"Legacy health remainder data was unreadable ({HealthPersistenceStore.LegacyModDataKey}={raw}) for farmer {playerKey}; dropped. Cost is at most 1 point of Health.",
                LogLevel.Warn
            );
            return;
        }

        remainderByPlayer[playerKey] = value;
        if (ReferenceEquals(farmer, Game1.player))
            farmer.modData.Remove(HealthPersistenceStore.LegacyModDataKey);
        monitor.Log(
            $"Legacy health remainder migrated into the authoritative ledger for farmer {playerKey}.",
            LogLevel.Info
        );
    }

    private void ClearSession()
    {
        sessionLoaded = false;
        decimalModeActive = false;
        remainderByPlayer.Clear();
        lastKnownHealthByPlayer.Clear();
        clientRemainder = 0;
        persistenceSession = null;
        loadedSaveIdentity = string.Empty;
        loggedPersistenceReason = string.Empty;
        lastSeenHealth = 0;
        loggedExternalDeltas.Clear();
        multiplayer?.ResetSessionDiagnostics();
    }

    private void OnUpdateTicking(object? sender, UpdateTickingEventArgs e)
    {
        // 每 tick 只做一次 int 读比较（性能底线：无分配、无扫描、常态无日志）。
        if (!sessionLoaded)
            return;

        var player = Game1.player;
        if (player is null || player.health == lastSeenHealth)
            return;

        var delta = player.health - lastSeenHealth;
        lastSeenHealth = player.health;

        if (Context.IsMainPlayer)
        {
            // 主机：外部改动零折叠——怪物伤害/睡觉回血/升级/其它 mod 都直接动 Farmer.health，
            // 真值不变量自动吸收；这里绝不把差额写进余数（会双计）。只按 delta 值去重打一条
            // Debug 日志（影怪兼容修复条款 6 的先例）；我方结算已同步 lastSeenHealth，不会走到这里。
            if (decimalModeActive && loggedExternalDeltas.Add((delta, player.health)))
            {
                monitor.Log(
                    $"外部血量变化 {delta:+0;-0} 已被真值不变量自动吸收（health={player.health}, 余数={GetAuthorityRemainder(KeyOf(player.UniqueMultiplayerID))}）。",
                    LogLevel.Debug
                );
            }
            return;
        }

        // 客机：血量底层状态在本地而主机只见投影（决议 6 结构性差异）——外部变化（被怪打、
        // 其它 mod、原版整数刀）以绝对值回报主机对账。我方 LedgerSync 落条已同步
        // lastSeenHealth，不会走到这里；血量变化是离散事件（受击/治疗），消息频率可接受。
        multiplayer.SendExternalReport(player.UniqueMultiplayerID, player.health);
    }

    private void ApplyLedgerSyncFromHost(long targetPlayerId, double remainder, int healthDelta)
    {
        // 客机执行（决议 6）：兑现落条 = clamp(本地血 + healthDelta)；本地执行余数 = 主机权威值。
        var player = Game1.player;
        if (player is null || player.UniqueMultiplayerID != targetPlayerId)
            return;

        var newHealth = Math.Clamp(player.health + healthDelta, 0, player.maxHealth);
        player.health = newHealth;
        clientRemainder = remainder;
        // 我方落条变化同步对账游标，避免 watcher 把自己执行的下达误报成外部变化回传主机。
        lastSeenHealth = newHealth;

        if (newHealth + clientRemainder <= 0)
        {
            // 死亡判定在血量所在机器（决议 6 冻结口径）：真值 ≤ 0 → 本地写 0 血 + 余数 0，
            // 交回该客机本地的原版死亡链路；不新增死亡入口。
            player.health = 0;
            clientRemainder = 0;
            LogDeath("主机下达的账本兑现");
        }
    }

    private void ApplyExternalReport(long reporterPlayerId, int health)
    {
        // 主机收到农场工血量回报：只更新权威侧"血量视图"（后续农场工结算的 ApplyEvent 基准）。
        // 外部变化本身零折叠——余数不动，与本地同一铁律（外部改动是整数、直接落在客机血量上，
        // 真值 = 血量视图 + 余数自动吸收）。
        if (health < 0)
        {
            // 血量不可能是负数：异常回报丢弃 + Warn，不静默。
            monitor.Log(
                $"External health report was negative ({health}) for farmer {reporterPlayerId}; dropped.",
                LogLevel.Warn
            );
            return;
        }

        lastKnownHealthByPlayer[KeyOf(reporterPlayerId)] = health;
    }

    private int ResolveFarmhandBaseHealth(string playerKey, Farmer who)
    {
        // 原版不同步 health：主机投影会漂移。客机回报过的绝对值（血量视图）优先；
        // 从未回报（刚加入 / 旧版本共存）时回退投影，误差由下一轮回报收敛。
        return lastKnownHealthByPlayer.TryGetValue(playerKey, out var view)
            ? view
            : who.health;
    }

    private double GetAuthorityRemainder(string playerKey)
    {
        return remainderByPlayer.TryGetValue(playerKey, out var value) ? value : 0;
    }

    private void SetAuthorityRemainder(string playerKey, double value)
    {
        // 非有限值不入账（宁可丢零头不污染权威字典）；ApplyEvent 的输出本就有限，此处兜底。
        if (double.IsFinite(value))
            remainderByPlayer[playerKey] = value;
    }

    private static bool TryGetSaveIdentity(out string saveIdentity)
    {
        var master = Game1.MasterPlayer;
        if (master is null)
        {
            saveIdentity = string.Empty;
            return false;
        }

        saveIdentity = string.Concat(
            Game1.uniqueIDForThisGame.ToString(CultureInfo.InvariantCulture),
            ":",
            master.UniqueMultiplayerID.ToString(CultureInfo.InvariantCulture)
        );
        return true;
    }

    private static string KeyOf(long playerId) =>
        playerId.ToString(CultureInfo.InvariantCulture);

    private bool ReadEnableDontStarveHealth()
    {
        return ReadBoolean(ConfigKeys.EnableDontStarveHealth);
    }

    private bool ReadEnableHealthEffectBalance()
    {
        return ReadBoolean(ConfigKeys.EnableHealthEffectBalance);
    }

    // resolver 不可用或键无值时按默认 true（两个开关默认开）。
    private bool ReadBoolean(string key)
    {
        if (configResolver is null)
            return true;

        var resolution = configResolver.GetBoolean(key);
        return resolution.HasValue ? resolution.Value : true;
    }

    private IReadOnlyDictionary<string, double> LoadFoodTable(IModHelper helper)
    {
        try
        {
            var raw = helper.ModContent.Load<Dictionary<string, double>>(FoodTablePath);
            if (raw is not null)
                return new Dictionary<string, double>(raw, StringComparer.Ordinal);

            monitor.Log(
                $"Health food table unavailable ({FoodTablePath}); using an empty table.",
                LogLevel.Warn
            );
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"Health food table failed closed ({FoodTablePath}; {exception.GetType().Name}); using an empty table.",
                LogLevel.Warn
            );
        }

        return new Dictionary<string, double>(StringComparer.Ordinal);
    }
}
