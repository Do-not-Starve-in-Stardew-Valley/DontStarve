#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using DontStarve.Display.UIElements;
using DontStarve.Player.Stats.Food;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using SObject = StardewValley.Object;

namespace DontStarve.Player.Stats.Health;

/// <summary>
/// 决议 10：tooltip 生命行小数显示。目标接缝（行号按 1.6.15 反编译钉死）：
/// - IClickableMenu.drawHoverText(SpriteBatch, StringBuilder, SpriteFont, ...) 的
///   能量/生命行组装区（IClickableMenu.cs:1288-1317，两接缝所在方法）；
/// - 同方法的高度计算三元式（IClickableMenu.cs:1114，只给正数生命行预留高度）。
///
/// 原方法体中部无法用 prefix/postfix 改值，故采用窄 transpiler（与
/// SmapiVanillaSanityTooltipService 的 ReplaceVanillaTooltipEdibilityReads 同一形态：
/// 锚定原版指令序列、替换/插入对静态 helper 的单次调用），共三个接缝：
/// 1. 正食生命行注入值替换：`LoadString("Strings\UI:ItemHover_Health", "+" + num10)`
///    的实参表达式换成 FormatPositiveHealthRowText —— 原位原样（同行/同图标/同模板），
///    只把注入数字换成真值小数（如 +103.5）；width 采样点（:1119）无 "+" 拼接，不匹配。
/// 2. 高度联动：负食补行时在高度三元式落位后追加 +40（VanillaSurvivalRowHeightReserve）；
///    与接缝 3 经同一 TryPlanNegativeFoodRow 联动，两处一致修改（加契约注释互相引用）。
/// 3. 负食补行：原版负食分支（:1311-1316）只画负能量行，在其行尾插入
///    DrawNegativeFoodHealthRow —— 复用正食生命行同款图标 (0,438,10,10)×3 与模板，
///    注入负值（对齐原版负能量行的无 + 风格，如 -22.5）。
///
/// 纯投影契约：本补丁只读本地数据（本地 JSON 表 + 本地效果解析），不改任何游戏状态、
/// 不写血、不碰余数账本；edibility 一律走 SeedEdibilityRuntime（与种子守门同源，
/// 防重演 -750 能量事故）；格式化走 LocalizedValueFormatter.TryFormatSigned
/// （与饥饿行小数先例 +62.5 逐字符对齐）。任何解析失败/开关关闭都退回原版串或
/// 不插行——宁可保持原版也不错改。安装失败 Warn 不静默（镜像进食补丁纪律）。
/// </summary>
internal static class SmapiHealthTooltipPatch
{
    internal const string OwnerSuffix = ".Stats.Health.Tooltip";

    private const string HealthRowTemplateKey = "Strings\\UI:ItemHover_Health";
    private const string EnergyRowTemplateKey = "Strings\\UI:ItemHover_Energy";
    private const string VanillaPlusSign = "+";
    private const string VanillaZeroPercentToken = "0";

    // 与 SmapiVanillaSanityTooltipService 同源验证过的 1.6.15 final-overload 局部变量布局：
    // 1 = 宽度 num、2 = 高度 num2、5 = x num5、6 = y num6。
    private const int WidthLocalIndex = 1;
    private const int HeightLocalIndex = 2;
    private const int XLocalIndex = 5;
    private const int YLocalIndex = 6;
    private const int BuffIconsArgumentIndex = 8;
    private const int HoveredItemArgumentIndex = 9;

    // 高度三元式从 callvirt healthRecoveredOnConsumption 到 stloc 高度局部变量的窗口上限；
    // 纯防御 IL 形态漂移，正常编译约 8 条指令。
    private const int HeightStoreSearchWindow = 24;

    private const int MaximumLoggedFailures = 16;

    private static readonly HashSet<string> loggedReasons = new(StringComparer.Ordinal);
    private static IMonitor? monitor;
    private static Harmony? harmony;
    private static string ownerId = string.Empty;
    private static Func<bool>? foodEffectsGate;
    private static CultureInfo tooltipCulture = CultureInfo.InvariantCulture;

    internal static bool IsInstalled => harmony is not null;

