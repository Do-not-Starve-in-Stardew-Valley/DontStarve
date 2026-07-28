#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using DontStarve.Player.Stats.Sanity.SanityBehaviors;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace DontStarve.Display.UIElements;

/// <summary>
/// Adds read-only Sanity data at the final Stardew 1.6.15 StringBuilder drawHoverText seam. Every
/// standard menu which forwards hoveredItem through drawToolTip/drawHoverText shares this adapter;
/// fully custom third-party drawing remains outside the contract.
/// </summary>
internal sealed class SmapiVanillaSanityTooltipService : IDisposable
{
    private readonly record struct CacheKey(
        SanityTooltipEffectKind Kind,
        string ItemId,
        double Value
    );

    internal const string ExpectedGameVersion = "1.6.15";
    internal const string ExpectedTargetSignature =
        "IClickableMenu.drawHoverText(SpriteBatch,StringBuilder,SpriteFont,...,Item hoveredItem,...)";
    private const int MaximumCachedLines = 256;
    private const int MaximumLoggedFailures = 32;

    private static SmapiVanillaSanityTooltipService? activeInstance;

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly string patchOwnerId;
    private readonly Dictionary<CacheKey, string> cachedLines = new();
    private readonly HashSet<string> loggedFailures = new(StringComparer.Ordinal);
    private readonly SanityTooltipDedupeGate dedupe = new();

    private Harmony? harmony;
    private MethodInfo? targetMethod;
    private CultureInfo culture;
    private bool patchInstalled;
    private bool disposed;

    internal SmapiVanillaSanityTooltipService(
        IModHelper helper,
        IMonitor monitor,
        string modId
    )
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        if (string.IsNullOrWhiteSpace(modId))
            throw new ArgumentException("A mod ID is required.", nameof(modId));
        patchOwnerId = string.Concat(modId, ".Sanity4.VanillaTooltip");
        culture = LocalizedValueFormatter.ResolveCulture(helper.Translation.Locale);

