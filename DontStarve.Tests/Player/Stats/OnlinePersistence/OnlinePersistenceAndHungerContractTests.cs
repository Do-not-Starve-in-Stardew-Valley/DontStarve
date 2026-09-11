using System.Text.Json;
using Xunit;

namespace DontStarve.Tests.Player.Stats.OnlinePersistence;

public sealed class FixedRestoreBuffTimingTests
{
    [Fact]
    public void NewLifetimeEmitsOnlyNewlyDuePulses()
    {
        var timing = new DontStarve.Buff.Buffs.FixedRestoreBuffTiming();

        Assert.Equal(0, timing.Observe("DS_Heal_Health", 252000, 252000));
        Assert.Equal(1, timing.Observe("DS_Heal_Health", 252000, 245000));
        Assert.Equal(0, timing.Observe("DS_Heal_Health", 252000, 244500));
        Assert.Equal(1, timing.Observe("DS_Heal_Health", 252000, 238000));
    }

    [Fact]
    public void ReferenceHealthDurationReleasesItsThirtySixPulseBudgetOnlyOnce()
    {
        var timing = new DontStarve.Buff.Buffs.FixedRestoreBuffTiming();

        Assert.Equal(0, timing.Observe("DS_Heal_Health", 252000, 252000));
        Assert.Equal(36, timing.Observe("DS_Heal_Health", 252000, 1000));
        Assert.Equal(0, timing.Observe("DS_Heal_Health", 252000, 1000));
    }

    [Fact]
    public void ReopenedLifetimeAlignsPastTimeWithoutBackfill()
    {
        var timing = new DontStarve.Buff.Buffs.FixedRestoreBuffTiming();

        Assert.Equal(0, timing.Observe("DS_Heal_Health", 252000, 126000));
        Assert.Equal(0, timing.Observe("DS_Heal_Health", 252000, 125000));
        Assert.Equal(1, timing.Observe("DS_Heal_Health", 252000, 119000));
    }

    [Fact]
    public void ReapplyAndExpiryStartFreshLifetimesWithoutDuplicatingPastPulses()
    {
        var timing = new DontStarve.Buff.Buffs.FixedRestoreBuffTiming();

        Assert.Equal(0, timing.Observe("DS_Heal_Health", 252000, 252000));
        Assert.Equal(1, timing.Observe("DS_Heal_Health", 252000, 245000));
        Assert.Equal(0, timing.Observe("DS_Heal_Health", 252000, 250000));
        Assert.Equal(1, timing.Observe("DS_Heal_Health", 252000, 243000));
        Assert.Equal(0, timing.Observe("DS_Heal_Health", 252000, 0));
        Assert.Equal(0, timing.Observe("DS_Heal_Health", 252000, 252000));
        Assert.Equal(1, timing.Observe("DS_Heal_Health", 252000, 245000));
    }
}