    internal static bool TryInstall(
        string modId,
        IModHelper helper,
        IMonitor log,
        Func<bool> gate
    )
    {
        if (harmony is not null)
        {
            if (
                string.Equals(
                    ownerId,
                    string.Concat(modId, OwnerSuffix),
                    StringComparison.Ordinal
                )
            )
            {
                // 同 owner 重入只换门控/监视器/文化，不重复 patch（镜像 SmapiHealthFoodCompletionPatch 形态）。
                foodEffectsGate = gate;
                monitor = log;
                tooltipCulture = LocalizedValueFormatter.ResolveCulture(helper.Translation.Locale);
                return true;
            }

            log.Log(
                "Health tooltip patch is already owned by a different mod ID.",
                LogLevel.Error
            );
            return false;
        }

        return Install(modId, helper, log, gate);
    }

    private static bool Install(
        string modId,
        IModHelper helper,
        IMonitor log,
        Func<bool> gate
    )
    {
        var candidateOwnerId = string.Concat(modId, OwnerSuffix);
        var target = AccessTools.DeclaredMethod(
            typeof(IClickableMenu),
            nameof(IClickableMenu.drawHoverText),
            new[]
            {
                typeof(SpriteBatch),
                typeof(StringBuilder),
                typeof(SpriteFont),
                typeof(int),
                typeof(int),
                typeof(int),
                typeof(string),
                typeof(int),
                typeof(string[]),
                typeof(Item),
                typeof(int),
                typeof(string),
                typeof(int),
                typeof(int),
                typeof(int),
                typeof(float),
                typeof(CraftingRecipe),
                typeof(IList<Item>),
                typeof(Texture2D),
                typeof(Rectangle?),
                typeof(Color?),
                typeof(Color?),
                typeof(float),
                typeof(int),
                typeof(int),
            }
        );
        var parameters = target?.GetParameters();
        if (
            target is null
            || !target.IsStatic
            || target.ReturnType != typeof(void)
            || parameters is null
            || parameters.Length != 25
            || !string.Equals(parameters[1].Name, "text", StringComparison.Ordinal)
            || parameters[1].ParameterType != typeof(StringBuilder)
            || !string.Equals(
                parameters[BuffIconsArgumentIndex].Name,
                "buffIconsToDisplay",
                StringComparison.Ordinal
            )
            || parameters[BuffIconsArgumentIndex].ParameterType != typeof(string[])
            || !string.Equals(
                parameters[HoveredItemArgumentIndex].Name,
                "hoveredItem",
                StringComparison.Ordinal
            )
            || parameters[HoveredItemArgumentIndex].ParameterType != typeof(Item)
        )
        {
            log.Log(
                "Health tooltip patch is unavailable (reason=health.tooltip.final-overload-signature-drift).",
                LogLevel.Warn
            );
            return false;
        }

        if (!HasExpectedLocalLayout(target))
        {
            log.Log(
                "Health tooltip patch is unavailable (reason=health.tooltip.final-overload-local-layout-drift).",
                LogLevel.Warn
            );
            return false;
        }

        var transpiler = AccessTools.DeclaredMethod(
            typeof(SmapiHealthTooltipPatch),
            nameof(TranspileDrawHoverText)
        );
        if (transpiler is null)
        {
            log.Log(
                "Health tooltip patch is unavailable (reason=health.tooltip.transpiler-method-missing).",
                LogLevel.Warn
            );
            return false;
        }

        var candidateHarmony = new Harmony(candidateOwnerId);
        try
        {
            candidateHarmony.Patch(
                target,
                transpiler: new HarmonyMethod(transpiler)
            );
            if (!IsOwnedTranspilerInstalled(target, candidateOwnerId))
            {
                candidateHarmony.Unpatch(
                    target,
                    HarmonyPatchType.Transpiler,
                    candidateOwnerId
                );
                log.Log(
                    "Health tooltip patch is unavailable (reason=health.tooltip.patch-owner-readback-failed).",
                    LogLevel.Warn
                );
                return false;
            }

            harmony = candidateHarmony;
            ownerId = candidateOwnerId;
            monitor = log;
            foodEffectsGate = gate;
            tooltipCulture = LocalizedValueFormatter.ResolveCulture(helper.Translation.Locale);
            helper.Events.Content.LocaleChanged += OnLocaleChanged;
            log.Log("Health tooltip patch installed.", LogLevel.Trace);
            return true;
        }
        catch (Exception exception)
        {
            try
            {
                candidateHarmony.Unpatch(target, HarmonyPatchType.All, candidateOwnerId);
            }
            catch (Exception cleanupException)
            {
                log.Log(
                    $"Health tooltip patch partial cleanup failed ({cleanupException.GetType().Name}: {cleanupException.Message}).",
                    LogLevel.Warn
                );
            }

            // 打最内层异常：外层 TargetInvocationException/HarmonyException 只会说"目标调用了
            // 一次"，真正的锚点失配信息在 InnerException 里（2026-11-13 实机教训：日志只记
            // 外层导致多查一轮）。
            var innermost = exception;
            while (innermost.InnerException is { } deeper)
            {
                innermost = deeper;
            }

            log.Log(
                $"Health tooltip patch failed ({innermost.GetType().Name}: {innermost.Message}); the tooltip keeps the vanilla display.",
                LogLevel.Warn
            );
            log.Log($"Health tooltip patch failure chain: {exception}", LogLevel.Debug);
            return false;
        }
    }

