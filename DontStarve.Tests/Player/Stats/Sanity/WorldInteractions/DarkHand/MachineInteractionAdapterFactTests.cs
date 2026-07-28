using System.Text.Json;
using DontStarve.Player.Stats.Sanity.WorldInteractions.DarkHand;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.WorldInteractions.DarkHand;

public sealed class MachineInteractionAdapterFactTests
{
    private static string ShippedCatalogPath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "ShippedMod",
            "Asset",
            "Sanity",
            "Data",
            "dark-hand-machines.json"
        );

    private static string AdapterSourcePath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "MachineInteraction",
            "SmapiMachineInteractionAdapter.cs"
        );

    [Fact]
    public void ShippedCatalogEnablesOnlyVerifiedFarmLoomTransactions()
    {
        var catalog = LoadShipped();

        Assert.True(catalog.IsAvailable);
        Assert.Equal(2, catalog.SchemaVersion);
        Assert.Equal(8, catalog.Targets.Count);
        Assert.Equal(1, catalog.EnabledTargetCount);
        var loom = Assert.Single(catalog.Targets.Where(target => target.Enabled));
        Assert.Equal("(BC)17", loom.QualifiedItemId);
        Assert.Equal(new[] { "Farm" }, loom.LocationAllowlist);
        Assert.True(loom.AllowDelay);
        Assert.True(loom.AllowThief);
        Assert.All(catalog.Targets, target => Assert.False(target.AllowEject));
        Assert.All(
            catalog.Targets.Where(target => !ReferenceEquals(target, loom)),
            target => Assert.False(target.Enabled)
        );
    }

    [Fact]
    public void VerifiedShippedCapabilityAllowsDelayButKeepsEjectClosed()
    {
        var capability = MachineInteractionCapabilityGate.Evaluate(
            LoadShipped(),
            MachineRuntimeEvidence.VerifiedStardew1615
        );

        Assert.Equal(
            MachineInteractionCapabilityStatus.Available,
            capability.Status
        );
        Assert.Equal(MachineInteractionReasonIds.Eligible, capability.Reason);
        Assert.Equal(8, capability.CandidateTargetCount);
        Assert.Equal(1, capability.EnabledTargetCount);
        Assert.True(capability.CanCaptureSnapshot);
        Assert.True(capability.CanCommitDelay);
        Assert.False(capability.CanCommitEject);
    }

    [Fact]
    public void CapabilityOrdersRevisionAndSafeLandingEvidenceWithoutGuessing()
    {
        var catalog = EnabledCatalog(
            "(BC)17",
            MachineInteractionCategory.OrdinarySingleInputFinite,
            allowDelay: true,
            allowEject: true
        );
        var noRevision = MachineInteractionCapabilityGate.Evaluate(
            catalog,
            new MachineRuntimeEvidence(false, false)
        );
        var noLanding = MachineInteractionCapabilityGate.Evaluate(
            catalog,
            new MachineRuntimeEvidence(true, false)
        );
        var available = MachineInteractionCapabilityGate.Evaluate(
            catalog,
            new MachineRuntimeEvidence(true, true)
        );

        Assert.Equal(
            MachineInteractionCapabilityStatus.ReadOnlyAuthorityRevisionUnavailable,
            noRevision.Status
        );
        Assert.False(noRevision.CanCommitDelay);
        Assert.Equal(
            MachineInteractionCapabilityStatus.ReadOnlySafeLandingUnavailable,
            noLanding.Status
        );
        Assert.True(noLanding.CanCommitDelay);
        Assert.False(noLanding.CanCommitEject);
        Assert.Equal(MachineInteractionCapabilityStatus.Available, available.Status);
        Assert.True(available.CanCommitDelay);
        Assert.True(available.CanCommitEject);
    }

    [Fact]
    public void IronAnvilIdentityIsExactAndPermanentlyExcludedBySchema()
    {
        var anvil = LoadShipped().Find(MachineTargetCatalog.IronAnvilQualifiedItemId);

        Assert.NotNull(anvil);
        Assert.Equal("(BC)Anvil", anvil!.QualifiedItemId);
        Assert.Equal(MachineInteractionCategory.Unsupported, anvil.ExpectedCategory);
        Assert.True(anvil.Excluded);
        Assert.Equal("dark-hand.machine.target.iron-anvil-excluded", anvil.Reason);
        Assert.Null(LoadShipped().Find("(BC)Anvil*"));
        Assert.Null(LoadShipped().Find("Anvil"));
    }

    [Fact]
    public void CatalogFreezesVanillaAndProjectCpRepresentativeClasses()
    {
        var catalog = LoadShipped();

        AssertExpected(catalog, "(BC)17", MachineInteractionCategory.OrdinarySingleInputFinite);
        AssertExpected(catalog, "(BC)21", MachineInteractionCategory.PermanentInput);
        AssertExpected(catalog, "(BC)10", MachineInteractionCategory.NoInputInfiniteOutput);
        AssertExpected(catalog, "(BC)13", MachineInteractionCategory.MultiInput);
        AssertExpected(catalog, "(BC)101", MachineInteractionCategory.Unsupported);
        AssertExpected(
            catalog,
            "(BC)DS_Lureplant",
            MachineInteractionCategory.NoInputInfiniteOutput
        );
        AssertExpected(
            catalog,
            "(BC)DS_Halloween_Candy_Machine",
            MachineInteractionCategory.MultiInput
        );
    }

    [Fact]
    public void StrictCatalogRejectsUnknownFieldsVersionsWildcardsAndDuplicates()
    {
        var shipped = File.ReadAllText(ShippedCatalogPath);

        AssertLoadFailure(
            shipped.Replace(
                "\"Targets\": [",
                "\"Unexpected\": true, \"Targets\": [",
                StringComparison.Ordinal
            ),
            MachineInteractionReasonIds.CatalogRootInvalid
        );
        AssertLoadFailure(
            shipped.Replace("\"SchemaVersion\": 2", "\"SchemaVersion\": 1"),
            MachineInteractionReasonIds.CatalogVersionUnsupported
        );
        AssertLoadFailure(
            shipped.Replace("(BC)17", "(BC)*", StringComparison.Ordinal),
            MachineInteractionReasonIds.TargetInvalid
        );
        AssertLoadFailure(
            shipped.Replace("(BC)21", "(BC)17", StringComparison.Ordinal),
            MachineInteractionReasonIds.TargetQualifiedIdDuplicated
        );
        AssertLoadFailure(
            ReplaceFirst(shipped, "\"Enabled\": false", "\"Enabled\": true"),
            MachineInteractionReasonIds.TargetInvalid
        );
    }

    [Theory]
    [InlineData(null, MachineInteractionReasonIds.CatalogJsonEmpty)]
    [InlineData("", MachineInteractionReasonIds.CatalogJsonEmpty)]
    [InlineData("{", MachineInteractionReasonIds.CatalogJsonMalformed)]
    [InlineData("[]", MachineInteractionReasonIds.CatalogRootInvalid)]
    public void CatalogFailsClosedForMissingOrMalformedData(string? json, string reason)
    {
        AssertLoadFailure(json, reason);
    }

    [Theory]
    [InlineData("(BC)17", "OrdinarySingleInputFinite", 1, false, 240, -1)]
    [InlineData("(BC)21", "PermanentInput", 1, false, 5000, -1)]
    [InlineData("(BC)13", "MultiInput", 5, true, 30, -1)]
    public void RunningFixturesClassifyFiniteInputTopology(
        string qualifiedItemId,
        string expectedText,
        int requiredCount,
        bool hasAdditional,
        int ruleMinutes,
        int ruleDays
    )
    {
        var expected = Enum.Parse<MachineInteractionCategory>(expectedText);
        var triggers = MachineTriggerKinds.ItemPlacedInMachine;
        if (expected == MachineInteractionCategory.PermanentInput)
            triggers |= MachineTriggerKinds.OutputCollected;
        var observation = Observation(
            qualifiedItemId: qualifiedItemId,
            data: Data(
                rule: Rule(
                    triggers: triggers,
                    requiredCount: requiredCount,
                    minutes: ruleMinutes,
                    days: ruleDays
                ),
                hasAdditional: hasAdditional
            )
        );

        var snapshot = CaptureShipped(observation);

        Assert.Equal(MachineLifecycleState.Running, snapshot.State);
        Assert.Equal(expected, snapshot.Category);
        Assert.False(snapshot.CanDelay);
        Assert.False(snapshot.CanEject);
        Assert.Equal(
            qualifiedItemId == "(BC)17"
                ? MachineInteractionReasonIds.AuthorityRevisionUnavailable
                : MachineInteractionReasonIds.TargetDisabled,
            snapshot.Reason
        );
    }

    [Theory]
    [InlineData("(BC)10")]
    [InlineData("(BC)DS_Lureplant")]
    public void AmbientOutputFixturesClassifyNoInputInfinite(string qualifiedItemId)
    {
        var observation = Observation(
            qualifiedItemId: qualifiedItemId,
            data: Data(
                rule: Rule(
                    triggers: MachineTriggerKinds.OutputCollected
                        | MachineTriggerKinds.MachinePutDown
                        | MachineTriggerKinds.DayUpdate,
                    minutes: -1,
                    days: 2,
                    recalculate: true
                ),
                clearCondition: true
            )
        );

        var snapshot = CaptureShipped(observation);

        Assert.Equal(MachineInteractionCategory.NoInputInfiniteOutput, snapshot.Category);
        Assert.Equal(MachineScheduleKind.DayBased, snapshot.Schedule);
        Assert.False(snapshot.CanDelay);
        Assert.False(snapshot.CanEject);
    }

    [Fact]
    public void ProjectCandyMachineAdditionalFuelClassifiesMultiInput()
    {
        var snapshot = CaptureShipped(
            Observation(
                qualifiedItemId: "(BC)DS_Halloween_Candy_Machine",
                data: Data(hasAdditional: true)
            )
        );

        Assert.Equal(MachineInteractionCategory.MultiInput, snapshot.Category);
        Assert.False(snapshot.LastInputIsCompleteRecoveryImage);
    }

    [Fact]
    public void EmptyAndReadyAreExplicitLifecycleStates()
    {
        var empty = Observation(minutesUntilReady: 0, lastRuleId: string.Empty) with
        {
            HeldOutput = null,
            LastInputItem = null,
            Data = Data(includeRule: false),
        };
        var ready = Observation(minutesUntilReady: 0, ready: true);

        Assert.Equal(MachineLifecycleState.Empty, CaptureShipped(empty).State);
        Assert.Equal(MachineLifecycleState.Ready, CaptureShipped(ready).State);
    }

    [Fact]
    public void ReadyWithoutHeldOutputIsDetectedAndCannotLandOrEject()
    {
        var catalog = EnabledCatalog(
            "(BC)17",
            MachineInteractionCategory.OrdinarySingleInputFinite,
            allowDelay: true,
            allowEject: true
        );
        var observation = Observation(ready: true, minutesUntilReady: 0) with
        {
            HeldOutput = null,
        };
        var snapshot = MachineInteractionClassifier.Capture(
            observation,
            catalog,
            new MachineRuntimeEvidence(true, true)
        );

        Assert.Equal(MachineLifecycleState.Ready, snapshot.State);
        Assert.Equal(MachineInteractionReasonIds.ReadyOutputMissing, snapshot.Reason);
        Assert.False(snapshot.CanLandHeldOutput);
        Assert.False(snapshot.CanEject);
    }

    [Fact]
    public void OvernightFixtureIsClassifiedButNeverGrantedMinuteDelay()
    {
        var catalog = EnabledCatalog(
            "(BC)17",
            MachineInteractionCategory.OrdinarySingleInputFinite,
            allowDelay: true
        );
        var observation = Observation(data: Data(overnight: true));
        var snapshot = MachineInteractionClassifier.Capture(
            observation,
            catalog,
            new MachineRuntimeEvidence(true, true)
        );

        Assert.Equal(MachineScheduleKind.OvernightOnly, snapshot.Schedule);
        Assert.False(snapshot.CanDelay);
    }

    [Fact]
    public void IncubatorSpecialStateAndAnvilAreUnsupported()
    {
        var incubator = CaptureShipped(
            Observation(qualifiedItemId: "(BC)101", data: Data(incubator: true, overnight: true))
        );
        var anvil = CaptureShipped(
            Observation(
                qualifiedItemId: "(BC)Anvil",
                data: Data(hasAdditional: true, rule: Rule(customOutput: true))
            )
        );

        Assert.Equal(MachineInteractionCategory.Unsupported, incubator.Category);
        Assert.Equal(MachineInteractionReasonIds.TargetExcluded, incubator.Reason);
        Assert.Equal(MachineInteractionCategory.Unsupported, anvil.Category);
        Assert.Equal(MachineInteractionReasonIds.TargetExcluded, anvil.Reason);
    }

    [Theory]
    [InlineData("(BC)SomeModdedMachine")]
    [InlineData("(O)12")]
    [InlineData("")]
    public void UnknownModdedOrNonMachineIdsAreSkipped(string qualifiedItemId)
    {
        var snapshot = CaptureShipped(Observation(qualifiedItemId: qualifiedItemId));

        Assert.Equal(MachineInteractionCategory.Unsupported, snapshot.Category);
        Assert.Equal(MachineInteractionReasonIds.TargetNotAllowlisted, snapshot.Reason);
        Assert.False(snapshot.CanDelay);
        Assert.False(snapshot.CanEject);
    }

    [Fact]
    public void MissingMachineDataAndMissingActiveRuleFailClosed()
    {
        var noData = CaptureShipped(Observation() with { Data = null });
        var noRule = CaptureShipped(Observation(data: Data(includeRule: false)));

        Assert.Equal(MachineInteractionReasonIds.DataUnavailable, noData.Reason);
        Assert.Equal(MachineInteractionCategory.Unsupported, noData.Category);
        Assert.Equal(MachineInteractionCategory.Unsupported, noRule.Category);
        Assert.False(noRule.CanDelay);
    }

    [Fact]
    public void LastInputIsEvidenceOnlyAndNeverACompleteRecoveryImage()
    {
        var catalog = EnabledCatalog(
            "(BC)17",
            MachineInteractionCategory.OrdinarySingleInputFinite,
            allowDelay: true,
            allowEject: true
        );
        var snapshot = MachineInteractionClassifier.Capture(
            Observation(),
            catalog,
            new MachineRuntimeEvidence(true, true)
        );

        Assert.NotNull(snapshot.LastInputItem);
        Assert.False(snapshot.LastInputIsCompleteRecoveryImage);
        Assert.True(snapshot.CanDelay);
        Assert.True(snapshot.CanEject);
    }

    [Fact]
    public void ReadyOutputNeedsInjectedSafeLandingBeforeEjectionCapability()
    {
        var catalog = EnabledCatalog(
            "(BC)17",
            MachineInteractionCategory.OrdinarySingleInputFinite,
            allowEject: true
        );
        var observation = Observation(ready: true, minutesUntilReady: 0);
        var unavailable = MachineInteractionClassifier.Capture(
            observation,
            catalog,
            new MachineRuntimeEvidence(true, false)
        );
        var available = MachineInteractionClassifier.Capture(
            observation,
            catalog,
            new MachineRuntimeEvidence(true, true)
        );

        Assert.Equal(MachineInteractionReasonIds.SafeLandingUnavailable, unavailable.Reason);
        Assert.False(unavailable.CanLandHeldOutput);
        Assert.False(unavailable.CanEject);
        Assert.True(available.CanLandHeldOutput);
        Assert.True(available.CanEject);
    }

    [Fact]
    public void CurrentRuntimeEvidenceCannotGrantOperationsEvenToSyntheticEnabledTarget()
    {
        var snapshot = MachineInteractionClassifier.Capture(
            Observation(),
            EnabledCatalog(
                "(BC)17",
                MachineInteractionCategory.OrdinarySingleInputFinite,
                allowDelay: true,
                allowEject: true
            ),
            MachineRuntimeEvidence.Current
        );

        Assert.Equal(MachineInteractionReasonIds.AuthorityRevisionUnavailable, snapshot.Reason);
        Assert.False(snapshot.CanDelay);
        Assert.False(snapshot.CanEject);
    }

    [Fact]
    public void FingerprintIncludesEveryFrozenMachineStateFieldButNotAuthorityRevision()
    {
        var baseline = Observation(authorityRevision: 1);
        var variants = new[]
        {
            baseline with { LastOutputRuleId = "OtherRule" },
            baseline with { MinutesUntilReady = 99 },
            baseline with { ReadyForHarvest = true },
            baseline with { ShowNextIndex = true },
            baseline with { HeldOutput = new MachineItemFacts("(O)2", 1, 0, false) },
            baseline with { LastInputItem = new MachineItemFacts("(O)3", 1, 0, false) },
            baseline with { Data = Data(hasAdditional: true) },
        };
        var fingerprint = MachineSnapshotFingerprint.Compute(baseline);

        Assert.Equal(64, fingerprint.Length);
        Assert.All(
            variants,
            variant => Assert.NotEqual(fingerprint, MachineSnapshotFingerprint.Compute(variant))
        );
        Assert.Equal(
            fingerprint,
            MachineSnapshotFingerprint.Compute(baseline with { AuthorityRevision = 999 })
        );
    }

    [Fact]
    public void RegistryRequiresInjectedPositiveMonotonicRevision()
    {
        var registry = new MachineSnapshotRegistry();
        var revision7 = EligibleSnapshot(7);
        var revision8 = EligibleSnapshot(8, minutesUntilReady: 200);

        Assert.Equal(
            MachineSnapshotRegistryUpdateStatus.Accepted,
            registry.Upsert(revision7).Status
        );
        Assert.Equal(
            MachineSnapshotRegistryUpdateStatus.IgnoredDuplicate,
            registry.Upsert(revision7).Status
        );
        Assert.Equal(
            MachineInteractionReasonIds.RegistrySnapshotStale,
            registry.Upsert(EligibleSnapshot(6)).Reason
        );
        Assert.Equal(
            MachineInteractionReasonIds.RegistrySnapshotConflict,
            registry.Upsert(revision7 with { MinutesUntilReady = 1 }).Reason
        );
        Assert.Equal(
            MachineSnapshotRegistryUpdateStatus.Accepted,
            registry.Upsert(revision8).Status
        );
        Assert.Equal(
            MachineInteractionReasonIds.RegistrySnapshotInvalid,
            registry.Upsert(EligibleSnapshot(0)).Reason
        );
    }

    [Fact]
    public void RegistryDetectsMultiplayerDriftAgainstRevisionAndFingerprint()
    {
        var registry = new MachineSnapshotRegistry();
        var current = EligibleSnapshot(12);
        Assert.Equal(MachineSnapshotRegistryUpdateStatus.Accepted, registry.Upsert(current).Status);

        Assert.Equal(
            MachineInteractionReasonIds.SnapshotCurrent,
            registry.ValidateCurrent(current.TargetId, 12, current.StateFingerprint)
        );
        Assert.Equal(
            MachineInteractionReasonIds.SnapshotDrifted,
            registry.ValidateCurrent(current.TargetId, 11, current.StateFingerprint)
        );
        Assert.Equal(
            MachineInteractionReasonIds.SnapshotDrifted,
            registry.ValidateCurrent(current.TargetId, 12, new string('0', 64))
        );
    }

    [Fact]
    public void RegistryIsBoundedAndSaveLoadClearDropsSessionSnapshots()
    {
        var registry = new MachineSnapshotRegistry();
        for (var index = 0; index < MachineSnapshotRegistry.MaximumTargets; index++)
        {
            Assert.Equal(
                MachineSnapshotRegistryUpdateStatus.Accepted,
                registry.Upsert(EligibleSnapshot(1, targetId: $"machine-{index}")).Status
            );
        }
        Assert.Equal(
            MachineInteractionReasonIds.RegistryFull,
            registry.Upsert(EligibleSnapshot(1, targetId: "overflow")).Reason
        );

        registry.Clear();

        Assert.Equal(0, registry.Count);
        Assert.Equal(
            MachineInteractionReasonIds.RegistryTargetMissing,
            registry.ValidateCurrent("machine-0", 1, new string('0', 64))
        );
    }

    [Fact]
    public void SmapiAdapterSourceIsReadOnlyPublicSurfaceWithoutReflectionOrHooks()
    {
        var source = File.ReadAllText(AdapterSourcePath);

        Assert.Contains("GetMachineData()", source, StringComparison.Ordinal);
        Assert.Contains("machine.heldObject.Value", source, StringComparison.Ordinal);
        Assert.Contains("machine.lastInputItem.Value", source, StringComparison.Ordinal);
        Assert.Contains("machine.lastOutputRuleId.Value", source, StringComparison.Ordinal);
        Assert.Contains("machine.MinutesUntilReady", source, StringComparison.Ordinal);
        Assert.Contains("machine.readyForHarvest.Value", source, StringComparison.Ordinal);
        Assert.Contains("machine.showNextIndex.Value", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MinutesUntilReady =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("heldObject.Value =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("readyForHarvest.Value =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("showNextIndex.Value =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Harmony", source, StringComparison.Ordinal);
        Assert.DoesNotContain("location.Objects", source, StringComparison.Ordinal);
    }

    private static MachineTargetCatalog LoadShipped()
    {
        var result = MachineTargetCatalog.Load(File.ReadAllText(ShippedCatalogPath));
        Assert.True(result.IsAvailable, result.Reason);
        return result.Catalog;
    }

    private static MachineInteractionSnapshot CaptureShipped(MachineReadObservation observation) =>
        MachineInteractionClassifier.Capture(
            observation,
            LoadShipped(),
            MachineRuntimeEvidence.Current
        );

    private static MachineTargetCatalog EnabledCatalog(
        string qualifiedItemId,
        MachineInteractionCategory category,
        bool allowDelay = false,
        bool allowEject = false
    )
    {
        var json = JsonSerializer.Serialize(
            new
            {
                SchemaVersion = MachineTargetCatalog.CurrentSchemaVersion,
                ContractId = MachineTargetCatalog.ContractId,
                Targets = new[]
                {
                    new
                    {
                        Id = "fixture.target",
                        QualifiedItemId = qualifiedItemId,
                        ExpectedCategory = category.ToString(),
                        Enabled = true,
                        Excluded = false,
                        AllowDelay = allowDelay,
                        AllowEject = allowEject,
                        AllowThief = false,
                        LocationAllowlist = new[] { "Farm" },
                        Evidence = new[] { "fixture-only" },
                        Reason = "fixture-only",
                    },
                },
            }
        );
        var result = MachineTargetCatalog.Load(json);
        Assert.True(result.IsAvailable, result.Reason);
        return result.Catalog;
    }

    private static MachineReadObservation Observation(
        string qualifiedItemId = "(BC)17",
        string targetId = "machine-1",
        long authorityRevision = 1,
        string lastRuleId = "Default",
        int minutesUntilReady = 120,
        bool ready = false,
        bool showNextIndex = false,
        MachineItemFacts? heldOutput = null,
        MachineItemFacts? lastInput = null,
        MachineDataFacts? data = null
    )
    {
        return new MachineReadObservation(
            targetId,
            "Farm",
            qualifiedItemId,
            authorityRevision,
            lastRuleId,
            minutesUntilReady,
            ready,
            showNextIndex,
            heldOutput ?? new MachineItemFacts("(O)428", 1, 0, false),
            lastInput ?? new MachineItemFacts("(O)440", 1, 0, false),
            data ?? Data()
        );
    }

    private static MachineDataFacts Data(
        bool incubator = false,
        bool overnight = false,
        bool hasAdditional = false,
        bool customInteract = false,
        bool clearCondition = false,
        bool includeRule = true,
        MachineRuleFacts? rule = null
    ) =>
        new(
            incubator,
            overnight,
            hasAdditional,
            customInteract,
            clearCondition,
            includeRule ? rule ?? Rule() : null
        );

    private static MachineRuleFacts Rule(
        MachineTriggerKinds triggers = MachineTriggerKinds.ItemPlacedInMachine,
        int requiredCount = 1,
        int minutes = 240,
        int days = -1,
        bool recalculate = false,
        bool customOutput = false
    ) =>
        new("Default", triggers, requiredCount, minutes, days, recalculate, customOutput);

    private static MachineInteractionSnapshot EligibleSnapshot(
        long revision,
        int minutesUntilReady = 120,
        string targetId = "machine-1"
    ) =>
        MachineInteractionClassifier.Capture(
            Observation(
                targetId: targetId,
                authorityRevision: revision,
                minutesUntilReady: minutesUntilReady
            ),
            EnabledCatalog(
                "(BC)17",
                MachineInteractionCategory.OrdinarySingleInputFinite,
                allowDelay: true
            ),
            new MachineRuntimeEvidence(true, true)
        );

    private static void AssertExpected(
        MachineTargetCatalog catalog,
        string qualifiedItemId,
        MachineInteractionCategory expected
    )
    {
        var target = catalog.Find(qualifiedItemId);
        Assert.NotNull(target);
        Assert.Equal(expected, target!.ExpectedCategory);
    }

    private static void AssertLoadFailure(string? json, string expectedReason)
    {
        var result = MachineTargetCatalog.Load(json);
        Assert.False(result.IsAvailable);
        Assert.Equal(expectedReason, result.Reason);
        Assert.False(result.Catalog.IsAvailable);
    }

    private static string ReplaceFirst(string value, string oldValue, string newValue)
    {
        var index = value.IndexOf(oldValue, StringComparison.Ordinal);
        Assert.True(index >= 0);
        return string.Concat(
            value.AsSpan(0, index),
            newValue,
            value.AsSpan(index + oldValue.Length)
        );
    }
}
