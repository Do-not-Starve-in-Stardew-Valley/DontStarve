using System.Text;
using System.Globalization;
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

    [Fact]
    public void DedupeIsPerFrameItemAndBuilder()
    {
        var gate = new SanityTooltipDedupeGate();
        var item = new object();
        var builder = new object();

        Assert.True(gate.TryEnter(10, item, builder));
        Assert.False(gate.TryEnter(10, item, builder));
        Assert.True(gate.TryEnter(10, new object(), builder));
        Assert.True(gate.TryEnter(10, item, new object()));
        Assert.False(gate.TryEnter(10, item, builder));
        Assert.True(gate.TryEnter(11, item, builder));
    }

    [Fact]
    public void DedupeWindowIsBoundedWithinOneFrame()
    {
        var gate = new SanityTooltipDedupeGate();
        for (var index = 0; index < SanityTooltipDedupeGate.MaximumEntriesPerFrame; index++)
            Assert.True(gate.TryEnter(20, new object(), new object()));

        Assert.False(gate.TryEnter(20, new object(), new object()));
        Assert.True(gate.TryEnter(21, new object(), new object()));
    }

    [Fact]
    public void AppenderAddsOneExactLineWithoutFlatteningExistingText()
    {
        var text = new StringBuilder("Rabbit's Foot\nA lucky charm");

        Assert.True(
            SanityTooltipTextAppender.TryAppendLine(
                text,
                "-10 Sanity when eaten"
            )
        );
        Assert.False(
            SanityTooltipTextAppender.TryAppendLine(
                text,
                "-10 Sanity when eaten"
            )
        );
        Assert.Equal(
            "Rabbit's Foot\nA lucky charm\n-10 Sanity when eaten",
            text.ToString()
        );
    }

    [Fact]
    public void SimilarSubstringDoesNotCountAsAnExactTooltipLine()
    {
        var text = new StringBuilder("Prefix +0.59 Sanity per minute while equipped suffix");

        Assert.True(
            SanityTooltipTextAppender.TryAppendLine(
                text,
                "+0.59 Sanity per minute while equipped"
            )
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
            SanityTooltipTextAppender.TryFormatValue(
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
    public void RuntimePatchesOnlyFinalStringBuilderOverloadAndReusesBehaviorTables()
    {
        var source = File.ReadAllText(RuntimeSourcePath);

        Assert.Contains("ExpectedGameVersion = \"1.6.15\"", source, StringComparison.Ordinal);
        Assert.Contains("typeof(IClickableMenu)", source, StringComparison.Ordinal);
        Assert.Contains("nameof(IClickableMenu.drawHoverText)", source, StringComparison.Ordinal);
        Assert.Contains("typeof(StringBuilder)", source, StringComparison.Ordinal);
        Assert.Contains("parameters[1].Name, \"text\"", source, StringComparison.Ordinal);
        Assert.Contains("parameters[9].Name,", source, StringComparison.Ordinal);
        Assert.Contains("\"hoveredItem\"", source, StringComparison.Ordinal);
        Assert.Contains("BeforeFinalDrawHoverText", source, StringComparison.Ordinal);
        Assert.Contains("Harmony.GetPatchInfo", source, StringComparison.Ordinal);
        Assert.Contains("EatFood.TryGetSanity", source, StringComparison.Ordinal);
        Assert.Contains("Wearing.TryGetPerMinuteSanity", source, StringComparison.Ordinal);
        Assert.Contains("\"sanity-tooltip.food-once\"", source, StringComparison.Ordinal);
        Assert.Contains("\"sanity-tooltip.equipment-per-minute\"", source, StringComparison.Ordinal);
        Assert.Contains("Events.Display.MenuChanged +=", source, StringComparison.Ordinal);
        Assert.Contains("Events.GameLoop.ReturnedToTitle +=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ChangeSanity", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteSaveData", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RenderingHud", source, StringComparison.Ordinal);
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