    // ── 接缝 1：正食生命行注入值（决议 10：原位原样，只换数字） ──────────────────

    /// <summary>
    /// 原版 `LoadString("Strings\UI:ItemHover_Health", "+" + num10)` 的实参替换点。
    /// 返回值直接进原版模板，因此正食保留原版 "+" 前缀风格（+103.5）；
    /// ≥999 的 " 100%" 特例在原版三元式更早处分流，根本到不了本方法，天然维持原版。
    /// </summary>
    internal static string FormatPositiveHealthRowText(Item? hoveredItem, int vanillaHealthValue)
    {
        // 原版回退串与 IClickableMenu.cs:1307 的 "+" + num10 逐字节等价（同为当前文化 ToString）。
        var fallback = string.Concat(VanillaPlusSign, vanillaHealthValue.ToString());

        if (!IsInjectionActive() || hoveredItem is not SObject obj)
            return fallback;

        try
        {
            var effectiveEdibility = SeedEdibilityRuntime.ResolveEffectiveEdibility(obj);
            // -300 资格门在原版 :1111 已排除，这里再挡一次为防御；负食生命行走补行接缝，
            // 不经本注入。
            if (
                effectiveEdibility == SeedEdibilityRules.NativeInedibleEdibility
                || effectiveEdibility < 0
            )
            {
                return fallback;
            }

            var staminaMirror = SeedEdibilityRuntime.ResolveEffectiveStaminaRecovered(obj);
            var resolution = HealthEffectResolver.Resolve(
                HealthEffectFactsReader.Read(obj, effectiveEdibility, staminaMirror)
            );

            switch (resolution.Source)
            {
                case HealthEffectSource.SpecialFormula:
                case HealthEffectSource.JsonEntry:
                    // tooltip 与结算说同一句话：显示完整效果真值（含小数；JSON 值为负则带负号）。
                    return FormatEffectValue(resolution.EffectValue, fallback);
                case HealthEffectSource.KnifeBefore:
                    // 决议 10：特例物品维持原版 tooltip（虫肉块 (O)874 ×0.68 等）。原版数字
                    // 若不是普通公式产物，说明原版走了特例分支，注入会改写原版怪癖 → 退回原版。
                    if (
                        !HealthTooltipRules.IsVanillaPlainFormulaValue(
                            vanillaHealthValue,
                            staminaMirror
                        )
                    )
                    {
                        return fallback;
                    }

                    return FormatEffectValue(resolution.EffectValue, fallback);
                default:
                    return fallback;
            }
        }
        catch (Exception exception)
        {
            LogOnce(
                $"health.tooltip.format-positive:{exception.GetType().Name}",
                LogLevel.Warn,
                exception.Message
            );
            return fallback;
        }
    }

    // ── 接缝 2 + 3：负食补行与高度联动（两处必须一致修改，共用同一计划函数） ─────────

    /// <summary>
    /// 高度接缝（IClickableMenu.cs:1114 三元式落位后调用）：负食补行时多预留一行高度。
    /// 契约：与 DrawNegativeFoodHealthRow 共用 TryPlanNegativeFoodRow——原版高度三元式只给
    /// 正数生命行预留高度，本补丁不改它、只在补行成立时追加，否则负食行文字会溢出框。
    /// </summary>
    internal static int AddNegativeFoodRowHeight(int height, Item? hoveredItem)
    {
        return TryPlanNegativeFoodRow(hoveredItem, out _)
            ? height + HealthTooltipRules.VanillaSurvivalRowHeightReserve
            : height;
    }

