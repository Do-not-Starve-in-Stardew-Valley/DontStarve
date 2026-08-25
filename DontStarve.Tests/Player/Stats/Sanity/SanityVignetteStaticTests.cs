using System.Xml.Linq;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityVignetteStaticTests
{
    private static string ShippedVignetteRoot => Path.Combine(
        AppContext.BaseDirectory,
        "ShippedMod",
        "Asset",
        "Sanity",
        "Overlays",
        "Vignette"
    );

    [Fact]
    public void Shipped_vignette_export_has_the_basic_and_insane_timelines_with_all_declared_images()
    {
        var document = XDocument.Load(Path.Combine(ShippedVignetteRoot, "vig.scml"));
        var root = Assert.IsType<XElement>(document.Root);
        Assert.Equal("spriter_data", root.Name.LocalName);

        var animations = root
            .Descendants("animation")
            .ToDictionary(
                animation => RequiredAttribute(animation, "name"),
                StringComparer.Ordinal
            );
        Assert.Equal(584, int.Parse(RequiredAttribute(animations["basic"], "length")));
        Assert.Equal(5334, int.Parse(RequiredAttribute(animations["insane"], "length")));

        var assetRootWithSeparator = Path.GetFullPath(ShippedVignetteRoot)
            + Path.DirectorySeparatorChar;
        var spritePaths = root
            .Elements("folder")
            .SelectMany(folder => folder.Elements("file"))
            .Select(file => RequiredAttribute(file, "name"))
            .ToArray();
        Assert.Equal(5, spritePaths.Length);
        foreach (var relativePath in spritePaths)
        {
            Assert.DoesNotContain("..", relativePath, StringComparison.Ordinal);
            var filePath = Path.GetFullPath(
                Path.Combine(
                    ShippedVignetteRoot,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)
                )
            );
            Assert.StartsWith(
                assetRootWithSeparator,
                filePath,
                StringComparison.OrdinalIgnoreCase
            );
            Assert.True(File.Exists(filePath), $"Missing SCML sprite: {relativePath}");
        }
    }

    [Fact]
    public void Renderer_preserves_the_tested_fixed_screen_layout_without_porting_the_failed_harmony_experiment()
    {
        var renderer = ReadVignetteContract("SanityVignetteRenderer.cs");
        var overlay = ReadVignetteContract("SanityVignetteOverlayService.cs");
        var entry = ReadContract("ShadowProjection", "ModEntry.cs");

        Assert.Contains("SourceCanvasWidth = 1366f", renderer, StringComparison.Ordinal);
        Assert.Contains("SourceCanvasHeight = 768f", renderer, StringComparison.Ordinal);
        Assert.Contains("OverlayScale = 1.075f", renderer, StringComparison.Ordinal);
        Assert.Contains("LoadAnimation(root, \"basic\")", renderer, StringComparison.Ordinal);
        Assert.Contains("LoadAnimation(root, \"insane\")", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("Harmony", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("Harmony", overlay, StringComparison.Ordinal);
        Assert.Contains("Events.Display.RenderedHud +=", overlay, StringComparison.Ordinal);
        Assert.Contains("Events.GameLoop.SaveLoaded +=", overlay, StringComparison.Ordinal);
        Assert.Contains("ConfigKeys.EnableSanityVignette", entry, StringComparison.Ordinal);
        Assert.Contains("_sanityVignette?.SetLowSanityFilterEnabled", entry, StringComparison.Ordinal);
        Assert.Contains("_sanityVignette?.SetVignetteEnabled", entry, StringComparison.Ordinal);

        var drawStart = overlay.IndexOf("private void OnRenderedHud", StringComparison.Ordinal);
        var drawEnd = overlay.IndexOf("private bool TryRefreshCurrentOwner", drawStart, StringComparison.Ordinal);
        Assert.True(drawStart >= 0 && drawEnd > drawStart);
        var draw = overlay[drawStart..drawEnd];
        Assert.Contains("renderer.IsLoaded", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("TryLoadRenderer", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.activeClickableMenu", draw, StringComparison.Ordinal);
    }

    private static string RequiredAttribute(XElement element, string attributeName)
    {
        return Assert.IsType<string>(element.Attribute(attributeName)?.Value);
    }

    private static string ReadVignetteContract(string fileName)
    {
        return ReadContract("Vignette", fileName);
    }

    private static string ReadContract(string folder, string fileName)
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", folder, fileName)
        );
    }
}
