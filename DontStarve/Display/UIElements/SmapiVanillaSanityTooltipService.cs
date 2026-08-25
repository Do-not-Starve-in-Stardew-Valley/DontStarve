#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HungerEatFood = DontStarve.Player.Stats.Hunger.HungerBehaviors.EatFood;
using DontStarve.Player.Stats.Sanity;
using DontStarve.Resource;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buffs;
using StardewValley.Menus;
using SanityEatFood = DontStarve.Player.Stats.Sanity.SanityBehaviors.EatFood;

namespace DontStarve.Display.UIElements;

/// <summary>
/// Adds read-only Hunger, Sanity and supported Buff data at the final Stardew 1.6.15 tooltip
/// overload. The transpiler extends the vanilla measurement and inserts survival rows after
/// vanilla Energy/Health rows, so item types whose drawTooltip ignores overrideText are covered.
/// </summary>
internal sealed class SmapiVanillaSanityTooltipService : IDisposable
{
    internal const string ExpectedGameVersion = "1.6.15";
    internal const string ExpectedTargetSignature =
        "IClickableMenu.drawHoverText(SpriteBatch,StringBuilder,SpriteFont,...,Item hoveredItem,...)";

    private const int VanillaTooltipTextWidthPadding = 92;
    private const int VanillaTooltipRowIconScale = SanityTooltipLayoutContract.VanillaTooltipIconScale;
    private const int WidthLocalIndex = 1;
    private const int HeightLocalIndex = 2;
    private const int XLocalIndex = 5;
    private const int YLocalIndex = 6;
    private const int BuffIconsArgumentIndex = 8;
    private const int HoveredItemArgumentIndex = 9;
    private const int AlphaArgumentIndex = 15;

    private const int TooltipIconDestinationPixels = SanityTooltipLayoutContract.TooltipIconPixels;
    private const int TooltipIconXOffset = 20;
    private const int TooltipIconYOffset = 16;
    private const int TextXOffset = 54;
    private const int MaximumLoggedTooltipDiagnostics = 96;

    private static SmapiVanillaSanityTooltipService? activeInstance;

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly string patchOwnerId;
    private readonly ISanitySystemState sanitySystemState;
    private readonly bool externalExtraMachineConfigLoaded;
    private readonly FoodBuffTooltipFormatter formatter;
    private readonly HashSet<string> loggedFailures = new(StringComparer.Ordinal);
    private readonly HashSet<int> loggedTooltipDiagnostics = new();

    private Harmony? harmony;
    private MethodInfo? targetMethod;
    private MethodInfo? combatLegacyTargetMethod;
    private bool patchInstalled;
    private bool hoverDiagnosticPrefixInstalled;
    private bool combatLegacyPatchInstalled;
    private bool disposed;

    internal SmapiVanillaSanityTooltipService(
        IModHelper helper,
        IMonitor monitor,
        string modId,
        ISanitySystemState sanitySystemState
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        if (string.IsNullOrWhiteSpace(modId))
            throw new ArgumentException("A mod ID is required.", nameof(modId));
        this.sanitySystemState =
            sanitySystemState ?? throw new ArgumentNullException(nameof(sanitySystemState));

        this.patchOwnerId = string.Concat(modId, ".Sanity4.VanillaTooltip");
        this.externalExtraMachineConfigLoaded = helper.ModRegistry.IsLoaded(
            FoodBuffTooltipFormatter.ExtraMachineConfigUniqueId
        );
        this.formatter = new FoodBuffTooltipFormatter(
            helper,
            this.externalExtraMachineConfigLoaded
        );

        this.InstallPatch();
        helper.Events.GameLoop.ReturnedToTitle += this.OnReturnedToTitle;
        AppDomain.CurrentDomain.ProcessExit += this.OnProcessExit;
        monitor.Log(
            $"Sanity vanilla tooltip capability: game-version={Game1.version}, target={ExpectedTargetSignature}, patch-owner={this.patchOwnerId}, patch-installed={this.patchInstalled}, hover-diagnostic-prefix={this.hoverDiagnosticPrefixInstalled}, combat-legacy-patch={this.combatLegacyPatchInstalled}, external-extra-machine-config={this.externalExtraMachineConfigLoaded}.",
            this.patchInstalled ? LogLevel.Debug : LogLevel.Warn
        );
        monitor.Log(
            "Sanity tooltip hover diagnostics armed (revision=tooltip-hover-v1). Hover a food item once and collect [sanity-tooltip-diagnostic] records.",
            LogLevel.Info
        );
    }

    internal bool PatchInstalled => this.patchInstalled;

    public void Dispose()
    {
        if (this.disposed)
            return;
        this.disposed = true;
        if (ReferenceEquals(activeInstance, this))
            activeInstance = null;
        if (this.harmony is not null && this.targetMethod is not null)
        {
            try
            {
                this.harmony.Unpatch(
                    this.targetMethod,
                    HarmonyPatchType.Transpiler,
                    this.patchOwnerId
                );
                this.harmony.Unpatch(
                    this.targetMethod,
                    HarmonyPatchType.Prefix,
                    this.patchOwnerId
                );
            }
            catch (Exception exception)
            {
                this.LogFailureOnce(
                    "patch-uninstall",
                    $"Sanity tooltip patch cleanup failed ({exception.GetType().Name}: {exception.Message})."
                );
            }
        }
        if (this.harmony is not null && this.combatLegacyTargetMethod is not null)
        {
            try
            {
                this.harmony.Unpatch(
                    this.combatLegacyTargetMethod,
                    HarmonyPatchType.Postfix,
                    this.patchOwnerId
                );
            }
            catch (Exception exception)
            {
                this.LogFailureOnce(
                    "combat-legacy-patch-uninstall",
                    $"Sanity combat legacy tooltip patch cleanup failed ({exception.GetType().Name}: {exception.Message})."
                );
            }
        }

        this.patchInstalled = false;
        this.hoverDiagnosticPrefixInstalled = false;
        this.combatLegacyPatchInstalled = false;
        this.helper.Events.GameLoop.ReturnedToTitle -= this.OnReturnedToTitle;
        this.formatter.Dispose();
        AppDomain.CurrentDomain.ProcessExit -= this.OnProcessExit;
    }