    /// <summary>
    /// 负食补行绘制（原版负食分支行尾调用）：复用正食生命行同款图标与模板，注入负值。
    /// 画法逐像素镜像 IClickableMenu.cs:1306-1308 的原版正食生命行（含其忽略 alpha 的
    /// 原版行为），推进 34px 与原版能量/生命行同节奏。
    /// </summary>
    internal static void DrawNegativeFoodHealthRow(
        SpriteBatch spriteBatch,
        Item? hoveredItem,
        SpriteFont font,
        int x,
        ref int y
    )
    {
        if (!TryPlanNegativeFoodRow(hoveredItem, out var formattedValue))
            return;

        try
        {
            Utility.drawWithShadow(
                spriteBatch,
                Game1.mouseCursors,
                new Vector2(x + 16 + 4, y + 16),
                new Rectangle(0, 438, 10, 10),
                Color.White,
                0f,
                Vector2.Zero,
                3f,
                flipped: false,
                0.95f
            );
            Utility.drawTextWithShadow(
                spriteBatch,
                Game1.content.LoadString(HealthRowTemplateKey, formattedValue),
                font,
                new Vector2(x + 16 + 34 + 4, y + 16),
                Game1.textColor
            );
            y += HealthTooltipRules.VanillaSurvivalRowAdvance;
        }
        catch (Exception exception)
        {
            LogOnce(
                $"health.tooltip.draw-negative:{exception.GetType().Name}",
                LogLevel.Warn,
                exception.Message
            );
        }
    }

    /// <summary>
    /// 负食补行计划函数：高度与绘制共用的唯一判定口（两处一致修改的落点）。
    /// -300 豁免、开关关闭、解析失败、真值四舍五入为 0 时都不补行（"0 生命值" 是噪声行，
    /// 与结算 delta 落 0 不动账一致）。
    /// </summary>
    private static bool TryPlanNegativeFoodRow(Item? hoveredItem, out string formattedValue)
    {
        formattedValue = string.Empty;
        if (!IsInjectionActive() || hoveredItem is not SObject obj)
            return false;

        try
        {
            var effectiveEdibility = SeedEdibilityRuntime.ResolveEffectiveEdibility(obj);
            var staminaMirror = SeedEdibilityRuntime.ResolveEffectiveStaminaRecovered(obj);
            if (
                !HealthTooltipRules.ShouldAppendNegativeFoodHealthRow(
                    effectiveEdibility,
                    staminaMirror
                )
            )
            {
                return false;
            }

            var resolution = HealthEffectResolver.Resolve(
                HealthEffectFactsReader.Read(obj, effectiveEdibility, staminaMirror)
            );
            if (resolution.Source == HealthEffectSource.None)
                return false;

            if (
                !LocalizedValueFormatter.TryFormatSigned(
                    resolution.EffectValue,
                    tooltipCulture,
                    out formattedValue
                )
            )
            {
                return false;
            }

            return !string.Equals(
                formattedValue,
                VanillaZeroPercentToken,
                StringComparison.Ordinal
            );
        }
        catch (Exception exception)
        {
            LogOnce(
                $"health.tooltip.plan-negative:{exception.GetType().Name}",
                LogLevel.Warn,
                exception.Message
            );
            formattedValue = string.Empty;
            return false;
        }
    }

    private static string FormatEffectValue(double effectValue, string fallback)
    {
        // 格式与既有饥饿行小数先例（实图 +62.5 饥饿值）完全一致：
        // LocalizedValueFormatter.TryFormatSigned（"+0.##;-0.##;0"，两位内小数、去尾零、
        // 正数带 +、负数无 +、零为 "0"），文化随 SMAPI locale。
        return LocalizedValueFormatter.TryFormatSigned(effectValue, tooltipCulture, out var formatted)
            ? formatted
            : fallback;
    }

    private static bool IsInjectionActive()
    {
        // 门控镜像 Health.cs 的 ReadBoolean 默认开语义：门未就绪视为关（退回原版，不猜测）。
        var gate = foodEffectsGate;
        return gate is not null && gate();
    }

