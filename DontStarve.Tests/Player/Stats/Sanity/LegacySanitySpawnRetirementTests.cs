using System.Security.Cryptography;
using DontStarve.Player.Stats.Sanity;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class LegacySanitySpawnRetirementTests
{
    public static TheoryData<string, string, string> LegacyTimerSources =>
        new()
        {
            {
                "SpawnMrSkitts.cs",
                "DontStarve.Sanity.SpawnMrSkitts",
                "E377CB3818E1BB2FF65CC537488755D1DBB537A4CD7015593E4A0F8A6A97AFD2"
            },
            {
                "SpawnDarkHand.cs",
                "DontStarve.Sanity.SpawnDarkHand",
                "46D2300ACE7F0B9EFC32F326EFFFDDE70A872F493670326C908C2975B0E7ABB1"
            },
            {
                "SpawnDarkWatcher.cs",
                "DontStarve.Sanity.SpawnDarkWatcher",
                "8724D4FB2B4C18940C73AEC45024FA531F134883B3789726E2868D26950FC824"
            },
            {
                "SpawnEye.cs",
                "DontStarve.Sanity.SpawnEye",
                "B6BD68EB52B95C2925934D2B05661329E4249B44B16488E448C202F63C4909DC"
            },
            {
                "SpawnCreeperFear.cs",
                "DontStarve.Sanity.SpawnCreeperFear",
                "92DD3D16D183929919D1EB63E224A56FE0E59A1F600EA82E9EC569910592D5A7"
            },
            {
                "SpawnTerrifyingSharpBeak.cs",
                "DontStarve.Sanity.SpawnTerrifyingSharpBeak",
                "81331555A156604C8D2C2D69703EE306330B1BE550A39E1268062CF988907284"
            },
        };

    [Fact]
    public void All_six_legacy_schedulers_are_absent_from_the_only_behavior_registry()
    {
        var source = ReadContract("Sanity.cs");

        Assert.Equal(6, LegacySanitySpawnRetirement.BehaviorTypeNames.Count);
        foreach (var behaviorType in LegacySanitySpawnRetirement.BehaviorTypeNames)
        {
            Assert.DoesNotContain(
                $"new {behaviorType}(",
                source,
                StringComparison.Ordinal
            );
        }
        Assert.Contains("new NearMonster()", source, StringComparison.Ordinal);
        Assert.Contains("new MineShaft()", source, StringComparison.Ordinal);
        Assert.Contains("旧生成停用，新幻觉尚未实现", source, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(LegacyTimerSources))]
    public void Legacy_timer_source_and_save_key_remain_byte_identical(
        string fileName,
        string saveKey,
        string expectedHash
    )
    {
        var path = ContractPath(fileName);
        var source = File.ReadAllText(path);

        Assert.Contains(saveKey, source, StringComparison.Ordinal);
        Assert.Equal(
            expectedHash,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
        );
        Assert.Contains(saveKey, LegacySanitySpawnRetirement.SaveKeys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("guessed-from-type-or-position")]
    public void Unproven_residual_ownership_never_allows_deletion(string? marker)
    {
        var decision = LegacySanitySpawnRetirement.EvaluateResidualCleanup(marker);

        Assert.False(decision.CanRemove);
        Assert.Equal(
            LegacySanitySpawnRetirement.ResidualCleanupReason,
            decision.Reason
        );
    }

    [Fact]
    public void Retirement_diagnostic_does_not_claim_replacement_is_implemented()
    {
        Assert.Equal(
            "legacy-spawn-scheduling-retired",
            LegacySanitySpawnRetirement.SchedulingStatus
        );
        Assert.Equal(
            "new-hallucinations-not-implemented",
            LegacySanitySpawnRetirement.ReplacementStatus
        );
    }

    private static string ReadContract(string fileName)
    {
        return File.ReadAllText(ContractPath(fileName));
    }

    private static string ContractPath(string fileName)
    {
        return Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "LegacySpawn",
            fileName
        );
    }
}
