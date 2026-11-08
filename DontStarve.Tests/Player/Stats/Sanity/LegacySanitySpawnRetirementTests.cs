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
                "E01F2EBED94BB574B383C3155737FC2FB36968BDEE7D41E24FCB28FDC73A25BF"
            },
            {
                "SpawnDarkHand.cs",
                "DontStarve.Sanity.SpawnDarkHand",
                "0CDDDD8A8BBD74FD8CB99FA21753062512727F97A946234F08E15F45E9E076A7"
            },
            {
                "SpawnDarkWatcher.cs",
                "DontStarve.Sanity.SpawnDarkWatcher",
                "F8D93E90AD98A0D73AEA6BA4DF64E36844E2F7335F29FBDE7273DC2074B45477"
            },
            {
                "SpawnEye.cs",
                "DontStarve.Sanity.SpawnEye",
                "12D7A90B0897842A2DE957687A4C2E418D5ED69CC38282B925B5E55F56A86AD7"
            },
            {
                "SpawnCreeperFear.cs",
                "DontStarve.Sanity.SpawnCreeperFear",
                "32D00F2F5B57DC8305FADC887781E73C106E666C8520DCC710C3F483E29C92BB"
            },
            {
                "SpawnTerrifyingSharpBeak.cs",
                "DontStarve.Sanity.SpawnTerrifyingSharpBeak",
                "BD19324522068D091864CE33B2B24A35540497E81E4957C9B8B9C486DEE11BCC"
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
        Assert.Contains("new NearMonster(lifecycle)", source, StringComparison.Ordinal);
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