    // ── transpiler：三个接缝的 IL 锚定与替换 ─────────────────────────────────────

    private static IEnumerable<CodeInstruction> TranspileDrawHoverText(
        IEnumerable<CodeInstruction> instructions
    )
    {
        // IL 事实（2026-11-13 Cecil 转储核实）：drawHoverText 里两处 callvirt 的方法引用都是
        // 基类虚槽 Item::healthRecoveredOnConsumption —— C# 对 override 发 callvirt 时绑定
        // 声明槽，与反编译源写 obj.xxx 与否无关。锚定基类版本，Object 版留作版本漂移回退。
        var healthRecoveredFromItemMethod =
            AccessTools.Method(
                typeof(Item),
                nameof(Item.healthRecoveredOnConsumption)
            )
            ?? throw new InvalidOperationException(
                "Health tooltip seam is unavailable (Item.healthRecoveredOnConsumption missing)."
            );
        var healthRecoveredFromObjectMethod =
            AccessTools.DeclaredMethod(
                typeof(SObject),
                nameof(SObject.healthRecoveredOnConsumption)
            )
            ?? throw new InvalidOperationException(
                "Health tooltip seam is unavailable (Object.healthRecoveredOnConsumption missing)."
            );
        var formatMethod =
            AccessTools.DeclaredMethod(
                typeof(SmapiHealthTooltipPatch),
                nameof(FormatPositiveHealthRowText)
            )
            ?? throw new InvalidOperationException(
                "Health tooltip seam is unavailable (format helper missing)."
            );
        var addHeightMethod =
            AccessTools.DeclaredMethod(
                typeof(SmapiHealthTooltipPatch),
                nameof(AddNegativeFoodRowHeight)
            )
            ?? throw new InvalidOperationException(
                "Health tooltip seam is unavailable (height helper missing)."
            );
        var drawRowMethod =
            AccessTools.DeclaredMethod(
                typeof(SmapiHealthTooltipPatch),
                nameof(DrawNegativeFoodHealthRow)
            )
            ?? throw new InvalidOperationException(
                "Health tooltip seam is unavailable (draw helper missing)."
            );
        var intToStringMethod =
            AccessTools.Method(typeof(int), "ToString", Type.EmptyTypes)
            ?? throw new InvalidOperationException(
                "Health tooltip seam is unavailable (int.ToString missing)."
            );
        var stringConcatMethod =
            AccessTools.Method(
                typeof(string),
                nameof(string.Concat),
                new[] { typeof(string), typeof(string) }
            )
            ?? throw new InvalidOperationException(
                "Health tooltip seam is unavailable (string.Concat missing)."
            );

        var matcher = new CodeMatcher(instructions);

        // 接缝 2（高度联动）：第一个 healthRecoveredOnConsumption 调用是高度三元式内的
        // 行数判定（:1114）；找到其后第一个高度局部变量落位指令，在落位后追加 +40。
        matcher.Start();
        if (
            !matcher
                .MatchStartForward(
                    new CodeMatch(OpCodes.Callvirt, healthRecoveredFromItemMethod)
                )
                .IsValid
        )
        {
            matcher
                .Start()
                .MatchStartForward(
                    new CodeMatch(OpCodes.Callvirt, healthRecoveredFromObjectMethod)
                )
                .ThrowIfNotMatch(
                    "Health tooltip height seam is unavailable (height row-count call missing)."
                );
        }
        AdvanceToNextLocalStore(
            matcher,
            HeightLocalIndex,
            HeightStoreSearchWindow,
            "Health tooltip height seam is unavailable (height local store missing)."
        );
        matcher.Advance(1);
        matcher.InsertAndAdvance(
            new[]
            {
                LoadLocal(HeightLocalIndex),
                LoadArgument(HoveredItemArgumentIndex),
                new CodeInstruction(OpCodes.Call, addHeightMethod),
                StoreLocal(HeightLocalIndex),
            }
        );

        // 接缝 3（负食补行）：负能量行的 LoadString 实参是 num9.ToString()（无 "+" 拼接），
        // 借此与正能量行区分；在其 34px 行尾推进落位后插入补行绘制。
        matcher
            .Start()
            .MatchStartForward(
                new CodeMatch(OpCodes.Ldstr, EnergyRowTemplateKey),
                new CodeMatch(OpCodes.Ldloca_S),
                new CodeMatch(OpCodes.Call, intToStringMethod)
            )
            .ThrowIfNotMatch(
                "Health tooltip negative-row seam is unavailable (vanilla negative energy row missing)."
            );
        matcher
            .MatchStartForward(
                new CodeMatch(OpCodes.Ldloc_S),
                new CodeMatch(
                    OpCodes.Ldc_I4_S,
                    (sbyte)HealthTooltipRules.VanillaSurvivalRowAdvance
                ),
                new CodeMatch(OpCodes.Add),
                new CodeMatch(OpCodes.Stloc_S)
            )
            .ThrowIfNotMatch(
                "Health tooltip negative-row seam is unavailable (vanilla row advance missing)."
            );
        if (
            !IsLocalInstruction(matcher.Instruction, OpCodes.Ldloc_S, YLocalIndex)
            || !IsLocalInstruction(
                matcher.InstructionAt(3),
                OpCodes.Stloc_S,
                YLocalIndex
            )
        )
        {
            throw new InvalidOperationException(
                "Health tooltip negative-row seam is unavailable (row advance targets a different local)."
            );
        }

        matcher.Advance(4);
        matcher.InsertAndAdvance(
            new[]
            {
                LoadArgument(0),
                LoadArgument(HoveredItemArgumentIndex),
                LoadArgument(2),
                LoadLocal(XLocalIndex),
                LoadLocalAddress(YLocalIndex),
                new CodeInstruction(OpCodes.Call, drawRowMethod),
            }
        );

        // 接缝 1（正食注入值）：模板 ldstr 后紧跟 "+" 拼接组的只有原版生命行组装点
        // （:1307）；:1119 的宽度采样点其后是 num4 + box，天然不匹配。把
        // [ldstr "+", ldloca num10, call ToString, call Concat] 四条换成一次 helper 调用。
        matcher
            .Start()
            .MatchStartForward(
                new CodeMatch(OpCodes.Ldstr, HealthRowTemplateKey),
                new CodeMatch(OpCodes.Ldstr, VanillaPlusSign),
                new CodeMatch(OpCodes.Ldloca_S),
                new CodeMatch(OpCodes.Call, intToStringMethod),
                new CodeMatch(OpCodes.Call, stringConcatMethod)
            )
            .ThrowIfNotMatch(
                "Health tooltip positive-row seam is unavailable (vanilla health row argument shape missing)."
            );
        var healthValueLocal = matcher.InstructionAt(2).operand;
        matcher.Advance(1);
        var incomingLabels = new List<Label>(matcher.Labels);
        matcher.RemoveInstructions(4);
        var replacement = new List<CodeInstruction>
        {
            LoadArgument(HoveredItemArgumentIndex),
            new CodeInstruction(OpCodes.Ldloc, healthValueLocal),
            new CodeInstruction(OpCodes.Call, formatMethod),
        };
        replacement[0].labels.AddRange(incomingLabels);
        matcher.InsertAndAdvance(replacement);

        return matcher.InstructionEnumeration();
    }