    private static int ApplyAdditionalWidth(
        int width,
        SpriteFont font,
        SanityTooltipRowLayout layout
    )
    {
        if (activeInstance is not { disposed: false, patchInstalled: true } service)
            return width;

        try
        {
            var originalWidth = width;
            foreach (var row in layout.Rows)
            {
                width = Math.Max(
                    width,
                    (int)font.MeasureString(row.Text).X + VanillaTooltipTextWidthPadding
                );
            }
            service.LogTooltipLayoutDiagnostic(
                "width",
                layout,
                null,
                $"before={originalWidth}, after={width}"
            );
        }
        catch (Exception exception)
        {
            service.LogFailureOnce(
                string.Concat("measure-width|", exception.GetType().FullName),
                $"Sanity tooltip width adapter failed open ({exception.GetType().Name}: {exception.Message})."
            );
        }

        return width;
    }

    private static int ApplyAdditionalHeight(
        int height,
        SanityTooltipRowLayout layout,
        string[]? vanillaBuffIcons
    )
    {
        if (activeInstance is not { disposed: false, patchInstalled: true } service)
            return height;

        try
        {
            var originalHeight = height;
            // The original Buff separator only exists when its argument is non-null. When it is
            // absent, the adapter owns the separator before its own extra Buff rows.
            var additionalHeight = layout.GetAdditionalHeight(vanillaBuffIcons is not null);
            height += additionalHeight;
            service.LogTooltipLayoutDiagnostic(
                "height",
                layout,
                vanillaBuffIcons,
                $"before={originalHeight}, additional={additionalHeight}, after={height}"
            );
        }
        catch (Exception exception)
        {
            service.LogFailureOnce(
                string.Concat("measure-height|", exception.GetType().FullName),
                $"Sanity tooltip height adapter failed open ({exception.GetType().Name}: {exception.Message})."
            );
        }

        return height;
    }

    private static void DrawAdditionalRows(
        SpriteBatch spriteBatch,
        SpriteFont font,
        float alpha,
        SanityTooltipRowLayout layout,
        string[]? vanillaBuffIcons,
        int width,
        int x,
        ref int y
    )
    {
        if (activeInstance is not { disposed: false, patchInstalled: true } service)
            return;

        try
        {
            service.LogTooltipLayoutDiagnostic(
                "draw-enter",
                layout,
                vanillaBuffIcons,
                $"x={x}, y={y}, width={width}, alpha={alpha:0.###}"
            );
            if (layout.SurvivalRowCount > 0)
                DrawTooltipSection(spriteBatch, font, alpha, layout, false, width, x, ref y);

            // The original Buff section (and its separator) is skipped for a null array. Render
            // the same final extra-Buff rows here so non-vanilla Buff data is never branch-skipped.
            if (vanillaBuffIcons is null && layout.ExtraBuffRowCount > 0)
                DrawTooltipSection(spriteBatch, font, alpha, layout, true, width, x, ref y);
            service.LogTooltipLayoutDiagnostic(
                "draw-exit",
                layout,
                vanillaBuffIcons,
                $"y={y}"
            );
        }
        catch (Exception exception)
        {
            service.LogFailureOnce(
                string.Concat("draw-rows|", exception.GetType().FullName),
                $"Sanity tooltip row adapter failed open ({exception.GetType().Name}: {exception.Message})."
            );
        }
    }

    // This keeps the seven multiplier rows in the existing vanilla Buff section and order used
    // by ExtraMachineConfig. Survival rows were already drawn before the vanilla separator.
    private static void DrawExtraMachineConfigRows(
        SpriteBatch spriteBatch,
        SpriteFont font,
        float alpha,
        SanityTooltipRowLayout layout,
        int x,
        ref int y
    )
    {
        if (activeInstance is not { disposed: false, patchInstalled: true } service)
            return;

        try
        {
            DrawTooltipRows(spriteBatch, font, alpha, layout, true, x, ref y);
        }
        catch (Exception exception)
        {
            service.LogFailureOnce(
                string.Concat("draw-extra-machine-config-rows|", exception.GetType().FullName),
                $"Sanity tooltip extra Buff adapter failed open ({exception.GetType().Name}: {exception.Message})."
            );
        }
    }

    private static void DrawExtraMachineConfigRowsFallback(
        SpriteBatch spriteBatch,
        SpriteFont font,
        float alpha,
        SanityTooltipRowLayout layout,
        string[]? vanillaBuffIcons,
        int x,
        ref int y
    )
    {
        if (
            layout.ExtraBuffRowCount == 0
            || !SanityTooltipLayoutContract.NeedsExtraBuffFallback(vanillaBuffIcons)
        )
        {
            return;
        }

        DrawExtraMachineConfigRows(spriteBatch, font, alpha, layout, x, ref y);
    }