public sealed class OnlinePersistenceAndHungerContractTests
{
    [Fact]
    public void RestoreBuffsUseActiveLifetimeAndDoNotRegisterSaveDataCallbacks()
    {
        var fixedRestore = ReadContract("FixedRestoreBuff.cs");
        var restoreAdapters = string.Join(
            "\n",
            ReadContract("HealthRestoreBuff.cs"),
            ReadContract("StaminaRestoreBuff.cs"),
            ReadContract("SanityRestoreBuff.cs")
        );

        Assert.DoesNotContain("ReadSaveData", fixedRestore, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteSaveData", fixedRestore, StringComparison.Ordinal);
        Assert.DoesNotContain("GameLoop.Saving", fixedRestore, StringComparison.Ordinal);
        Assert.Contains(
            "SaveLoaded += (_, _) => ResetTracking()",
            fixedRestore,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "ReturnedToTitle += (_, _) => ResetTracking()",
            fixedRestore,
            StringComparison.Ordinal
        );
        Assert.Contains("timing.Observe", fixedRestore, StringComparison.Ordinal);
        Assert.DoesNotContain("DontStarve.Buff.HealthRestore", restoreAdapters);
        Assert.DoesNotContain("DontStarve.Buff.StaminaRestore", restoreAdapters);
        Assert.DoesNotContain("DontStarve.Buff.SanityRestore", restoreAdapters);
    }

    [Fact]
    public void BuffManagerKeepsRestoreAdaptersOutsideTheTimeSaveSurface()
    {
        var manager = ReadContract("BuffManager.cs");

        Assert.Contains("private static readonly List<ITimeRelatedBuff> timeRelatedBuffs = new();", manager);
        Assert.Contains("if (timeRelatedBuffs.Count > 0)", manager, StringComparison.Ordinal);
        Assert.Contains("new HealthRestoreBuff()", manager, StringComparison.Ordinal);
        Assert.Contains("new StaminaRestoreBuff()", manager, StringComparison.Ordinal);
        Assert.Contains("new SanityRestoreBuff()", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void HungerReadAndWriteApisAreGuardedAtTheActualCallSites()
    {
        var hunger = ReadContract("Hunger.cs");
        var cycle = ReadContract("HungerCycle.cs");

        AssertHostOnlySaveAccess(
            ExtractMethod(hunger, "private static void Load(IModHelper helper)"),
            "ReadSaveData"
        );
        AssertHostOnlySaveAccess(
            ExtractMethod(hunger, "private static void Save(IModHelper helper)"),
            "WriteSaveData"
        );
        AssertHostOnlySaveAccess(
            ExtractMethod(cycle, "public void Load(IModHelper helper)"),
            "ReadSaveData"
        );
        AssertHostOnlySaveAccess(
            ExtractMethod(cycle, "public void Save(IModHelper helper)"),
            "WriteSaveData"
        );

        Assert.Contains(
            "ReturnedToTitle += (_, _) => ResetRuntime()",
            hunger,
            StringComparison.Ordinal
        );
        Assert.Contains("ResetRuntime();", ExtractMethod(hunger, "private static void Load(IModHelper helper)"));
        Assert.Contains("DontStarve.Hunger", hunger, StringComparison.Ordinal);
        Assert.Contains("DontStarve.Hunger.HungerCycle", cycle, StringComparison.Ordinal);
        Assert.Contains("new HungerData", hunger, StringComparison.Ordinal);
        Assert.Contains("new HungerCycleData", cycle, StringComparison.Ordinal);
    }

    [Fact]
    public void HungerBusinessChainUsesDoubleWithoutEarlyFloatNarrowing()
    {
        var hunger = ReadContract("Hunger.cs");
        var cycle = ReadContract("HungerCycle.cs");
        var eatFood = ReadContract("EatFood.cs");
        var runtime = ReadContract("FoodRuleRuntime.cs");

        Assert.Contains("public double? Hunger", hunger, StringComparison.Ordinal);
        Assert.Contains("internal const double DefaultMaxHunger", hunger, StringComparison.Ordinal);
        Assert.Contains("internal static double farmerHunger", hunger, StringComparison.Ordinal);
        Assert.Contains("public static double GetHunger", hunger, StringComparison.Ordinal);
        Assert.Contains("public static void SetHunger(this Farmer farmer, double value)", hunger, StringComparison.Ordinal);
        Assert.Contains("0.052d", cycle, StringComparison.Ordinal);
        Assert.Contains("Dictionary<string, double>", eatFood, StringComparison.Ordinal);
        Assert.Contains("Load<Dictionary<string, double>>", eatFood, StringComparison.Ordinal);
        Assert.DoesNotContain("Dictionary<string, float>", eatFood, StringComparison.Ordinal);
        Assert.DoesNotContain("(float)hunger", eatFood, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyDictionary<string, double>", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("IReadOnlyDictionary<string, float>", runtime, StringComparison.Ordinal);
        Assert.Contains("!IsObjectItem(item)", runtime, StringComparison.Ordinal);
        Assert.Contains("TryGetObjectItemId(qualifiedItemId", runtime, StringComparison.Ordinal);
        Assert.Contains("const string objectPrefix = \"(O)\"", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingFoodNumbersAndLegacyPayloadShapesRemainReadableAsDouble()
    {
        using var food = JsonDocument.Parse(
            ReadContract("food.json"),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            }
        );

        var root = food.RootElement;
        Assert.Equal(456, root.EnumerateObject().Count());
        Assert.Equal(9.375d, root.GetProperty("447").GetDouble());
        Assert.Equal(4.6875d, root.GetProperty("812").GetDouble());
        Assert.Equal(9.375d, root.GetProperty("DS_Dragon_Fruit").GetDouble());
        Assert.False(root.TryGetProperty("DS_Dragon Fruit", out _));
        Assert.False(root.TryGetProperty("DS_Halved_Coconut", out _));
        Assert.Contains(root.EnumerateObject(), property => property.Value.GetDouble() == 0d);
        Assert.Contains(root.EnumerateObject(), property => property.Value.GetDouble() < 0d);
        Assert.Contains(root.EnumerateObject(), property => property.Value.GetDouble() % 1d != 0d);

        using var sanityFood = JsonDocument.Parse(
            ReadContract("sanity-food.json"),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            }
        );
        Assert.Equal(0d, sanityFood.RootElement.GetProperty("DS_Dragon_Fruit").GetDouble());
        Assert.False(sanityFood.RootElement.TryGetProperty("DS_Dragon Fruit", out _));
        Assert.False(sanityFood.RootElement.TryGetProperty("DS_Halved_Coconut", out _));

        var oldValue = JsonSerializer.Deserialize<DoubleHungerPayload>("{\"Hunger\":37.5}");
        var missingValue = JsonSerializer.Deserialize<DoubleHungerPayload>("{}");
        Assert.Equal(37.5d, oldValue?.Hunger);
        Assert.Null(missingValue?.Hunger);

        var oldCycle = JsonSerializer.Deserialize<HungerCyclePayload>(
            "{\"LastHasHunger\":true,\"LastTime\":123,\"Wait\":2}"
        );
        var missingCycle = JsonSerializer.Deserialize<HungerCyclePayload>("{}");
        Assert.NotNull(oldCycle);
        Assert.True(oldCycle.LastHasHunger);
        Assert.Equal(123L, oldCycle.LastTime);
        Assert.Equal(2L, oldCycle.Wait);
        Assert.NotNull(missingCycle);
        Assert.False(missingCycle.LastHasHunger);
        Assert.Equal(0L, missingCycle.LastTime);
        Assert.Equal(0L, missingCycle.Wait);
    }

    [Fact]
    public void HungerAndBuffSourcesDoNotAddTheOutOfScopeMultiplayerProtocol()
    {
        var sources = new[]
        {
            ReadContract("FixedRestoreBuff.cs"),
            ReadContract("Hunger.cs"),
            ReadContract("HungerCycle.cs"),
            ReadContract("EatFood.cs"),
        };
        var combined = string.Join("\n", sources);

        Assert.DoesNotContain("ModMessage", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("UniqueMultiplayerID", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("late join", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reconnect", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("断线重连", combined, StringComparison.Ordinal);
    }

    private static void AssertHostOnlySaveAccess(string method, string saveApi)
    {
        var guard = method.IndexOf("!Context.IsMainPlayer", StringComparison.Ordinal);
        var call = method.IndexOf(saveApi, StringComparison.Ordinal);

        Assert.True(guard >= 0, $"Missing Context.IsMainPlayer guard before {saveApi}.");
        Assert.True(call > guard, $"{saveApi} is not after the host-only guard.");
        Assert.Contains("return;", method, StringComparison.Ordinal);
    }

    private static string ExtractMethod(string source, string signature)
    {
        var signatureStart = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(signatureStart >= 0, $"Missing method signature: {signature}");

        var openingBrace = source.IndexOf('{', signatureStart);
        Assert.True(openingBrace >= 0, $"Missing opening brace: {signature}");

        var depth = 0;
        for (var index = openingBrace; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source.Substring(openingBrace, index - openingBrace + 1);
        }

        throw new InvalidOperationException($"Missing closing brace: {signature}");
    }

    private static string ReadContract(string fileName)
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", "OnlinePersistence", fileName)
        );
    }

    private sealed class DoubleHungerPayload
    {
        public double? Hunger { get; init; }
    }

    private sealed class HungerCyclePayload
    {
        public bool LastHasHunger { get; init; }
        public long LastTime { get; init; }
        public long Wait { get; init; }
    }
}