    // 从当前位置逐条前扫，找到第一个写入指定局部变量的指令（stloc.2 / stloc.s 2 等编码
    // 形态不赌，逐条验证），窗口上限防御 IL 形态漂移（fail closed）。
    private static void AdvanceToNextLocalStore(
        CodeMatcher matcher,
        int localIndex,
        int window,
        string failureMessage
    )
    {
        for (var offset = 0; offset < window; offset++)
        {
            if (
                IsLocalInstruction(matcher.Instruction, OpCodes.Stloc_S, localIndex)
                || IsLocalInstruction(matcher.Instruction, OpCodes.Stloc, localIndex)
                || IsShortLocalStore(matcher.Instruction, localIndex)
            )
            {
                return;
            }

            matcher.Advance(1);
        }

        throw new InvalidOperationException(failureMessage);
    }

    private static bool IsShortLocalStore(CodeInstruction? instruction, int localIndex)
    {
        if (instruction is null)
            return false;

        return localIndex switch
        {
            0 => instruction.opcode == OpCodes.Stloc_0,
            1 => instruction.opcode == OpCodes.Stloc_1,
            2 => instruction.opcode == OpCodes.Stloc_2,
            3 => instruction.opcode == OpCodes.Stloc_3,
            _ => false,
        };
    }