    private static SanityTooltipRowLayout GetTooltipRowLayout(
        Item? hoveredItem,
        string[]? vanillaBuffIcons
    )
    {
        if (activeInstance is not { disposed: false, patchInstalled: true } service)
            return SanityTooltipRowLayout.Empty;

        try
        {
            var layout = service.CreateTooltipRowLayout(hoveredItem, vanillaBuffIcons);
            service.LogTooltipItemDiagnostic(hoveredItem, layout, vanillaBuffIcons);
            return layout;
        }
        catch (Exception exception)
        {
            service.LogFailureOnce(
                string.Concat("layout|", exception.GetType().FullName),
                $"Sanity tooltip layout adapter failed open ({exception.GetType().Name}: {exception.Message})."
            );
            return SanityTooltipRowLayout.Empty;
        }
    }

    // This prefix is diagnostic-only. It runs before the transpiled body, so a target-enter record
    // proves which drawHoverText overload the live tooltip actually invoked even if a later branch
    // never reaches one of this adapter's injected seams.
    private static void DiagnoseFinalDrawHoverText(
        Item? hoveredItem,
        string[]? buffIconsToDisplay
    )
    {
        if (activeInstance is not { disposed: false, patchInstalled: true } service)
            return;

        service.LogTooltipTargetEntryDiagnostic(hoveredItem, buffIconsToDisplay);
    }

    private SanityTooltipRowLayout CreateTooltipRowLayout(
        Item? hoveredItem,
        string[]? vanillaBuffIcons
    )
    {
        return new SanityTooltipRowLayout(
            this.formatter.GetRows(
                hoveredItem,
                this.sanitySystemState.IsEnabled,
                vanillaBuffIcons,
                // Icons already identify the survival statistic in the vanilla tooltip. Keep
                // these rows compact without changing the descriptive i18n used elsewhere.
                textMode: FoodBuffTooltipTextMode.CompactVanillaTooltip
            )
        );
    }

    private static void DrawTooltipSection(
        SpriteBatch spriteBatch,
        SpriteFont font,
        float alpha,
        SanityTooltipRowLayout layout,
        bool drawBuffRows,
        int width,
        int x,
        ref int y
    )
    {
        y += 16;
        spriteBatch.Draw(
            Game1.staminaRect,
            new Rectangle(x + 12, y + 6, Math.Max(1, width - 24), 2),
            new Color(207, 147, 103) * 0.8f * alpha
        );
        DrawTooltipRows(spriteBatch, font, alpha, layout, drawBuffRows, x, ref y);
        y -= 8;
    }

    private static void DrawTooltipRows(
        SpriteBatch spriteBatch,
        SpriteFont font,
        float alpha,
        SanityTooltipRowLayout layout,
        bool drawBuffRows,
        int x,
        ref int y
    )
    {
        foreach (var row in layout.Rows)
        {
            if ((row.Kind == SanityTooltipRowKind.Buff) != drawBuffRows)
                continue;

            if (
                row.IconKind == SanityTooltipIconKind.HungerIcon
                || row.IconKind == SanityTooltipIconKind.SanityBrain
            )
            {
                var icon = row.IconKind == SanityTooltipIconKind.HungerIcon
                    ? TextureLoader.HungerIcon
                    : TextureLoader.SanityBrain;
                spriteBatch.Draw(
                    icon,
                    new Rectangle(
                        x + TooltipIconXOffset,
                        y + TooltipIconYOffset,
                        TooltipIconDestinationPixels,
                        TooltipIconDestinationPixels
                    ),
                    // Keep the full custom texture as the source rectangle. Both supplied
                    // tooltip icons are 16x16 and are scaled to the vanilla 30x30 target.
                    new Rectangle(
                        0,
                        0,
                        icon.Width,
                        icon.Height
                    ),
                    Color.White * alpha
                );
            }
            else
            {
                Utility.drawWithShadow(
                    spriteBatch,
                    Game1.mouseCursors,
                    new Vector2(x + TooltipIconXOffset, y + TooltipIconYOffset),
                    new Rectangle(row.VanillaCursorSourceX, 428, 10, 10),
                    Color.White,
                    0f,
                    Vector2.Zero,
                    VanillaTooltipRowIconScale,
                    flipped: false,
                    0.95f * alpha
                );
            }

            Utility.drawTextWithShadow(
                spriteBatch,
                row.Text,
                font,
                new Vector2(x + TextXOffset, y + 16),
                Game1.textColor * 0.9f * alpha
            );
            y += SanityTooltipLayoutContract.CustomRowHeight;
        }
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        this.formatter.ClearCache();
        this.loggedTooltipDiagnostics.Clear();
    }

    // Tooltip drawing runs every frame. Each diagnostic stage is therefore deduplicated by the
    // observed item, Buff array and final rows, with a hard cap for the current title session.
    private void LogTooltipTargetEntryDiagnostic(Item? hoveredItem, string[]? vanillaBuffIcons)
    {
        try
        {
            if (!this.TryBeginTooltipDiagnostic("target-enter", hoveredItem, null, vanillaBuffIcons))
                return;

            this.monitor.Log(
                string.Concat(
                    "[sanity-tooltip-diagnostic] stage=target-enter ",
                    DescribeItem(hoveredItem),
                    " ",
                    DescribeFoodTableMatches(hoveredItem),
                    " sanity-enabled=",
                    this.sanitySystemState.IsEnabled,
                    " vanilla-buff-icons=",
                    DescribeVanillaBuffIcons(vanillaBuffIcons)
                ),
                LogLevel.Info
            );
        }
        catch (Exception exception)
        {
            this.LogFailureOnce(
                string.Concat("hover-diagnostic-target|", exception.GetType().FullName),
                $"Sanity tooltip hover diagnostic failed open ({exception.GetType().Name}: {exception.Message})."
            );
        }
    }

