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
    public void RestoreBuffsUseActiveLifetimeAndHostOwnedPlayerObservations()
    {
        var fixedRestore = ReadContract("FixedRestoreBuff.cs");
        var coordinator = ReadContract("BuffMultiplayerCoordinator.cs");
        var restoreAdapters = string.Join(
            "\n",
            ReadContract("HealthRestoreBuff.cs"),
            ReadContract("StaminaRestoreBuff.cs"),
            ReadContract("SanityRestoreBuff.cs")
        );

        Assert.DoesNotContain("ReadSaveData", fixedRestore, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteSaveData", fixedRestore, StringComparison.Ordinal);
        Assert.DoesNotContain("GameLoop.Saving", fixedRestore, StringComparison.Ordinal);
        Assert.Contains("RestoreObservationReceived", fixedRestore, StringComparison.Ordinal);
        Assert.Contains("ObserveRestore", fixedRestore, StringComparison.Ordinal);
        Assert.Contains("Context.IsMainPlayer", fixedRestore, StringComparison.Ordinal);
        Assert.Contains("SessionRequestMessageType", coordinator, StringComparison.Ordinal);
        Assert.Contains("RestoreObservationMessageType", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("DontStarve.Buff.HealthRestore", restoreAdapters);
        Assert.DoesNotContain("DontStarve.Buff.StaminaRestore", restoreAdapters);
        Assert.Contains("SanityChangeSource.Buff", restoreAdapters, StringComparison.Ordinal);
    }

    [Fact]
    public void BuffManagerKeepsRestoreAdaptersOutsideTheTimeSaveSurface()
    {
        var manager = ReadContract("BuffManager.cs");

        Assert.Contains(
            "private static readonly List<INonTimeRelatedBuff> nonTimeRelatedBuffs = new();",
            manager,
            StringComparison.Ordinal
        );
        Assert.Contains("new ElectricityAppendBuff(multiplayer)", manager, StringComparison.Ordinal);
        Assert.Contains("new HealthRestoreBuff(multiplayer)", manager, StringComparison.Ordinal);
        Assert.Contains("new StaminaRestoreBuff(multiplayer)", manager, StringComparison.Ordinal);
        Assert.Contains("new SanityRestoreBuff(multiplayer)", manager, StringComparison.Ordinal);
        Assert.Contains("BuffMultiplayerCoordinator", manager, StringComparison.Ordinal);
        Assert.Contains("private static readonly List<ITimeRelatedBuff> timeRelatedBuffs = new();", manager);
    }

    [Fact]
    public void HungerReadAndWriteApisAreHostOnlyAndUsePlayerPayloads()
    {
        var hunger = ReadContract("Hunger.cs");
        var cycle = ReadContract("HungerCycle.cs");
        var runtime = string.Join(
            "\n",
            ReadContract("HungerRuntimeState.cs"),
            ReadContract("HungerSaveData.cs")
        );
        var protocol = ReadContract("HungerMultiplayerProtocol.cs");
        var coordinator = ReadContract("SmapiHungerMultiplayerCoordinator.cs");

        Assert.Contains("persistenceStore.Load", hunger, StringComparison.Ordinal);
        Assert.Contains("persistenceStore.Save", hunger, StringComparison.Ordinal);
        Assert.Contains("SmapiHungerSaveDataAccess", hunger, StringComparison.Ordinal);
        AssertHostOnlySaveAccess(
            ExtractMethod(cycle, "public void Load(IModHelper helper)"),
            "ReadSaveData"
        );
        AssertHostOnlySaveAccess(
            ExtractMethod(cycle, "public void Save(IModHelper helper)"),
            "WriteSaveData"
        );

        Assert.Contains("DontStarve.Hunger", hunger, StringComparison.Ordinal);
        Assert.Contains("DontStarve.Hunger.HungerCycle", cycle, StringComparison.Ordinal);
        Assert.Contains("HungerSaveData", hunger, StringComparison.Ordinal);
        Assert.Contains("HungerCycleSaveData", cycle, StringComparison.Ordinal);
        Assert.Contains("Dictionary<string, HungerPlayerSaveData>", runtime, StringComparison.Ordinal);
        Assert.Contains("Dictionary<string, HungerCyclePlayerSaveData>", runtime, StringComparison.Ordinal);
        Assert.Contains("UniqueMultiplayerID", hunger, StringComparison.Ordinal);
        Assert.Contains("Hunger.Snapshot.v1", coordinator, StringComparison.Ordinal);
        Assert.Contains("PeerConnected", coordinator, StringComparison.Ordinal);
        Assert.Contains("PeerDisconnected", coordinator, StringComparison.Ordinal);
        Assert.Contains("RequestFullSnapshot", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void HungerBusinessChainKeepsDoubleValuesAndRoutesFoodThroughHost()
    {
        var hunger = ReadContract("Hunger.cs");
        var cycle = ReadContract("HungerCycle.cs");
        var eatFood = ReadContract("EatFood.cs");
        var runtime = ReadContract("FoodRuleRuntime.cs");

        Assert.Contains("internal const double DefaultMaxHunger", hunger, StringComparison.Ordinal);
        Assert.DoesNotContain("internal static double farmerHunger", hunger, StringComparison.Ordinal);
        Assert.Contains("public static double GetHunger(this Farmer farmer)", hunger, StringComparison.Ordinal);
        Assert.Contains("public static void SetHunger(this Farmer farmer, double value)", hunger, StringComparison.Ordinal);
        Assert.Contains("food.ItemId", hunger, StringComparison.Ordinal);
        Assert.Contains("0.052d", cycle, StringComparison.Ordinal);
        Assert.Contains("Dictionary<string, double>", eatFood, StringComparison.Ordinal);
        Assert.Contains("Load<Dictionary<string, double>>", eatFood, StringComparison.Ordinal);
        Assert.DoesNotContain("Dictionary<string, float>", eatFood, StringComparison.Ordinal);
        Assert.DoesNotContain("(float)hunger", eatFood, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyDictionary<string, double>", runtime, StringComparison.Ordinal);
        Assert.Contains("TryGetObjectItemId(qualifiedItemId", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void NewPlayerPayloadIsReadableAndLegacySingleValueIsIgnored()
    {
        var newValue = JsonSerializer.Deserialize<NewHungerPayload>(
            "{\"SchemaVersion\":1,\"Players\":{\"123\":{\"Current\":37.5}}}"
        );
        Assert.NotNull(newValue);
        Assert.Equal(37.5d, newValue!.Players["123"].Current);

        var legacyValue = JsonSerializer.Deserialize<NewHungerPayload>(
            "{\"Hunger\":37.5}"
        );
        Assert.NotNull(legacyValue);
        Assert.Empty(legacyValue!.Players);

        var cycle = JsonSerializer.Deserialize<NewCyclePayload>(
            "{\"SchemaVersion\":3,\"Players\":{\"123\":{\"IsStarving\":true,\"Wait\":2}}}"
        );
        Assert.NotNull(cycle);
        Assert.True(cycle!.Players["123"].IsStarving);
        Assert.Equal(2L, cycle.Players["123"].Wait);

        var legacyCycle = JsonSerializer.Deserialize<NewCyclePayload>(
            "{\"SchemaVersion\":2,\"Players\":{\"123\":{\"LastHasHunger\":true,\"LastTime\":999,\"Wait\":2}}}"
        );
        Assert.NotNull(legacyCycle);
        Assert.False(legacyCycle!.Players["123"].IsStarving);
        Assert.Equal(2L, legacyCycle.Players["123"].Wait);
    }

    [Fact]
    public void HungerAndElectricityUseOwnerBoundMultiplayerMessages()
    {
        var sources = new[]
        {
            ReadContract("Hunger.cs"),
            ReadContract("HungerMultiplayerProtocol.cs"),
            ReadContract("SmapiHungerMultiplayerCoordinator.cs"),
            ReadContract("BuffMultiplayerCoordinator.cs"),
            ReadContract("ElectricityAppendBuff.cs"),
        };
        var combined = string.Join("\n", sources);

        Assert.Contains("ModMessage", combined, StringComparison.Ordinal);
        Assert.Contains("UniqueMultiplayerID", combined, StringComparison.Ordinal);
        Assert.Contains("SendFullSnapshot(e.Peer.PlayerID)", combined, StringComparison.Ordinal);
        Assert.Contains("PeerDisconnected", combined, StringComparison.Ordinal);
        Assert.Contains("ElectricityStateMessageType", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("internal static double farmerHunger", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void HungerStage03RequiresRatioMigrationAndDedicatedHealthSeam()
    {
        var cycle = ReadContract("HungerCycle.cs");
        var runtime = ReadContract("HungerRuntimeState.cs");

        Assert.Contains("MaxAtSave", runtime, StringComparison.Ordinal);
        Assert.Contains("HungerDamage", cycle, StringComparison.Ordinal);
        Assert.DoesNotContain("player.health -=", cycle, StringComparison.Ordinal);
    }

    [Fact]
    public void HungerRuntimeHasLowFrequencyZeroAndDamageDiagnostics()
    {
        var cycle = ReadContract("HungerCycle.cs");

        Assert.Contains("hunger.runtime.zero-evaluated", cycle, StringComparison.Ordinal);
        Assert.Contains("hunger.runtime.damage-evaluated", cycle, StringComparison.Ordinal);
        Assert.Contains("ZeroDiagnosticLogged", cycle, StringComparison.Ordinal);
        Assert.Contains("DamageDiagnosticLogged", cycle, StringComparison.Ordinal);
    }

    [Fact]
    public void HungerDisablePausesTheHostCycleAndNonHostStillDiscardsLocalState()
    {
        var cycle = ReadContract("HungerCycle.cs");
        var update = ExtractMethod(cycle, "public void Update(long time)");
        var nonHostBranch = ExtractBlock(
            update,
            "if (changeService.Role != HungerAuthorityRole.Host)"
        );
        var disabledBranch = ExtractBlock(update, "if (!HungerExtensions.IsEnabled)");
        var pauseMethod = ExtractBlock(cycle, "internal void Pause()");

        Assert.Contains("state.Reset()", nonHostBranch, StringComparison.Ordinal);
        Assert.Contains("state.Pause()", disabledBranch, StringComparison.Ordinal);
        Assert.Contains("timeState.Clear()", disabledBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("IsStarving =", pauseMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("IsInLowValueRegion =", pauseMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void HungerMinuteMaintenanceDoesNotUseAnAbsoluteLastTimeCursor()
    {
        var cycle = ReadContract("HungerCycle.cs");
        var save = ReadContract("HungerSaveData.cs");

        Assert.Contains("PlayerTimeBehaviorState", cycle, StringComparison.Ordinal);
        Assert.Contains("TryEnterMinute", cycle, StringComparison.Ordinal);
        Assert.DoesNotContain("state.LastTime", cycle, StringComparison.Ordinal);
        Assert.DoesNotContain("LastTime =", cycle, StringComparison.Ordinal);
        Assert.DoesNotContain("LastTime", save, StringComparison.Ordinal);
    }

    [Fact]
    public void HungerDamageExecutorAddsOnlyVanillaNumberDebrisAfterDirectHealthWrite()
    {
        var executor = ReadContract("SmapiHungerDamageExecutor.cs");

        Assert.Contains("player.health = result.RemainingHealth", executor, StringComparison.Ordinal);
        Assert.Contains("new Debris(", executor, StringComparison.Ordinal);
        Assert.Contains("result.Damage", executor, StringComparison.Ordinal);
        Assert.Contains("currentLocation?.debris.Add", executor, StringComparison.Ordinal);
        Assert.DoesNotContain("takeDamage(", executor, StringComparison.Ordinal);
        Assert.DoesNotContain("temporaryInvincibility", executor, StringComparison.Ordinal);
        Assert.DoesNotContain("playNearbySound", executor, StringComparison.Ordinal);
    }

    [Fact]
    public void HungerDeathCauseIsWiredWithoutReplacingTheVanillaDeathEvent()
    {
        var hunger = ReadContract("Hunger.cs");
        var executor = ReadContract("SmapiHungerDamageExecutor.cs");
        var dialogue = ReadContract("HungerDeathDialogueService.cs");

        Assert.Contains("HungerDeath", hunger, StringComparison.Ordinal);
        Assert.Contains("lethalDamageHandler", executor, StringComparison.Ordinal);
        Assert.Contains("player.health <= 0", executor, StringComparison.Ordinal);
        Assert.DoesNotContain("Data/Events/Hospital", hunger, StringComparison.Ordinal);
        Assert.DoesNotContain("Data/Events/Mine", hunger, StringComparison.Ordinal);
        Assert.DoesNotContain("Data/Events/IslandSouth", hunger, StringComparison.Ordinal);
        Assert.DoesNotContain("DesertFestival", hunger, StringComparison.Ordinal);
        Assert.Contains("GameLocation", dialogue, StringComparison.Ordinal);
        Assert.Contains("DefaultCommands", dialogue, StringComparison.Ordinal);
        Assert.Contains("Hospital", dialogue, StringComparison.Ordinal);
        Assert.Contains("IslandSouth", dialogue, StringComparison.Ordinal);
        Assert.Contains("Mine", dialogue, StringComparison.Ordinal);
        Assert.DoesNotContain("DesertFestival", dialogue, StringComparison.Ordinal);
        Assert.DoesNotContain("ReplaceAllCommands", dialogue, StringComparison.Ordinal);
    }

    [Fact]
    public void HungerDeathDialogueReplacesTheDialogueObjectBeforeVanillaDrawDialogue()
    {
        var dialogue = ReadContract("HungerDeathDialogueService.cs");

        Assert.Contains("nameof(Game1.drawDialogue)", dialogue, StringComparison.Ordinal);
        Assert.Contains("prefix: new HarmonyMethod(drawDialoguePrefix)", dialogue, StringComparison.Ordinal);
        Assert.Contains("speaker.CurrentDialogue.Pop()", dialogue, StringComparison.Ordinal);
        Assert.Contains("new Dialogue(speaker, null", dialogue, StringComparison.Ordinal);
    }

    [Fact]
    public void DarknessLethalDamageFeedsTheSharedDeathDialogueCause()
    {
        var dialogue = ReadContract("HungerDeathDialogueService.cs");
        var darkness = ReadDarknessContract();

        Assert.Contains("PlayerDeathCause.Darkness", dialogue, StringComparison.Ordinal);
        Assert.Contains("darkness-death.hospital.1", dialogue, StringComparison.Ordinal);
        Assert.Contains("darkness-death.hospital.2", dialogue, StringComparison.Ordinal);
        Assert.Contains("lethalDamageHandler", darkness, StringComparison.Ordinal);
        Assert.Contains("player.health <= 0", darkness, StringComparison.Ordinal);
        Assert.DoesNotContain("Data/Events/Hospital", dialogue, StringComparison.Ordinal);
    }

    private static void AssertHostOnlySaveAccess(string method, string saveApi)
    {
        var guard = method.IndexOf("!Context.IsMainPlayer", StringComparison.Ordinal);
        if (guard < 0)
            guard = method.IndexOf("Context.IsMainPlayer", StringComparison.Ordinal);
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

    private static string ExtractBlock(string source, string marker)
    {
        var markerStart = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerStart >= 0, $"Missing source marker: {marker}");

        var openingBrace = source.IndexOf('{', markerStart);
        Assert.True(openingBrace >= 0, $"Missing opening brace after: {marker}");

        var depth = 0;
        for (var index = openingBrace; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source.Substring(openingBrace, index - openingBrace + 1);
        }

        throw new InvalidOperationException($"Missing closing brace after: {marker}");
    }

    private static string ReadContract(string fileName)
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", "OnlinePersistence", fileName)
        );
    }

    private static string ReadDarknessContract(string fileName = "SmapiDarknessAttackResolutionService.cs")
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", "DarknessAttack", fileName)
        );
    }

    private sealed class NewHungerPayload
    {
        public int SchemaVersion { get; init; }

        public Dictionary<string, NewHungerPlayerPayload> Players { get; init; } = new();
    }

    private sealed class NewHungerPlayerPayload
    {
        public double Current { get; init; }
    }

    private sealed class NewCyclePayload
    {
        public int SchemaVersion { get; init; }

        public Dictionary<string, NewCyclePlayerPayload> Players { get; init; } = new();
    }

    private sealed class NewCyclePlayerPayload
    {
        public bool IsStarving { get; init; }

        public long Wait { get; init; }
    }
}
