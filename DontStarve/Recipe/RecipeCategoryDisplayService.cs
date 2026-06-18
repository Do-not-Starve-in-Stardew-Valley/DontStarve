using System;
using System.Collections.Generic;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using Object = StardewValley.Object;

namespace DontStarve.Recipe;

internal static class RecipeCategoryDisplayService
{
    private const string IconIndexPath = "Asset/RecipeCategoryIcons/index.json";

    private static readonly Dictionary<int, string> CategoryTranslationKeys = new()
    {
        [-9] = "recipe-category.big-craftable",
        [-12] = "recipe-category.geode-mineral",
        [-14] = "recipe-category.meat",
        [-15] = "recipe-category.metal-resource",
        [-16] = "recipe-category.basic-resource",
        [-17] = "recipe-category.rare-product",
        [-23] = "recipe-category.other-aquatic-products",
        [-25] = "recipe-category.other-cooking",
        [-27] = "recipe-category.syrup",
        [-95] = "recipe-category.hat",
        [-98] = "recipe-category.weapon",
        [-101] = "recipe-category.trinket",
    };

    private static Dictionary<string, string> _iconIndex = new();
    private static IMonitor _monitor;
    private static ITranslationHelper _translations;
    private static bool _initialized;

    internal static void Initialize(IModHelper helper, IMonitor monitor, string manifestId)
    {
        if (_initialized)
            return;

        _initialized = true;
        _monitor = monitor;
        _translations = helper.Translation;
        _iconIndex = LoadIconIndex(helper, monitor);

        RegisterHarmonyPatches(new Harmony(manifestId));
    }

    private static Dictionary<string, string> LoadIconIndex(IModHelper helper, IMonitor monitor)
    {
        try
        {
            return helper.ModContent.Load<Dictionary<string, string>>(IconIndexPath) ?? new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            monitor.Log($"Failed to load recipe category icon index '{IconIndexPath}': {ex.Message}", LogLevel.Warn);
            return new Dictionary<string, string>();
        }
    }

    private static void RegisterHarmonyPatches(Harmony harmony)
    {
        PatchMethod(
            harmony,
            AccessTools.Method(typeof(CraftingRecipe), nameof(CraftingRecipe.getSpriteIndexFromRawIndex)),
            nameof(PatchGetSpriteIndexFromRawIndex));

        PatchMethod(
            harmony,
            AccessTools.Method(typeof(CraftingRecipe), nameof(CraftingRecipe.getNameFromIndex)),
            nameof(PatchGetNameFromIndex));
    }

    private static void PatchMethod(Harmony harmony, System.Reflection.MethodInfo original, string postfixName)
    {
        if (original == null)
        {
            _monitor.Log($"Skipped recipe category display patch '{postfixName}' because the Stardew method was not found.", LogLevel.Warn);
            return;
        }

        try
        {
            var postfix = AccessTools.Method(typeof(RecipeCategoryDisplayService), postfixName);
            harmony.Patch(original, postfix: new HarmonyMethod(postfix));
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to apply recipe category display patch '{postfixName}': {ex.Message}", LogLevel.Error);
        }
    }

    private static void PatchGetSpriteIndexFromRawIndex(ref string __result, string item_id)
    {
        if (string.IsNullOrWhiteSpace(item_id) || item_id[0] != '-')
            return;

        if (_iconIndex.TryGetValue(item_id, out var iconId) && !string.IsNullOrWhiteSpace(iconId))
            __result = iconId;
    }

    private static void PatchGetNameFromIndex(ref string __result, string item_id)
    {
        if (__result != "???" || !int.TryParse(item_id, out var categoryId) || categoryId >= 0)
            return;

        var categoryName = GetCategoryDisplayName(categoryId);
        if (string.IsNullOrWhiteSpace(categoryName) || categoryName == "???")
            return;

        __result = $"{categoryName}{GetTranslationOrDefault("recipe-category.any", " (Any)")}";
    }

    private static string GetCategoryDisplayName(int categoryId)
    {
        if (CategoryTranslationKeys.TryGetValue(categoryId, out var translationKey))
        {
            var translated = GetTranslationOrDefault(translationKey, null);
            if (!string.IsNullOrWhiteSpace(translated))
                return translated;
        }

        try
        {
            return Object.GetCategoryDisplayName(categoryId);
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to resolve recipe category name for '{categoryId}': {ex.Message}", LogLevel.Trace);
            return null;
        }
    }

    private static string GetTranslationOrDefault(string key, string fallback)
    {
        var translated = _translations?.Get(key).ToString();
        return string.IsNullOrWhiteSpace(translated) || translated == key
            ? fallback
            : translated;
    }
}