    private void LogTooltipItemDiagnostic(
        Item? hoveredItem,
        SanityTooltipRowLayout layout,
        string[]? vanillaBuffIcons
    )
    {
        try
        {
            if (!this.TryBeginTooltipDiagnostic("layout", hoveredItem, layout, vanillaBuffIcons))
                return;

            this.monitor.Log(
                string.Concat(
                    "[sanity-tooltip-diagnostic] stage=layout ",
                    DescribeItem(hoveredItem),
                    " ",
                    DescribeFoodTableMatches(hoveredItem),
                    " sanity-enabled=",
                    this.sanitySystemState.IsEnabled,
                    " vanilla-buff-icons=",
                    DescribeVanillaBuffIcons(vanillaBuffIcons),
                    " rows=",
                    DescribeRows(layout)
                ),
                LogLevel.Info
            );
        }
        catch (Exception exception)
        {
            this.LogFailureOnce(
                string.Concat("hover-diagnostic-layout|", exception.GetType().FullName),
                $"Sanity tooltip layout diagnostic failed open ({exception.GetType().Name}: {exception.Message})."
            );
        }
    }

    private void LogTooltipLayoutDiagnostic(
        string stage,
        SanityTooltipRowLayout layout,
        string[]? vanillaBuffIcons,
        string details
    )
    {
        try
        {
            if (!this.TryBeginTooltipDiagnostic(stage, null, layout, vanillaBuffIcons))
                return;

            this.monitor.Log(
                string.Concat(
                    "[sanity-tooltip-diagnostic] stage=",
                    stage,
                    " vanilla-buff-icons=",
                    DescribeVanillaBuffIcons(vanillaBuffIcons),
                    " rows=",
                    DescribeRows(layout),
                    " ",
                    details
                ),
                LogLevel.Info
            );
        }
        catch (Exception exception)
        {
            this.LogFailureOnce(
                string.Concat("hover-diagnostic-", stage, "|", exception.GetType().FullName),
                $"Sanity tooltip {stage} diagnostic failed open ({exception.GetType().Name}: {exception.Message})."
            );
        }
    }

    private bool TryBeginTooltipDiagnostic(
        string stage,
        Item? hoveredItem,
        SanityTooltipRowLayout? layout,
        string[]? vanillaBuffIcons
    )
    {
        if (this.loggedTooltipDiagnostics.Count >= MaximumLoggedTooltipDiagnostics)
            return false;

        var hash = new HashCode();
        hash.Add(stage, StringComparer.Ordinal);
        hash.Add(hoveredItem?.QualifiedItemId ?? string.Empty, StringComparer.Ordinal);
        hash.Add(hoveredItem?.ItemId ?? string.Empty, StringComparer.Ordinal);
        hash.Add(vanillaBuffIcons is null);
        if (vanillaBuffIcons is not null)
        {
            hash.Add(vanillaBuffIcons.Length);
            foreach (var icon in vanillaBuffIcons)
                hash.Add(icon ?? string.Empty, StringComparer.Ordinal);
        }

        if (layout is not null)
        {
            foreach (var row in layout.Rows)
                hash.Add(row);
        }

        return this.loggedTooltipDiagnostics.Add(hash.ToHashCode());
    }

    private static string DescribeItem(Item? item)
    {
        if (item is null)
            return "item=<null>";

        return string.Concat(
            "item-id=",
            EscapeDiagnosticText(item.ItemId),
            " qualified-id=",
            EscapeDiagnosticText(item.QualifiedItemId),
            " name=",
            EscapeDiagnosticText(item.Name)
        );
    }

    private static string DescribeFoodTableMatches(Item? item)
    {
        if (item is null)
            return "hunger-data=not-checked sanity-data=not-checked";

        var hungerMatch = "table-uninitialized";
        var hungerTable = HungerEatFood.FoodHunger;
        if (hungerTable is not null)
        {
            hungerMatch = hungerTable.TryGetValue(item.ItemId, out var hungerValue)
                ? $"hit:{hungerValue}"
                : "miss";
        }

        var sanityMatch = "table-uninitialized";
        var sanityTable = SanityEatFood.FoodSanity;
        if (sanityTable is not null)
        {
            sanityMatch = sanityTable.TryGetValue(item.ItemId, out var sanityValue)
                ? $"hit:{sanityValue}"
                : "miss";
        }

        return string.Concat(
            "hunger-data=",
            hungerMatch,
            " sanity-data=",
            sanityMatch
        );
    }

    private static string DescribeVanillaBuffIcons(string[]? vanillaBuffIcons)
    {
        if (vanillaBuffIcons is null)
            return "null";

        var displayedCount = 0;
        foreach (var icon in vanillaBuffIcons)
        {
            if (!string.IsNullOrEmpty(icon) && !string.Equals(icon, "0", StringComparison.Ordinal))
                displayedCount++;
        }

        var duration = vanillaBuffIcons.Length <= SanityTooltipLayoutContract.VanillaBuffDurationLegacyIndex
            ? "missing"
            : EscapeDiagnosticText(
                vanillaBuffIcons[SanityTooltipLayoutContract.VanillaBuffDurationLegacyIndex]
            );
        return $"len={vanillaBuffIcons.Length}, displayed={displayedCount}, duration={duration}";
    }

