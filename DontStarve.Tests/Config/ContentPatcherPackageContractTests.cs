using System.Text.Json;
using Xunit;

namespace DontStarve.Tests.Config;

public sealed class ContentPatcherPackageContractTests
{
    private static readonly string[] ExposedKeys =
    {
        "EnableSanitySystem",
        "SanityMonsterIntensity",
        "DarkHandMode",
        "EnableSanityVisualEffects",
        "DarknessDamageMode",
        "MonsterDifficultyProfile",
        "EnableJunimoBlessing",
    };

    private static readonly string[] CpOnlyKeys =
    {
        "Texture_version",
        "Mid_Autumn_Festival",
        "Hallowed_Nights",
        "Halloween_Candies_Drop",
        "Halloween_Candies_Strengthen",
        "Winter_Feast",
        "Winter_Food_Drop",
        "Winter_Food_Strengthen",
    };

    private static string ContractsRoot =>
        Path.Combine(AppContext.BaseDirectory, "Contracts");

    private static string WorkCopyRoot =>
        Path.Combine(ContractsRoot, "CpWorkCopy");

    [Fact]
    public void ManifestDependencyDirectionsEncodeFourInstallationCombinations()
    {
        using var main = ReadJson(Path.Combine(ContractsRoot, "MainManifest.json"));
        var mainRoot = main.RootElement;
        Assert.Equal("Yurin.DontStarve", mainRoot.GetProperty("UniqueID").GetString());
        Assert.Equal("DontStarve.dll", mainRoot.GetProperty("EntryDll").GetString());
        var mainDependency = Assert.Single(
            mainRoot.GetProperty("Dependencies").EnumerateArray()
        );
        Assert.Equal(
            "Pathoschild.ContentPatcher",
            mainDependency.GetProperty("UniqueID").GetString()
        );
        Assert.Equal("2.9.1", mainDependency.GetProperty("MinimumVersion").GetString());
        Assert.False(mainDependency.GetProperty("IsRequired").GetBoolean());

        using var work = ReadJson(Path.Combine(WorkCopyRoot, "manifest.json"));
        var workRoot = work.RootElement;
        Assert.Equal("ZG.DS", workRoot.GetProperty("UniqueID").GetString());
        Assert.Equal("1.4.1", workRoot.GetProperty("Version").GetString());
        Assert.Equal(
            "Pathoschild.ContentPatcher",
            workRoot
                .GetProperty("ContentPackFor")
                .GetProperty("UniqueID")
                .GetString()
        );
        var workDependency = Assert.Single(
            workRoot.GetProperty("Dependencies").EnumerateArray()
        );
        Assert.Equal("Yurin.DontStarve", workDependency.GetProperty("UniqueID").GetString());
        Assert.True(workDependency.GetProperty("IsRequired").GetBoolean());
    }

    [Fact]
    public void WorkCopyPreservesEightCpOnlyKeysAndAddsOnlyNamespacedWhenKeys()
    {
        using var config = ReadJson(Path.Combine(WorkCopyRoot, "config.json"));
        Assert.Equal(
            CpOnlyKeys,
            config.RootElement.EnumerateObject().Select(property => property.Name)
        );

        using var content = ReadJson(Path.Combine(WorkCopyRoot, "content.json"));
        var root = content.RootElement;
        Assert.Equal("2.3.0", root.GetProperty("Format").GetString());
        Assert.Equal(
            CpOnlyKeys,
            root.GetProperty("ConfigSchema")
                .EnumerateObject()
                .Select(property => property.Name)
        );

        var bridge = Assert.Single(
            root.GetProperty("Changes")
                .EnumerateArray()
                .Where(
                    change =>
                        change.TryGetProperty("FromFile", out var fromFile)
                        && fromFile.GetString() == "assets/object/Changes/Locations.json"
                )
        );
        Assert.Equal("Include", bridge.GetProperty("Action").GetString());
        Assert.Equal(
            "{{i18n:sanity4.token-bridge.log-name}}",
            bridge.GetProperty("LogName").GetString()
        );

        var when = bridge.GetProperty("When");
        var expectedNames = ExposedKeys.Select(key => $"Yurin.DontStarve/{key}");
        Assert.Equal(expectedNames, when.EnumerateObject().Select(property => property.Name));
        Assert.All(
            when.EnumerateObject(),
            condition =>
            {
                Assert.StartsWith("Yurin.DontStarve/", condition.Name, StringComparison.Ordinal);
                Assert.DoesNotContain("{{", condition.Name, StringComparison.Ordinal);
                Assert.DoesNotContain("EnableDawnDuskMusic", condition.Name, StringComparison.Ordinal);
            }
        );
        Assert.Equal("true", when.GetProperty("Yurin.DontStarve/EnableSanitySystem").GetString());
        Assert.Equal("Default", when.GetProperty("Yurin.DontStarve/SanityMonsterIntensity").GetString());
        Assert.Equal("FireThief", when.GetProperty("Yurin.DontStarve/DarkHandMode").GetString());
        Assert.Equal("true", when.GetProperty("Yurin.DontStarve/EnableSanityVisualEffects").GetString());
        Assert.Equal("Default", when.GetProperty("Yurin.DontStarve/DarknessDamageMode").GetString());
        Assert.Equal("Compatible", when.GetProperty("Yurin.DontStarve/MonsterDifficultyProfile").GetString());
        Assert.Equal("false", when.GetProperty("Yurin.DontStarve/EnableJunimoBlessing").GetString());
    }

    [Fact]
    public void WorkCopyBridgeLogNameExistsInBothLanguages()
    {
        using var english = ReadJson(
            Path.Combine(WorkCopyRoot, "i18n", "default.json")
        );
        using var chinese = ReadJson(Path.Combine(WorkCopyRoot, "i18n", "zh.json"));

        Assert.False(
            string.IsNullOrWhiteSpace(
                english.RootElement
                    .GetProperty("sanity4.token-bridge.log-name")
                    .GetString()
            )
        );
        Assert.False(
            string.IsNullOrWhiteSpace(
                chinese.RootElement
                    .GetProperty("sanity4.token-bridge.log-name")
                    .GetString()
            )
        );
    }

    [Fact]
    public void WorkCopyBridgeIncludesOneLegalNoGameplayCustomAssetLoad()
    {
        var changesRoot = Path.Combine(
            WorkCopyRoot,
            "assets",
            "object",
            "Changes"
        );
        using var included = ReadJson(Path.Combine(changesRoot, "Locations.json"));
        var patch = Assert.Single(
            included.RootElement.GetProperty("Changes").EnumerateArray()
        );

        Assert.Equal("Load", patch.GetProperty("Action").GetString());
        Assert.Equal(
            "Mods/ZG.DS/Sanity4TokenBridge",
            patch.GetProperty("Target").GetString()
        );
        Assert.Equal(
            "assets/object/Changes/Sanity4TokenBridge.json",
            patch.GetProperty("FromFile").GetString()
        );

        using var marker = ReadJson(
            Path.Combine(changesRoot, "Sanity4TokenBridge.json")
        );
        Assert.Equal(1, marker.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(
            "Sanity4TokenBridge",
            marker.RootElement.GetProperty("Purpose").GetString()
        );
        Assert.False(marker.RootElement.GetProperty("GameplayEffects").GetBoolean());
    }

    private static JsonDocument ReadJson(string path)
    {
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