        InstallPatch();
        helper.Events.Content.LocaleChanged += OnLocaleChanged;
        helper.Events.Content.AssetsInvalidated += OnAssetsInvalidated;
        helper.Events.Display.MenuChanged += OnMenuChanged;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        monitor.Log(
            $"Sanity vanilla tooltip capability: game-version={Game1.version}, target={ExpectedTargetSignature}, patch-owner={patchOwnerId}, patch-installed={patchInstalled}.",
            patchInstalled ? LogLevel.Debug : LogLevel.Warn
        );
    }

    internal bool PatchInstalled => patchInstalled;

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        if (ReferenceEquals(activeInstance, this))
            activeInstance = null;
        if (harmony is not null && targetMethod is not null)
        {
            try
            {
                harmony.Unpatch(
                    targetMethod,
                    HarmonyPatchType.Prefix,
                    patchOwnerId
                );
            }
            catch (Exception exception)
            {
                LogFailureOnce(
                    "patch-uninstall",
                    $"Sanity tooltip patch cleanup failed ({exception.GetType().Name}: {exception.Message})."
                );
            }
        }
        patchInstalled = false;
        helper.Events.Content.LocaleChanged -= OnLocaleChanged;
        helper.Events.Content.AssetsInvalidated -= OnAssetsInvalidated;
        helper.Events.Display.MenuChanged -= OnMenuChanged;
        helper.Events.GameLoop.ReturnedToTitle -= OnReturnedToTitle;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        cachedLines.Clear();
        dedupe.Clear();
    }

    private static void BeforeFinalDrawHoverText(
        StringBuilder text,
        Item? hoveredItem
    )
    {
        if (
            activeInstance is not { disposed: false, patchInstalled: true } service
            || hoveredItem is null
        )
        {
            return;
        }

        try
        {
            service.TryAppend(text, hoveredItem);
        }
        catch (Exception exception)
        {
            service.LogFailureOnce(
                string.Concat("draw|", exception.GetType().FullName),
                $"Sanity tooltip draw adapter failed open ({exception.GetType().Name}: {exception.Message})."
            );
        }
    }

    private void TryAppend(StringBuilder text, Item hoveredItem)
    {
        if (!dedupe.TryEnter(Game1.ticks, hoveredItem, text))
            return;
        if (!TryResolveEffect(hoveredItem, out var effect))
            return;

        var key = new CacheKey(effect.Kind, hoveredItem.ItemId, effect.Value);
        if (!cachedLines.TryGetValue(key, out var line))
        {
            if (
                !SanityTooltipTextAppender.TryFormatValue(
                    effect.Value,
                    culture,
                    out var value
                )
            )
            {
                return;
            }

            var translationKey =
                effect.Kind == SanityTooltipEffectKind.FoodOnce
                    ? "sanity-tooltip.food-once"
                    : "sanity-tooltip.equipment-per-minute";
            line = helper.Translation.Get(
                translationKey,
                new { value }
            );
            if (cachedLines.Count >= MaximumCachedLines)
                cachedLines.Clear();
            cachedLines[key] = line;
        }
        SanityTooltipTextAppender.TryAppendLine(text, line);
    }

    private static bool TryResolveEffect(
        Item item,
        out SanityTooltipEffect effect
    )
    {
        if (EatFood.TryGetSanity(item.ItemId, out var foodValue))
        {
            effect = new SanityTooltipEffect(
                SanityTooltipEffectKind.FoodOnce,
                foodValue
            );
            return true;
        }
        if (Wearing.TryGetPerMinuteSanity(item, out var equipmentValue))
        {
            effect = new SanityTooltipEffect(
                SanityTooltipEffectKind.EquipmentPerMinute,
                equipmentValue
            );
            return true;
        }

        effect = default;
        return false;
    }

    private void OnLocaleChanged(object? sender, LocaleChangedEventArgs e)
    {
        culture = LocalizedValueFormatter.ResolveCulture(e.NewLocale);
        cachedLines.Clear();
        dedupe.Clear();
    }

    private void OnAssetsInvalidated(
        object? sender,
        AssetsInvalidatedEventArgs e
    )
    {
        // Behavior tables remain the unique runtime values. Clearing only presentation state
        // ensures a content reload never leaves localized/formatted text pinned to an old frame.
        cachedLines.Clear();
        dedupe.Clear();
    }

    private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
    {
        dedupe.Clear();
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        cachedLines.Clear();
        dedupe.Clear();
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        Dispose();
    }

    private void InstallPatch()
    {
        if (!string.Equals(Game1.version, ExpectedGameVersion, StringComparison.Ordinal))
        {
            LogFailureOnce(
                "version",
                $"Sanity vanilla tooltip is unavailable (expected-game-version={ExpectedGameVersion}, actual={Game1.version})."
            );
            return;
        }
        if (activeInstance is not null && !ReferenceEquals(activeInstance, this))
        {
            LogFailureOnce(
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
        targetMethod = AccessTools.DeclaredMethod(
            typeof(IClickableMenu),
            nameof(IClickableMenu.drawHoverText),
            signature
        );
        var prefixMethod = AccessTools.DeclaredMethod(
            typeof(SmapiVanillaSanityTooltipService),
            nameof(BeforeFinalDrawHoverText)
        );
        var parameters = targetMethod?.GetParameters();
        if (
            targetMethod is null
            || prefixMethod is null
            || !targetMethod.IsStatic
            || targetMethod.ReturnType != typeof(void)
            || parameters is null
            || parameters.Length != signature.Length
            || !string.Equals(parameters[1].Name, "text", StringComparison.Ordinal)
            || parameters[1].ParameterType != typeof(StringBuilder)
            || !string.Equals(
                parameters[9].Name,
                "hoveredItem",
                StringComparison.Ordinal
            )
            || parameters[9].ParameterType != typeof(Item)
        )
        {
            LogFailureOnce(
                "signature",
                "Sanity vanilla tooltip is unavailable (reason=sanity.tooltip.final-overload-signature-drift)."
            );
            return;
        }

        try
        {
            harmony = new Harmony(patchOwnerId);
            harmony.Patch(
                targetMethod,
                prefix: new HarmonyMethod(prefixMethod)
                {
                    priority = Priority.Last,
                }
            );
            if (!IsOwnedPrefixInstalled(targetMethod, patchOwnerId))
            {
                harmony.Unpatch(
                    targetMethod,
                    HarmonyPatchType.Prefix,
                    patchOwnerId
                );
                LogFailureOnce(
                    "readback",
                    "Sanity vanilla tooltip is unavailable (reason=sanity.tooltip.patch-owner-readback-failed)."
                );
                return;
            }

            activeInstance = this;
            patchInstalled = true;
        }
        catch (Exception exception)
        {
            if (harmony is not null && targetMethod is not null)
            {
                try
                {
                    harmony.Unpatch(
                        targetMethod,
                        HarmonyPatchType.Prefix,
                        patchOwnerId
                    );
                }
                catch (Exception cleanupException)
                {
                    LogFailureOnce(
                        "install-cleanup",
                        $"Sanity tooltip partial patch cleanup failed ({cleanupException.GetType().Name}: {cleanupException.Message})."
                    );
                }
            }
            LogFailureOnce(
                "install",
                $"Sanity vanilla tooltip patch failed closed ({exception.GetType().Name}: {exception.Message})."
            );
        }
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

    private void LogFailureOnce(string key, string message)
    {
        if (loggedFailures.Count >= MaximumLoggedFailures || !loggedFailures.Add(key))
            return;
        monitor.Log(message, LogLevel.Warn);
    }
}