    // SMAPI 4.5.2 供给 Harmony 2.2.2：本地变量操作数在指令流里读回为 LocalBuilder，
    // 不赌编码字节，逐形态解析（镜像 SmapiVanillaSanityTooltipService.IsLocalInstruction）。
    private static bool IsLocalInstruction(
        CodeInstruction? instruction,
        OpCode expectedOpcode,
        int expectedLocalIndex
    )
    {
        if (instruction is null || instruction.opcode != expectedOpcode)
            return false;

        return instruction.operand switch
        {
            LocalBuilder local => local.LocalIndex == expectedLocalIndex,
            byte index => index == expectedLocalIndex,
            sbyte index => index == expectedLocalIndex,
            short index => index == expectedLocalIndex,
            ushort index => index == expectedLocalIndex,
            int index => index == expectedLocalIndex,
            _ => false,
        };
    }

    private static CodeInstruction LoadArgument(int argumentIndex)
    {
        return argumentIndex switch
        {
            0 => new CodeInstruction(OpCodes.Ldarg_0),
            1 => new CodeInstruction(OpCodes.Ldarg_1),
            2 => new CodeInstruction(OpCodes.Ldarg_2),
            3 => new CodeInstruction(OpCodes.Ldarg_3),
            <= byte.MaxValue => new CodeInstruction(OpCodes.Ldarg_S, (byte)argumentIndex),
            _ => new CodeInstruction(OpCodes.Ldarg, checked((short)argumentIndex)),
        };
    }

    private static CodeInstruction LoadLocal(int localIndex)
    {
        return localIndex switch
        {
            0 => new CodeInstruction(OpCodes.Ldloc_0),
            1 => new CodeInstruction(OpCodes.Ldloc_1),
            2 => new CodeInstruction(OpCodes.Ldloc_2),
            3 => new CodeInstruction(OpCodes.Ldloc_3),
            <= byte.MaxValue => new CodeInstruction(OpCodes.Ldloc_S, (byte)localIndex),
            _ => new CodeInstruction(OpCodes.Ldloc, checked((short)localIndex)),
        };
    }

    private static CodeInstruction LoadLocalAddress(int localIndex)
    {
        return localIndex <= byte.MaxValue
            ? new CodeInstruction(OpCodes.Ldloca_S, (byte)localIndex)
            : new CodeInstruction(OpCodes.Ldloca, checked((short)localIndex));
    }

    private static CodeInstruction StoreLocal(int localIndex)
    {
        return localIndex switch
        {
            0 => new CodeInstruction(OpCodes.Stloc_0),
            1 => new CodeInstruction(OpCodes.Stloc_1),
            2 => new CodeInstruction(OpCodes.Stloc_2),
            3 => new CodeInstruction(OpCodes.Stloc_3),
            <= byte.MaxValue => new CodeInstruction(OpCodes.Stloc_S, (byte)localIndex),
            _ => new CodeInstruction(OpCodes.Stloc, checked((short)localIndex)),
        };
    }

    private static bool HasExpectedLocalLayout(MethodInfo method)
    {
        var locals = method.GetMethodBody()?.LocalVariables;
        return locals is not null
            && locals.Count > YLocalIndex
            && locals[WidthLocalIndex].LocalType == typeof(int)
            && locals[HeightLocalIndex].LocalType == typeof(int)
            && locals[XLocalIndex].LocalType == typeof(int)
            && locals[YLocalIndex].LocalType == typeof(int);
    }

    private static bool IsOwnedTranspilerInstalled(MethodInfo method, string owner)
    {
        var patchInfo = Harmony.GetPatchInfo(method);
        if (patchInfo is null)
            return false;

        foreach (var transpiler in patchInfo.Transpilers)
        {
            if (string.Equals(transpiler.owner, owner, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static void OnLocaleChanged(object? sender, LocaleChangedEventArgs e)
    {
        tooltipCulture = LocalizedValueFormatter.ResolveCulture(e.NewLocale);
    }

    private static void LogOnce(string reason, LogLevel level, string? detail = null)
    {
        if (monitor is null || !loggedReasons.Add(reason))
            return;

        monitor.Log(
            string.IsNullOrWhiteSpace(detail)
                ? reason
                : $"{reason} ({detail})",
            level
        );
    }
}