    private static string DescribeRows(SanityTooltipRowLayout layout)
    {
        if (layout.Rows.Count == 0)
            return "[]";

        var builder = new StringBuilder();
        builder.Append('[');
        for (var index = 0; index < layout.Rows.Count; index++)
        {
            if (index > 0)
                builder.Append("; ");
            var row = layout.Rows[index];
            builder
                .Append(row.Kind)
                .Append('/')
                .Append(row.IconKind)
                .Append(":\"")
                .Append(EscapeDiagnosticText(row.Text))
                .Append('\"');
        }
        builder.Append(']');
        return builder.ToString();
    }

    private static string EscapeDiagnosticText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "<empty>";

        return value.Replace("\r", "\\r").Replace("\n", "\\n");
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        this.Dispose();
    }

    private void InstallPatch()
    {
        if (!string.Equals(Game1.version, ExpectedGameVersion, StringComparison.Ordinal))
        {
            this.LogFailureOnce(
                "version",
                $"Sanity vanilla tooltip is unavailable (expected-game-version={ExpectedGameVersion}, actual={Game1.version})."
            );
            return;
        }
        if (activeInstance is not null && !ReferenceEquals(activeInstance, this))
        {
            this.LogFailureOnce(
                "instance-conflict",
                "Sanity vanilla tooltip is unavailable because another adapter instance is active."
            );
            return;
        }

        var signature = new[]
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
        };
        this.targetMethod = AccessTools.DeclaredMethod(
            typeof(IClickableMenu),
            nameof(IClickableMenu.drawHoverText),
            signature
        );
        var transpilerMethod = AccessTools.DeclaredMethod(
            typeof(SmapiVanillaSanityTooltipService),
            nameof(TranspileFinalDrawHoverText)
        );
        var diagnosticPrefixMethod = AccessTools.DeclaredMethod(
            typeof(SmapiVanillaSanityTooltipService),
            nameof(DiagnoseFinalDrawHoverText)
        );
        var parameters = this.targetMethod?.GetParameters();
        if (
            this.targetMethod is null
            || transpilerMethod is null
            || diagnosticPrefixMethod is null
            || !this.targetMethod.IsStatic
            || this.targetMethod.ReturnType != typeof(void)
            || parameters is null
            || parameters.Length != signature.Length
            || !string.Equals(parameters[1].Name, "text", StringComparison.Ordinal)
            || parameters[1].ParameterType != typeof(StringBuilder)
            || !string.Equals(
                parameters[BuffIconsArgumentIndex].Name,
                "buffIconsToDisplay",
                StringComparison.Ordinal
            )
            || parameters[BuffIconsArgumentIndex].ParameterType != typeof(string[])
            || !string.Equals(parameters[HoveredItemArgumentIndex].Name, "hoveredItem", StringComparison.Ordinal)
            || parameters[HoveredItemArgumentIndex].ParameterType != typeof(Item)
        )
        {
            this.LogFailureOnce(
                "signature",
                "Sanity vanilla tooltip is unavailable (reason=sanity.tooltip.final-overload-signature-drift)."
            );
            return;
        }
        if (!HasExpectedLocalLayout(this.targetMethod))
        {
            this.LogFailureOnce(
                "locals",
                "Sanity vanilla tooltip is unavailable (reason=sanity.tooltip.final-overload-local-layout-drift)."
            );
            return;
        }

        try
        {
            this.harmony = new Harmony(this.patchOwnerId);
            this.harmony.Patch(
                this.targetMethod,
                prefix: new HarmonyMethod(diagnosticPrefixMethod)
                {
                    // Run before all tooltip body code; this is diagnostic-only and never changes flow.
                    priority = Priority.High,
                },
                transpiler: new HarmonyMethod(transpilerMethod)
                {
                    // Run against the unmodified vanilla shape before later tooltip transpilers.
                    priority = Priority.High,
                }
            );
            if (!IsOwnedTranspilerInstalled(this.targetMethod, this.patchOwnerId))
            {
                this.harmony.Unpatch(
                    this.targetMethod,
                    HarmonyPatchType.Transpiler,
                    this.patchOwnerId
                );
                this.harmony.Unpatch(
                    this.targetMethod,
                    HarmonyPatchType.Prefix,
                    this.patchOwnerId
                );
                this.LogFailureOnce(
                    "readback",
                    "Sanity vanilla tooltip is unavailable (reason=sanity.tooltip.patch-owner-readback-failed)."
                );
                return;
            }

            this.hoverDiagnosticPrefixInstalled = IsOwnedPrefixInstalled(
                this.targetMethod,
                this.patchOwnerId
            );
            if (!this.hoverDiagnosticPrefixInstalled)
            {
                // Keep the actual tooltip adapter available when the optional observability hook
                // cannot be read back, but make that limitation explicit in SMAPI's log.
                this.LogFailureOnce(
                    "hover-diagnostic-prefix-readback",
                    "Sanity tooltip hover diagnostic prefix owner readback failed; target-enter diagnostics are unavailable."
                );
            }

            this.InstallCombatLegacyPatch();
            activeInstance = this;
            this.patchInstalled = true;
        }
        catch (Exception exception)
        {
            if (this.harmony is not null && this.targetMethod is not null)
            {
                try
                {
                    this.harmony.Unpatch(
                        this.targetMethod,
                        HarmonyPatchType.Transpiler,
                        this.patchOwnerId
                    );
                    this.harmony.Unpatch(
                        this.targetMethod,
                        HarmonyPatchType.Prefix,
                        this.patchOwnerId
                    );
                }
                catch (Exception cleanupException)
                {
                    this.LogFailureOnce(
                        "install-cleanup",
                        $"Sanity tooltip partial patch cleanup failed ({cleanupException.GetType().Name}: {cleanupException.Message})."
                    );
                }
            }
            this.LogFailureOnce(
                "install",
                $"Sanity vanilla tooltip transpiler failed closed ({exception.GetType().Name}: {exception.Message})."
            );
        }
    }

    private void InstallCombatLegacyPatch()
    {
        if (this.externalExtraMachineConfigLoaded || this.harmony is null)
            return;

        this.combatLegacyTargetMethod = AccessTools.Method(
            typeof(BuffEffects),
            nameof(BuffEffects.ToLegacyAttributeFormat)
        );
        var postfixMethod = AccessTools.DeclaredMethod(
            typeof(SmapiVanillaSanityTooltipService),
            nameof(ApplyCombatLevelToLegacyAttributes)
        );
        if (this.combatLegacyTargetMethod is null || postfixMethod is null)
        {
            this.LogFailureOnce(
                "combat-legacy-patch-target",
                "Sanity combat legacy tooltip patch is unavailable because BuffEffects.ToLegacyAttributeFormat changed."
            );
            return;
        }

        try
        {
            this.harmony.Patch(
                this.combatLegacyTargetMethod,
                postfix: new HarmonyMethod(postfixMethod)
            );
            if (!IsOwnedPostfixInstalled(this.combatLegacyTargetMethod, this.patchOwnerId))
            {
                this.harmony.Unpatch(
                    this.combatLegacyTargetMethod,
                    HarmonyPatchType.Postfix,
                    this.patchOwnerId
                );
                this.LogFailureOnce(
                    "combat-legacy-patch-readback",
                    "Sanity combat legacy tooltip patch owner readback failed."
                );
                return;
            }

            this.combatLegacyPatchInstalled = true;
        }
        catch (Exception exception)
        {
            this.LogFailureOnce(
                "combat-legacy-patch-install",
                $"Sanity combat legacy tooltip patch failed open ({exception.GetType().Name}: {exception.Message})."
            );
        }
    }

    private static void ApplyCombatLevelToLegacyAttributes(
        BuffEffects __instance,
        ref string[] __result
    )
    {
        if (
            __result is null
            || __result.Length <= SanityTooltipLayoutContract.CombatLegacyIndex
        )
        {
            return;
        }

        __result[SanityTooltipLayoutContract.CombatLegacyIndex] =
            ((int)__instance.CombatLevel.Value).ToString();
    }

    private static IEnumerable<CodeInstruction> TranspileFinalDrawHoverText(
        IEnumerable<CodeInstruction> instructions,
        ILGenerator generator
    )
    {
        var layoutMethod = AccessTools.DeclaredMethod(
            typeof(SmapiVanillaSanityTooltipService),
            nameof(GetTooltipRowLayout)
        );
        var heightMethod = AccessTools.DeclaredMethod(
            typeof(SmapiVanillaSanityTooltipService),
            nameof(ApplyAdditionalHeight)
        );
        var widthMethod = AccessTools.DeclaredMethod(
            typeof(SmapiVanillaSanityTooltipService),
            nameof(ApplyAdditionalWidth)
        );
        var drawMethod = AccessTools.DeclaredMethod(
            typeof(SmapiVanillaSanityTooltipService),
            nameof(DrawAdditionalRows)
        );
        var extraMachineConfigDrawMethod = AccessTools.DeclaredMethod(
            typeof(SmapiVanillaSanityTooltipService),
            nameof(DrawExtraMachineConfigRows)
        );
        var extraMachineConfigFallbackMethod = AccessTools.DeclaredMethod(
            typeof(SmapiVanillaSanityTooltipService),
            nameof(DrawExtraMachineConfigRowsFallback)
        );
        if (
            layoutMethod is null
            || heightMethod is null
            || widthMethod is null
            || drawMethod is null
            || extraMachineConfigDrawMethod is null
            || extraMachineConfigFallbackMethod is null
        )
        {
            throw new InvalidOperationException("Sanity tooltip transpiler helpers are unavailable.");
        }

        var matcher = new CodeMatcher(instructions);
        var layoutLocal = generator.DeclareLocal(typeof(SanityTooltipRowLayout));

        // Build one final layout at the common target of the vanilla height guard. The exact same
        // local is consumed by height, width and both drawing seams. Inserting immediately after
        // brfalse would leave the null-array path jumping over the adapter.
        matcher
            .MatchStartForward(
                new CodeMatch(OpCodes.Ldarg_S, (byte)BuffIconsArgumentIndex),
                new CodeMatch(OpCodes.Brfalse_S)
            )
            .ThrowIfNotMatch("Vanilla tooltip height measurement seam is unavailable.");
        InsertAtBranchTarget(
            matcher,
            BuildLayoutAndHeightAdjustmentInstructions(layoutMethod, heightMethod, layoutLocal),
            "Vanilla tooltip height measurement target is unavailable."
        );

        matcher
            .Start()
            .MatchStartForward(
                new CodeMatch(OpCodes.Ldarg_S, (byte)BuffIconsArgumentIndex),
                new CodeMatch(OpCodes.Brfalse_S)
            )
            .Advance(1)
            .MatchStartForward(
                new CodeMatch(OpCodes.Ldarg_S, (byte)BuffIconsArgumentIndex),
                new CodeMatch(OpCodes.Brfalse_S)
            )
            .ThrowIfNotMatch("Vanilla tooltip width measurement seam is unavailable.");
        InsertAtBranchTarget(
            matcher,
            BuildWidthAdjustmentInstructions(widthMethod, layoutLocal),
            "Vanilla tooltip width measurement target is unavailable."
        );

        // The third vanilla Buff guard begins immediately after the Energy and Health rows.
        // Insert before that guard so Hunger/Sanity stay before vanilla Buff rows even when the
        // vanilla buff array is null and the original branch skips its section.
        matcher
            .MatchStartForward(
                new CodeMatch(OpCodes.Ldarg_S, (byte)BuffIconsArgumentIndex),
                new CodeMatch(OpCodes.Brfalse)
            )
            .ThrowIfNotMatch("Vanilla tooltip Buff drawing seam is unavailable.");

        // Energy/Health branches target the original Buff guard. Move those incoming labels to
        // the custom block so every path converges here before the vanilla Buff flow continues.
        var incomingLabels = new List<Label>(matcher.Labels);
        matcher.Labels.Clear();
        var drawingInstructions = new List<CodeInstruction>(
            BuildDrawingInstructions(drawMethod, layoutLocal)
        );
        if (drawingInstructions.Count > 0)
            drawingInstructions[0].labels.AddRange(incomingLabels);
        matcher.InsertAndAdvance(drawingInstructions);

        // Clone ExtraMachineConfig's duration guard: the seven multiplier rows belong in the
        // existing vanilla Buff section immediately before its duration line.
        var durationGuardMatch = new[]
        {
            new CodeMatch(OpCodes.Ldloc_S),
            new CodeMatch(OpCodes.Ldc_I4_S, (sbyte)12),
            new CodeMatch(OpCodes.Bne_Un),
        };
        matcher
            .End()
            .MatchStartBackwards(
                new CodeMatch(OpCodes.Ldstr, "Strings\\UI:ItemHover_Buff")
            )
            .MatchStartBackwards(durationGuardMatch)
            .ThrowIfNotMatch("Vanilla tooltip Buff-duration seam is unavailable.");
        // Confirm the same guard in the forward direction, then restore its start position for
        // cloning. This protects the insertion from accidentally matching an earlier loop test.
        var durationGuardStart = matcher.Pos;
        var durationGuardVerifier = new CodeMatcher(matcher.Instructions());
        durationGuardVerifier
            .Advance(durationGuardStart)
            .MatchEndForward(durationGuardMatch)
            .ThrowIfNotMatch("Vanilla tooltip Buff-duration guard is unavailable.");
        var durationGuard = matcher.Instructions(3);
        matcher
            .InsertAndAdvance(durationGuard)
            .InsertAndAdvance(
                BuildExtraMachineConfigDrawingInstructions(extraMachineConfigDrawMethod, layoutLocal)
            );

        // Some producers provide a short array or leave the duration slot empty. In that case the
        // original k == 12 seam is never reached, so draw the same rows once at the loop tail.
        var buffLoopTailMatch = new[]
        {
            // Harmony's original-instruction reader represents local operands as LocalBuilder,
            // not as the encoded byte used by the ldloc.s instruction. Match the opcode first,
            // then validate the resolved local index below so this seam works on the real 2.2.2
            // instruction stream without weakening the target check.
            new CodeMatch(OpCodes.Ldloc_S),
            new CodeMatch(OpCodes.Ldc_I4_8),
            new CodeMatch(OpCodes.Sub),
            new CodeMatch(OpCodes.Stloc_S),
        };
        matcher
            .End()
            .MatchStartBackwards(buffLoopTailMatch)
            .ThrowIfNotMatch("Vanilla tooltip Buff-loop tail seam is unavailable.");
        if (
            !IsLocalInstruction(matcher.InstructionAt(0), OpCodes.Ldloc_S, YLocalIndex)
            || !IsLocalInstruction(matcher.InstructionAt(3), OpCodes.Stloc_S, YLocalIndex)
        )
        {
            throw new InvalidOperationException("Vanilla tooltip Buff-loop tail local seam is unavailable.");
        }
        matcher.InsertAndAdvance(
            BuildExtraMachineConfigFallbackInstructions(
                extraMachineConfigFallbackMethod,
                layoutLocal
            )
        );

        return matcher.InstructionEnumeration();
    }

    private static IEnumerable<CodeInstruction> BuildLayoutAndHeightAdjustmentInstructions(
        MethodInfo layoutMethod,
        MethodInfo heightMethod,
        LocalBuilder layoutLocal
    )
    {
        return new[]
        {
            LoadArgumentValue(HoveredItemArgumentIndex),
            LoadArgumentValue(BuffIconsArgumentIndex),
            new CodeInstruction(OpCodes.Call, layoutMethod),
            StoreLocalValue(layoutLocal),
            LoadLocalValue(HeightLocalIndex),
            LoadLocalValue(layoutLocal),
            LoadArgumentValue(BuffIconsArgumentIndex),
            new CodeInstruction(OpCodes.Call, heightMethod),
            StoreLocalValue(HeightLocalIndex),
        };
    }

    private static void InsertAtBranchTarget(
        CodeMatcher matcher,
        IEnumerable<CodeInstruction> instructions,
        string failureMessage
    )
    {
        matcher.ThrowIfInvalid(failureMessage);
        if (matcher.InstructionAt(1).operand is not Label targetLabel)
            throw new InvalidOperationException(failureMessage);

        var codes = matcher.Instructions();
        var targetIndex = -1;
        for (var index = 0; index < codes.Count; index++)
        {
            if (codes[index].labels.Contains(targetLabel))
            {
                targetIndex = index;
                break;
            }
        }

        if (targetIndex < 0)
            throw new InvalidOperationException(failureMessage);

        var incomingLabels = new List<Label>(codes[targetIndex].labels);
        codes[targetIndex].labels.Clear();
        var inserted = new List<CodeInstruction>(instructions);
        if (inserted.Count == 0)
            return;
        inserted[0].labels.AddRange(incomingLabels);

        matcher.Start().Advance(targetIndex).InsertAndAdvance(inserted);
    }

    private static IEnumerable<CodeInstruction> BuildWidthAdjustmentInstructions(
        MethodInfo widthMethod,
        LocalBuilder layoutLocal
    )
    {
        return new[]
        {
            LoadLocalValue(WidthLocalIndex),
            LoadArgumentValue(2),
            LoadLocalValue(layoutLocal),
            new CodeInstruction(OpCodes.Call, widthMethod),
            StoreLocalValue(WidthLocalIndex),
        };
    }

    private static IEnumerable<CodeInstruction> BuildDrawingInstructions(
        MethodInfo drawMethod,
        LocalBuilder layoutLocal
    )
    {
        return new[]
        {
            LoadArgumentValue(0),
            LoadArgumentValue(2),
            LoadArgumentValue(AlphaArgumentIndex),
            LoadLocalValue(layoutLocal),
            LoadArgumentValue(BuffIconsArgumentIndex),
            LoadLocalValue(WidthLocalIndex),
            LoadLocalValue(XLocalIndex),
            LoadLocalAddress(YLocalIndex),
            new CodeInstruction(OpCodes.Call, drawMethod),
        };
    }

    private static IEnumerable<CodeInstruction> BuildExtraMachineConfigDrawingInstructions(
        MethodInfo drawMethod,
        LocalBuilder layoutLocal
    )
    {
        return new[]
        {
            LoadArgumentValue(0),
            LoadArgumentValue(2),
            LoadArgumentValue(AlphaArgumentIndex),
            LoadLocalValue(layoutLocal),
            LoadLocalValue(XLocalIndex),
            LoadLocalAddress(YLocalIndex),
            new CodeInstruction(OpCodes.Call, drawMethod),
        };
    }

    private static IEnumerable<CodeInstruction> BuildExtraMachineConfigFallbackInstructions(
        MethodInfo drawMethod,
        LocalBuilder layoutLocal
    )
    {
        return new[]
        {
            LoadArgumentValue(0),
            LoadArgumentValue(2),
            LoadArgumentValue(AlphaArgumentIndex),
            LoadLocalValue(layoutLocal),
            LoadArgumentValue(BuffIconsArgumentIndex),
            LoadLocalValue(XLocalIndex),
            LoadLocalAddress(YLocalIndex),
            new CodeInstruction(OpCodes.Call, drawMethod),
        };
    }

    // SMAPI 4.5.2 supplies Harmony 2.2.2, which lacks the newer CodeInstruction helper methods.
    private static CodeInstruction LoadArgumentValue(int argumentIndex)
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

    private static CodeInstruction LoadLocalValue(int localIndex)
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

    private static CodeInstruction LoadLocalValue(LocalBuilder local)
    {
        return new CodeInstruction(OpCodes.Ldloc, local);
    }

    private static CodeInstruction LoadLocalAddress(int localIndex)
    {
        return localIndex <= byte.MaxValue
            ? new CodeInstruction(OpCodes.Ldloca_S, (byte)localIndex)
            : new CodeInstruction(OpCodes.Ldloca, checked((short)localIndex));
    }

    private static CodeInstruction StoreLocalValue(int localIndex)
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

    private static CodeInstruction StoreLocalValue(LocalBuilder local)
    {
        return new CodeInstruction(OpCodes.Stloc, local);
    }

    private static bool IsLocalInstruction(
        CodeInstruction instruction,
        OpCode expectedOpcode,
        int expectedLocalIndex
    )
    {
        if (instruction.opcode != expectedOpcode)
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

    private static bool IsOwnedTranspilerInstalled(MethodInfo method, string ownerId)
    {
        var patchInfo = Harmony.GetPatchInfo(method);
        if (patchInfo is null)
            return false;
        foreach (var transpiler in patchInfo.Transpilers)
        {
            if (string.Equals(transpiler.owner, ownerId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool IsOwnedPrefixInstalled(MethodInfo method, string ownerId)
    {
        var patchInfo = Harmony.GetPatchInfo(method);
        if (patchInfo is null)
            return false;
        foreach (var prefix in patchInfo.Prefixes)
        {
            if (string.Equals(prefix.owner, ownerId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool IsOwnedPostfixInstalled(MethodInfo method, string ownerId)
    {
        var patchInfo = Harmony.GetPatchInfo(method);
        if (patchInfo is null)
            return false;
        foreach (var postfix in patchInfo.Postfixes)
        {
            if (string.Equals(postfix.owner, ownerId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private void LogFailureOnce(string key, string message)
    {
        if (this.loggedFailures.Count >= 32 || !this.loggedFailures.Add(key))
            return;
        this.monitor.Log(message, LogLevel.Warn);
    }
}
