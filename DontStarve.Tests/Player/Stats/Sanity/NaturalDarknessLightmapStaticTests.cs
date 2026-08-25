using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class NaturalDarknessLightmapStaticTests
{
    [Fact]
    public void RuntimeUsesARealTimeClockAndTheNativeDuskAndNightBoundaries()
    {
        var service = ReadContract("SmapiNaturalDarknessLightmapService.cs");

        Assert.Contains("Stopwatch.GetTimestamp()", service, StringComparison.Ordinal);
        Assert.Contains(
            "NaturalDarknessTransitionPolicy.Advance(",
            service,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "Game1.getStartingToGetDarkTime(location)",
            service,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "Game1.getTrulyDarkTime(location)",
            service,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "SmapiEnvironmentLightSnapshotProvider.ResolveLocationRule(",
            service,
            StringComparison.Ordinal
        );
        Assert.Contains("Game1.eventUp", service, StringComparison.Ordinal);
        Assert.Contains("Game1.isWarping", service, StringComparison.Ordinal);
        Assert.Contains("Game1.currentMinigame", service, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalWarpUsesTheDestinationCompletedPlanRatherThanReplayingTheTransition()
    {
        var service = ReadContract("SmapiNaturalDarknessLightmapService.cs");

        Assert.Contains("if (!e.IsLocalPlayer)", service, StringComparison.Ordinal);
        Assert.Contains(
            "ResolveScenePlan(screenId, e.NewLocation, ignoreWarping: true)",
            service,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "NaturalDarknessTransitionPolicy.GetWarpArrivalProgress(plan)",
            service,
            StringComparison.Ordinal
        );
        Assert.Contains("if (!Game1.isWarping)", service, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeEmitsBoundedSceneDiagnosticsForWarpAndPlanTransitions()
    {
        var service = ReadContract("SmapiNaturalDarknessLightmapService.cs");

        Assert.Contains("Natural darkness scene diagnostic", service, StringComparison.Ordinal);
        Assert.Contains("LogSceneDiagnostic(\"warp\"", service, StringComparison.Ordinal);
        Assert.Contains("LogCurrentLocationSceneDiagnostic(\"transition-start\"", service, StringComparison.Ordinal);
        Assert.Contains("LogCurrentLocationSceneDiagnostic(\"transition-change\"", service, StringComparison.Ordinal);
        Assert.Contains("if (previous.Plan == plan)", service, StringComparison.Ordinal);
    }

    [Fact]
    public void PatchCoversOutdoorIndoorAndMineNativeBaseLightWithoutReplacingLocalLights()
    {
        var patch = ReadContract("NaturalDarknessLightmapPatch.cs");

        Assert.Contains("nameof(Game1.DrawWorld)", patch, StringComparison.Ordinal);
        Assert.Contains("nameof(Game1.DrawLighting)", patch, StringComparison.Ordinal);
        Assert.Contains("nameof(MineShaft.getLightingColor)", patch, StringComparison.Ordinal);
        Assert.Contains("typeof(GameTime)", patch, StringComparison.Ordinal);
        Assert.Contains("typeof(RenderTarget2D)", patch, StringComparison.Ordinal);
        Assert.Contains("Color.Lerp(", patch, StringComparison.Ordinal);
        Assert.Contains("Color.White", patch, StringComparison.Ordinal);
        Assert.Contains("Game1.outdoorLight", patch, StringComparison.Ordinal);
        Assert.Contains("Game1.ambientLight", patch, StringComparison.Ordinal);
        Assert.Contains("private static void DrawWorldPrefix", patch, StringComparison.Ordinal);
        Assert.Contains("Game1.drawLighting = true", patch, StringComparison.Ordinal);
        Assert.Contains("private static IEnumerable<CodeInstruction> DrawLightingTranspiler", patch, StringComparison.Ordinal);
        Assert.Contains("list[index].Calls(mineLightingTarget)", patch, StringComparison.Ordinal);
        Assert.Contains("OpCodes.Call", patch, StringComparison.Ordinal);
        Assert.Contains("GetAdjustedMineLightingColor", patch, StringComparison.Ordinal);
        Assert.Contains("mine.getLightingColor(time)", patch, StringComparison.Ordinal);
        Assert.Contains("Natural darkness mine render diagnostic", patch, StringComparison.Ordinal);
        Assert.Contains("private static void Postfix", patch, StringComparison.Ordinal);
        Assert.Contains("private static Exception? Finalizer", patch, StringComparison.Ordinal);
        Assert.Contains("Game1.outdoorLight = state.OriginalOutdoorLight", patch, StringComparison.Ordinal);
        Assert.Contains("Game1.ambientLight = state.OriginalAmbientLight", patch, StringComparison.Ordinal);
        Assert.Contains("HarmonyPatchType.Transpiler", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("MineLightingPostfix", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.drawLighting = false", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("lightmap.GetData", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("new RenderTarget2D", patch, StringComparison.Ordinal);
    }

    [Fact]
    public void LocationPolicyKeepsSpecialMineRulesAndVolcanoExceptionExplicit()
    {
        var policy = ReadContract("NaturalDarknessLocationPolicy.cs");

        Assert.Contains("input.MineLevel == 77377", policy, StringComparison.Ordinal);
        Assert.Contains("input.MineLevel >= 1000", policy, StringComparison.Ordinal);
        Assert.Contains("input.MineLevel is >= 31 and <= 39", policy, StringComparison.Ordinal);
        Assert.Contains("NaturalDarknessProfile.MineTwoStage", policy, StringComparison.Ordinal);
        Assert.Contains("NaturalDarknessProfile.Unchanged", policy, StringComparison.Ordinal);
        Assert.Contains("IsJunimoBlessingProtected", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void ModEntryWiresTheNativeLightmapServiceToTheSanityAndDarknessToggles()
    {
        var entry = ReadContract("ModEntry.cs");

        Assert.Contains("SmapiNaturalDarknessLightmapService", entry, StringComparison.Ordinal);
        Assert.Contains("environmentLightLocationRules", entry, StringComparison.Ordinal);
        Assert.Contains("darknessAttackLocationAuthorization", entry, StringComparison.Ordinal);
        Assert.Contains("ConfigKeys.EnableNaturalDarkness", entry, StringComparison.Ordinal);
        Assert.Contains("IsNaturalDarknessRuntimeEnabled", entry, StringComparison.Ordinal);
        Assert.Contains(
            "private void ApplyNaturalDarknessRuntimeState()",
            entry,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "_naturalDarknessLightmap?.SetEnabled(enabled)",
            entry,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "_npcFlashlights?.SetEnabled(enabled)",
            entry,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void NativeMineWallSconcesUseADrawOnlyGateAndLeaveOtherLightSourcesAlone()
    {
        var service = ReadContract("SmapiNaturalDarknessLightmapService.cs");
        var patch = ReadContract("MineWallSconceRenderPatch.cs");

        Assert.Contains("MineWallSconceRenderPatch.TryInstall", service, StringComparison.Ordinal);
        Assert.Contains("MineWallSconceRenderPatch.Uninstall", service, StringComparison.Ordinal);
        Assert.Contains("GetSceneStateForScreen", service, StringComparison.Ordinal);
        Assert.Contains("nameof(LightSource.Draw)", patch, StringComparison.Ordinal);
        Assert.Contains("\"Mines_\"", patch, StringComparison.Ordinal);
        Assert.Contains("\"_5\"", patch, StringComparison.Ordinal);
        Assert.Contains("LightSource.sconceLight", patch, StringComparison.Ordinal);
        Assert.Contains("MineWallSconcePolicy.ShouldDraw", patch, StringComparison.Ordinal);
        Assert.Contains("Game1.getStartingToGetDarkTime(mine)", patch, StringComparison.Ordinal);
        Assert.Contains("if (!sceneState.IsActive)", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("sharedLights.Remove", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("currentLightSources.Remove", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("currentLightSources.Clear", patch, StringComparison.Ordinal);
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
