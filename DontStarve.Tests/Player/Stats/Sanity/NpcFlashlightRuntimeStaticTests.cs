using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class NpcFlashlightRuntimeStaticTests
{
    [Fact]
    public void RuntimeUsesTwoOwnedNativeLightSourcesAndAvoidsPerNpcReadback()
    {
        var service = ReadContract("SmapiNpcFlashlightService.cs");

        Assert.Contains("Game1.currentLocation", service, StringComparison.Ordinal);
        Assert.Contains("location.characters", service, StringComparison.Ordinal);
        Assert.Contains("LightSource.lantern", service, StringComparison.Ordinal);
        Assert.Contains("NpcFlashlightConeLightSource", service, StringComparison.Ordinal);
        Assert.Contains("NpcFlashlightPolicy.BodyLightRadiusTiles", service, StringComparison.Ordinal);
        Assert.Contains("NpcFlashlightPolicy.IsEligibleNpc", service, StringComparison.Ordinal);
        Assert.Contains("npc is Monster", service, StringComparison.Ordinal);
        Assert.Contains("EnsureCachedTexture", service, StringComparison.Ordinal);
        Assert.Contains("RefreshCadenceTicks = 3", service, StringComparison.Ordinal);
        Assert.DoesNotContain("sharedLights", service, StringComparison.Ordinal);
        Assert.DoesNotContain("GetData(", service, StringComparison.Ordinal);
        Assert.DoesNotContain("RenderTarget2D", service, StringComparison.Ordinal);
    }

    [Fact]
    public void ConeOverridesNativeLightSourceDrawAndUsesACachedTexture()
    {
        var cone = ReadContract("NpcFlashlightConeLightSource.cs");

        Assert.Contains(
            "class NpcFlashlightConeLightSource : LightSource",
            cone,
            StringComparison.Ordinal
        );
        Assert.Contains("public override void Draw", cone, StringComparison.Ordinal);
        Assert.Contains("spriteBatch.Draw(", cone, StringComparison.Ordinal);
        Assert.Contains("new Texture2D", cone, StringComparison.Ordinal);
        Assert.Contains("texture.SetData(pixels)", cone, StringComparison.Ordinal);
        Assert.Contains("NpcFlashlightPolicy.GetConeHalfWidthPixels", cone, StringComparison.Ordinal);
        Assert.Contains("onlyLocation", cone, StringComparison.Ordinal);
        Assert.Contains("ReleaseCachedTexture", cone, StringComparison.Ordinal);
        Assert.Contains("GetCachedConeTexture(spriteBatch.GraphicsDevice)", cone, StringComparison.Ordinal);
        Assert.DoesNotContain("RenderTarget2D", cone, StringComparison.Ordinal);
    }

    [Fact]
    public void EntryWiresTheFlashlightsToNaturalDarknessAndTheTwoRuntimeToggles()
    {
        var entry = ReadContract("ModEntry.cs");
        var naturalDarkness = ReadContract("SmapiNaturalDarknessLightmapService.cs");

        Assert.Contains("SmapiNpcFlashlightService", entry, StringComparison.Ordinal);
        Assert.Contains(
            "_naturalDarknessLightmap.GetSceneStateForScreen",
            entry,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "ConfigKeys.EnableNaturalDarkness",
            entry,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "_npcFlashlights?.SetEnabled(enabled)",
            entry,
            StringComparison.Ordinal
        );
        Assert.Contains("LightmapWhiteBlend", naturalDarkness, StringComparison.Ordinal);
    }

    private static string ReadContract(string fileName)
    {
        return File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "NaturalDarkness",
                fileName
            )
        );
    }
}
