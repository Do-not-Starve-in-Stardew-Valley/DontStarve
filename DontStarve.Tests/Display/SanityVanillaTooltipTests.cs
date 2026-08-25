using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using DontStarve.Display.UIElements;
using Xunit;

namespace DontStarve.Tests.Display;

public sealed class SanityVanillaTooltipTests
{
    private static string RuntimeSourcePath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "Tooltip",
            "SmapiVanillaSanityTooltipService.cs"
        );

    private static string WearingSourcePath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "Tooltip",
            "Wearing.cs"
        );

    private static string FormatterSourcePath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "Tooltip",
            "FoodBuffTooltipFormatter.cs"
        );

    private static string FoodTooltipSourcePath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "Tooltip",
            "FoodTooltip.cs"
        );

    private static string DisplayManagerSourcePath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "Tooltip",
            "DisplayManager.cs"
        );

    private static string BrainAssetPath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "ShippedMod",
            "Asset",
            "Sanity",
            "sanity.png"
        );

    [Fact]
    public void VanillaDuplicateFilteringRetainsRowsBeforeTheFirstDuplicate()
    {
        var rows = new[]
        {
            new SanityTooltipRow(
                SanityTooltipRowKind.Hunger,
                "hunger",
                SanityTooltipIconKind.HungerIcon,
                -1,
                -1
            ),
            new SanityTooltipRow(
                SanityTooltipRowKind.Buff,
                "combat",
                SanityTooltipIconKind.VanillaCursor,
                SanityTooltipLayoutContract.CombatLegacyIndex,
                40
            ),
            new SanityTooltipRow(
                SanityTooltipRowKind.Buff,
                "attack",
                SanityTooltipIconKind.VanillaCursor,
                SanityTooltipLayoutContract.AttackLegacyIndex,
                120
            ),
            new SanityTooltipRow(
                SanityTooltipRowKind.Buff,
                "custom",
                SanityTooltipIconKind.VanillaCursor,
                -1,
                150
            ),
        };
        var vanillaBuffIcons = Enumerable.Repeat("0", 12).ToArray();
        vanillaBuffIcons[SanityTooltipLayoutContract.CombatLegacyIndex] = "+1";
        vanillaBuffIcons[SanityTooltipLayoutContract.AttackLegacyIndex] = "2";

        var filtered = SanityTooltipRowFilter.RemoveVanillaDuplicates(rows, vanillaBuffIcons);

        Assert.Equal(new[] { "hunger", "custom" }, filtered.Select(row => row.Text));
    }

    [Fact]
    public void ZeroVanillaIconsDoNotHideCustomRowsOrAllocateAReplacementList()
    {
        var rows = new[]
        {
            new SanityTooltipRow(
                SanityTooltipRowKind.Buff,
                "combat",
                SanityTooltipIconKind.VanillaCursor,
                SanityTooltipLayoutContract.CombatLegacyIndex,
                40
            ),
        };
        var vanillaBuffIcons = Enumerable.Repeat("0", 12).ToArray();

        var filtered = SanityTooltipRowFilter.RemoveVanillaDuplicates(rows, vanillaBuffIcons);

        Assert.Same(rows, filtered);
    }

    [Fact]
    public void FinalLayoutSeparatesSurvivalAndExtraBuffRows()
    {
        var rows = new[]
        {
            new SanityTooltipRow(
                SanityTooltipRowKind.Hunger,
                "hunger",
                SanityTooltipIconKind.HungerIcon,
                -1,
                -1
            ),
            new SanityTooltipRow(
                SanityTooltipRowKind.Sanity,
                "sanity",
                SanityTooltipIconKind.SanityBrain,
                -1,
                -1
            ),
            new SanityTooltipRow(
                SanityTooltipRowKind.Buff,
                "buff",
                SanityTooltipIconKind.VanillaCursor,
                -1,
                120
            ),
        };

        var layout = new SanityTooltipRowLayout(rows);

        Assert.Same(rows, layout.Rows);
        Assert.Equal(2, layout.SurvivalRowCount);
        Assert.Equal(1, layout.ExtraBuffRowCount);
        Assert.Equal(4 + 3 * 39, layout.GetAdditionalHeight(vanillaBuffSectionPresent: true));
        Assert.Equal(
            4 + 2 * 39 + 4 + 39,
            layout.GetAdditionalHeight(vanillaBuffSectionPresent: false)
        );
        Assert.Equal(0, SanityTooltipRowLayout.Empty.GetAdditionalHeight(false));
    }

    [Fact]
    public void ExtraBuffFallbackCoversMissingOrEmptyVanillaDurationSlot()
    {
        Assert.False(SanityTooltipLayoutContract.NeedsExtraBuffFallback(null));
        Assert.True(
            SanityTooltipLayoutContract.NeedsExtraBuffFallback(new string[12])
        );

        var missingDuration = new string[13];
        Assert.True(
            SanityTooltipLayoutContract.NeedsExtraBuffFallback(missingDuration)
        );

        var emptyDuration = Enumerable.Repeat("0", 13).ToArray();
        emptyDuration[SanityTooltipLayoutContract.VanillaBuffDurationLegacyIndex] = "";
        Assert.True(
            SanityTooltipLayoutContract.NeedsExtraBuffFallback(emptyDuration)
        );

        var zeroDuration = Enumerable.Repeat("0", 13).ToArray();
        Assert.True(
            SanityTooltipLayoutContract.NeedsExtraBuffFallback(zeroDuration)
        );

        var displayedDuration = Enumerable.Repeat("0", 13).ToArray();
        displayedDuration[SanityTooltipLayoutContract.VanillaBuffDurationLegacyIndex] =
            " 00:30";
        Assert.False(
            SanityTooltipLayoutContract.NeedsExtraBuffFallback(displayedDuration)
        );
    }

    [Theory]
    [InlineData(10d, "+10")]
    [InlineData(-10d, "-10")]
    [InlineData(0d, "0")]
    [InlineData(0.588d, "+0.59")]
    [InlineData(-0.004d, "0")]
    public void FoodAndEquipmentValuesUseSharedSignedFormatting(
        double value,
        string expected
    )
    {
        Assert.True(
            LocalizedValueFormatter.TryFormatSigned(
                value,
                CultureInfo.InvariantCulture,
                out var formatted
            )
        );
        Assert.Equal(expected, formatted);
    }

    [Fact]
    public void CoverageNamesCommonVanillaMenusAndExcludesCustomThirdPartyDraw()
    {
        Assert.Equal(
            new[]
            {
                "InventoryPage",
                "ItemGrabMenu",
                "ShopMenu",
                "CollectionsPage",
                "CraftingPage",
            },
            SanityTooltipCoverageContract.StandardVanillaMenus
        );
        Assert.False(
            SanityTooltipCoverageContract.ThirdPartyFullyCustomDrawingCovered
        );
    }

    [Fact]
    public void LayoutContractCoversAllSupportedItemsAndBuffFields()
    {
        Assert.Equal(
            new[]
            {
                "Food",
                "Hat",
                "Shirt",
                "Pants",
                "Boots",
                "Ring",
                "Trinket",
                "MeleeWeapon",
                "Tool",
                "Other",
            },
            SanityTooltipLayoutContract.SupportedItemCategories
        );
        Assert.Equal(
            new[]
            {
                "CombatLevel",
                "AttackMultiplier",
                "Immunity",
                "KnockbackMultiplier",
                "WeaponSpeedMultiplier",
                "CriticalChanceMultiplier",
                "CriticalPowerMultiplier",
                "WeaponPrecisionMultiplier",
            },
            SanityTooltipLayoutContract.SupportedBuffAttributes
        );
        Assert.Equal(39, SanityTooltipLayoutContract.CustomRowHeight);
        Assert.Equal(10, SanityTooltipLayoutContract.VanillaTooltipIconSourcePixels);
        Assert.Equal(3, SanityTooltipLayoutContract.VanillaTooltipIconScale);
        Assert.Equal(30, SanityTooltipLayoutContract.TooltipIconPixels);
        var runtimeSource = File.ReadAllText(RuntimeSourcePath);
        Assert.Contains("TooltipIconYOffset = 16", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("TextureLoader.SanityBrain", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("TextureLoader.HungerIcon", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("icon.Width", runtimeSource, StringComparison.Ordinal);
        Assert.Equal(
            4 + 3 * 39,
            SanityTooltipLayoutContract.GetAdditionalHeight(3)
        );
        Assert.Equal(
            7 * 39,
            SanityTooltipLayoutContract.GetAdditionalHeight(0, 7)
        );
        Assert.Equal(
            4 + 3 * 39 + 7 * 39,
            SanityTooltipLayoutContract.GetAdditionalHeight(3, 7)
        );
        Assert.Equal(
            4 + 2 * 39 + 4 + 3 * 39,
            SanityTooltipLayoutContract.GetAdditionalHeight(2, 3, vanillaBuffSectionPresent: false)
        );
    }

    [Fact]
    public void RuntimePatchesOnlyFinalStringBuilderOverloadAndReusesBehaviorTables()
    {
        var source = File.ReadAllText(RuntimeSourcePath);

        Assert.Contains("ExpectedGameVersion = \"1.6.15\"", source, StringComparison.Ordinal);
        Assert.Contains("typeof(IClickableMenu)", source, StringComparison.Ordinal);
        Assert.Contains("nameof(IClickableMenu.drawHoverText)", source, StringComparison.Ordinal);
        Assert.Contains("typeof(StringBuilder)", source, StringComparison.Ordinal);
        Assert.Contains("parameters[1].Name, \"text\"", source, StringComparison.Ordinal);
        Assert.Contains("BuffIconsArgumentIndex = 8", source, StringComparison.Ordinal);
        Assert.Contains("parameters[BuffIconsArgumentIndex].Name,", source, StringComparison.Ordinal);
        Assert.Contains("\"buffIconsToDisplay\"", source, StringComparison.Ordinal);
        Assert.Contains(
            "parameters[BuffIconsArgumentIndex].ParameterType != typeof(string[])",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains("HoveredItemArgumentIndex = 9", source, StringComparison.Ordinal);
        Assert.Contains("parameters[HoveredItemArgumentIndex].Name", source, StringComparison.Ordinal);
        Assert.Contains("\"hoveredItem\"", source, StringComparison.Ordinal);
        Assert.Contains("nameof(TranspileFinalDrawHoverText)", source, StringComparison.Ordinal);
        Assert.Contains("ApplyAdditionalWidth", source, StringComparison.Ordinal);
        Assert.Contains("ApplyAdditionalHeight", source, StringComparison.Ordinal);
        Assert.Contains("DrawAdditionalRows", source, StringComparison.Ordinal);
        Assert.Contains("DrawExtraMachineConfigRows", source, StringComparison.Ordinal);
        Assert.Contains("DrawExtraMachineConfigRowsFallback", source, StringComparison.Ordinal);
        Assert.Contains("BuildExtraMachineConfigFallbackInstructions", source, StringComparison.Ordinal);
        Assert.Contains("NeedsExtraBuffFallback", source, StringComparison.Ordinal);
        Assert.Contains("third vanilla Buff guard", source, StringComparison.Ordinal);
        Assert.Contains("MatchStartForward", source, StringComparison.Ordinal);
        Assert.Contains("incomingLabels", source, StringComparison.Ordinal);
        Assert.Contains("matcher.Labels.Clear()", source, StringComparison.Ordinal);
        Assert.Contains("drawingInstructions[0].labels.AddRange(incomingLabels)", source, StringComparison.Ordinal);
        Assert.Contains("GetTooltipRowLayout", source, StringComparison.Ordinal);
        Assert.Contains("generator.DeclareLocal(typeof(SanityTooltipRowLayout))", source, StringComparison.Ordinal);
        Assert.Contains("BuildLayoutAndHeightAdjustmentInstructions", source, StringComparison.Ordinal);
        Assert.Contains("LoadLocalValue(layoutLocal)", source, StringComparison.Ordinal);
        Assert.Contains(
            "layout.GetAdditionalHeight(vanillaBuffIcons is not null)",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains("layout.Rows", source, StringComparison.Ordinal);
        Assert.Contains(
            "vanillaBuffIcons is null && layout.ExtraBuffRowCount > 0",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains("DrawTooltipSection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("attachmentSlotsMethod", source, StringComparison.Ordinal);
        Assert.Contains("CodeMatcher", source, StringComparison.Ordinal);
        Assert.Contains("MatchEndForward", source, StringComparison.Ordinal);
        Assert.Contains("MatchStartBackwards", source, StringComparison.Ordinal);
        Assert.Contains("Vanilla tooltip Buff-loop tail seam", source, StringComparison.Ordinal);
        Assert.Contains("IsLocalInstruction(matcher.InstructionAt(0)", source, StringComparison.Ordinal);
        Assert.Contains("LocalBuilder local => local.LocalIndex", source, StringComparison.Ordinal);
        Assert.Contains("HasExpectedLocalLayout", source, StringComparison.Ordinal);
        Assert.Contains("Priority.High", source, StringComparison.Ordinal);
        Assert.DoesNotContain("codes.FindIndex", source, StringComparison.Ordinal);
        Assert.Contains("Harmony.GetPatchInfo", source, StringComparison.Ordinal);
        Assert.Contains("HarmonyPatchType.Transpiler", source, StringComparison.Ordinal);
        Assert.Contains("HarmonyPatchType.Postfix", source, StringComparison.Ordinal);
        Assert.Contains("nameof(BuffEffects.ToLegacyAttributeFormat)", source, StringComparison.Ordinal);
        Assert.Contains("helper.ModRegistry.IsLoaded", source, StringComparison.Ordinal);
        Assert.Contains("FoodBuffTooltipFormatter.ExtraMachineConfigUniqueId", source, StringComparison.Ordinal);
        Assert.Contains("nameof(DiagnoseFinalDrawHoverText)", source, StringComparison.Ordinal);
        Assert.Contains("HarmonyPatchType.Prefix", source, StringComparison.Ordinal);
        Assert.Contains("IsOwnedPrefixInstalled", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BeforeFinalDrawHoverText", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ChangeSanity", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteSaveData", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RenderingHud", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DrawingHelperMatchesTheValueAndReferenceArgumentsItEmits()
    {
        var source = File.ReadAllText(RuntimeSourcePath).Replace("\r\n", "\n");

        Assert.Contains("int width,\n        int x,\n        ref int y", source, StringComparison.Ordinal);
        Assert.Contains("LoadLocalValue(XLocalIndex)", source, StringComparison.Ordinal);
        Assert.Contains("LoadLocalAddress(YLocalIndex)", source, StringComparison.Ordinal);
        Assert.Contains("OpCodes.Ldloca_S", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CodeInstruction.LoadArgument", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CodeInstruction.LoadLocal", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CodeInstruction.StoreLocal", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeProvidesBoundedHoverDiagnosticsAtTargetLayoutMeasurementAndDrawingSeams()
    {
        var source = File.ReadAllText(RuntimeSourcePath);

        Assert.Contains("Sanity tooltip hover diagnostics armed", source, StringComparison.Ordinal);
        Assert.Contains("[sanity-tooltip-diagnostic] stage=target-enter", source, StringComparison.Ordinal);
        Assert.Contains("[sanity-tooltip-diagnostic] stage=layout", source, StringComparison.Ordinal);
        Assert.Contains("LogTooltipTargetEntryDiagnostic", source, StringComparison.Ordinal);
        Assert.Contains("LogTooltipItemDiagnostic", source, StringComparison.Ordinal);
        Assert.Contains("LogTooltipLayoutDiagnostic", source, StringComparison.Ordinal);
        Assert.Contains("MaximumLoggedTooltipDiagnostics", source, StringComparison.Ordinal);
        Assert.Contains("HungerEatFood.FoodHunger", source, StringComparison.Ordinal);
        Assert.Contains("SanityEatFood.FoodSanity", source, StringComparison.Ordinal);
        Assert.Contains("this.loggedTooltipDiagnostics.Clear()", source, StringComparison.Ordinal);
        Assert.Contains("\"draw-enter\"", source, StringComparison.Ordinal);
        Assert.Contains("\"draw-exit\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatterMirrorsExtraMachineConfigBuffAggregationAndCompatibilityGate()
    {
        var source = File.ReadAllText(FormatterSourcePath);

        Assert.Contains("GetRows(", source, StringComparison.Ordinal);
        Assert.Contains("Item? item", source, StringComparison.Ordinal);
        Assert.Contains("HungerEatFood.FoodHunger", source, StringComparison.Ordinal);
        Assert.Contains("SanityEatFood.FoodSanity", source, StringComparison.Ordinal);
        Assert.Contains("Wearing.TryGetPerMinuteSanity", source, StringComparison.Ordinal);
        Assert.Contains("Game1.objectData.TryGetValue", source, StringComparison.Ordinal);
        Assert.Contains("SObject.TryCreateBuffsFromData", source, StringComparison.Ordinal);
        Assert.Contains("item.ModifyItemBuffs", source, StringComparison.Ordinal);
        Assert.Contains("BuffEffects", source, StringComparison.Ordinal);
        Assert.Contains("externalExtraMachineConfigLoaded", source, StringComparison.Ordinal);
        Assert.DoesNotContain("buff?.CustomAttributes", source, StringComparison.Ordinal);
        Assert.Contains("SanityTooltipRowFilter.RemoveVanillaDuplicates", source, StringComparison.Ordinal);

        foreach (var field in SanityTooltipLayoutContract.SupportedBuffAttributes.Skip(1))
            Assert.Contains($"effects.{field}.Value", source, StringComparison.Ordinal);

        Assert.Contains("AttackMultiplierIconSourceX = 120", source, StringComparison.Ordinal);
        Assert.Contains("ImmunityIconSourceX = 150", source, StringComparison.Ordinal);
        Assert.Contains("KnockbackMultiplierIconSourceX = 70", source, StringComparison.Ordinal);
        Assert.Contains("WeaponSpeedMultiplierIconSourceX = 130", source, StringComparison.Ordinal);
        Assert.Contains("CriticalMultiplierIconSourceX = 160", source, StringComparison.Ordinal);
        Assert.Contains("WeaponPrecisionMultiplierIconSourceX = 40", source, StringComparison.Ordinal);
        Assert.Contains("Math.Round(value * 100f)", source, StringComparison.Ordinal);
        Assert.Contains("Math.Round(value, 2)", source, StringComparison.Ordinal);
        Assert.Contains(
            "__instance.CombatLevel.Value",
            File.ReadAllText(RuntimeSourcePath),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void LegacyHeldItemPanelIsRetainedButNotRegistered()
    {
        var legacySource = File.ReadAllText(FoodTooltipSourcePath);
        var displayManagerSource = File.ReadAllText(DisplayManagerSourcePath);

        Assert.Contains("internal class FoodTooltip", legacySource, StringComparison.Ordinal);
        Assert.Contains("public void Render", legacySource, StringComparison.Ordinal);
        Assert.DoesNotContain("new FoodTooltip(", displayManagerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatterGatesOnlySanityRowsAndKeepsHungerAndBuffRows()
    {
        var source = File.ReadAllText(FormatterSourcePath);
        var survivalRowsStart = source.IndexOf("private void AddSurvivalRows", StringComparison.Ordinal);
        var survivalRowsSource = source[survivalRowsStart..];
        var hungerIndex = survivalRowsSource.IndexOf("HungerEatFood.FoodHunger", StringComparison.Ordinal);
        var sanityGateIndex = survivalRowsSource.IndexOf("if (!showSanity)", StringComparison.Ordinal);

        Assert.True(survivalRowsStart >= 0);
        Assert.True(hungerIndex >= 0);
        Assert.True(sanityGateIndex > hungerIndex);
        Assert.Contains(
            "AddRows(rows, this.GetExtraMachineConfigRows(item))",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains("this.sanitySystemState.IsEnabled", File.ReadAllText(RuntimeSourcePath), StringComparison.Ordinal);
    }

    [Fact]
    public void VanillaTooltipUsesCompactSurvivalTextWithoutChangingTheDescriptiveFormatterPath()
    {
        var formatterSource = File.ReadAllText(FormatterSourcePath);
        var runtimeSource = File.ReadAllText(RuntimeSourcePath);

        Assert.Contains(
            "FoodBuffTooltipTextMode textMode = FoodBuffTooltipTextMode.Descriptive",
            formatterSource,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "textMode == FoodBuffTooltipTextMode.CompactVanillaTooltip",
            formatterSource,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "string? compactTranslationKey = null",
            formatterSource,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "compactTranslationKey ?? descriptiveTranslationKey",
            formatterSource,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "compactTranslationKey: \"sanity-tooltip\"",
            formatterSource,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "this.helper.Translation.Get(translationKey, new { value = formattedValue }).ToString()",
            formatterSource,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "textMode: FoodBuffTooltipTextMode.CompactVanillaTooltip",
            runtimeSource,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void EquipmentRowsKeepTheirDedicatedTranslationInCompactTooltips()
    {
        var source = File.ReadAllText(FormatterSourcePath);
        var equipmentStart = source.IndexOf(
            "Wearing.TryGetPerMinuteSanity",
            StringComparison.Ordinal
        );
        var equipmentEnd = source.IndexOf(
            "SanityTooltipIconKind.SanityBrain",
            equipmentStart,
            StringComparison.Ordinal
        );

        Assert.True(equipmentStart >= 0);
        Assert.True(equipmentEnd > equipmentStart);
        var equipmentBlock = source[equipmentStart..equipmentEnd];
        Assert.Contains(
            "sanity-tooltip.equipment-per-minute",
            equipmentBlock,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("compactTranslationKey", equipmentBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void HungerRowsUseTheDedicatedHungerIconWhileSanityRowsUseTheBrainIcon()
    {
        var source = File.ReadAllText(FormatterSourcePath);
        var hungerStart = source.IndexOf("\"hunger-tooltip\"", StringComparison.Ordinal);
        var sanityStart = source.IndexOf(
            "\"sanity-tooltip.food-once\"",
            StringComparison.Ordinal
        );

        Assert.True(hungerStart >= 0);
        Assert.True(sanityStart > hungerStart);
        Assert.Contains(
            "SanityTooltipIconKind.HungerIcon",
            source[hungerStart..sanityStart],
            StringComparison.Ordinal
        );
        Assert.Contains(
            "SanityTooltipIconKind.SanityBrain",
            source[sanityStart..],
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void BrainAssetIsTheValidated16By16RgbaSource()
    {
        var bytes = File.ReadAllBytes(BrainAssetPath);

        Assert.True(bytes.Length >= 26);
        Assert.Equal(
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 },
            bytes[..8]
        );
        Assert.Equal(16, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)));
        Assert.Equal(16, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
        Assert.Equal(8, bytes[24]);
        Assert.Equal(6, bytes[25]);
        Assert.Equal(
            "D6553F1D119C765C30506C86A2882A16D89B6AC00888D87C47058D76C8DD3DEA",
            Convert.ToHexString(SHA256.HashData(bytes))
        );
    }

    [Fact]
    public void HungerAssetIsTheValidated16By16RgbaSource()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "ShippedMod",
            "Asset",
            "Hunger",
            "hunger.png"
        );
        var bytes = File.ReadAllBytes(path);

        Assert.True(bytes.Length >= 26);
        Assert.Equal(
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 },
            bytes[..8]
        );
        Assert.Equal(16, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)));
        Assert.Equal(16, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
        Assert.Equal(8, bytes[24]);
        Assert.Equal(6, bytes[25]);
        Assert.Equal(
            "B035A46224EAED67747067D88FF12A9BA0E6524A51BB621CD786C92894377C7D",
            Convert.ToHexString(SHA256.HashData(bytes))
        );
    }

    [Fact]
    public void EquipmentRoutingUsesCurrentVanillaQualifiedTypeIds()
    {
        var source = File.ReadAllText(WearingSourcePath);

        foreach (var qualifier in new[] { "(H)", "(S)", "(P)", "(B)", "(O)", "(TR)" })
            Assert.Contains($"StartsWith(\"{qualifier}\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StartsWith(\"(R)\"", source, StringComparison.Ordinal);
    }
}
