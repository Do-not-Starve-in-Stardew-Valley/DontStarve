using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.Darkness;
using DontStarve.Player.Stats.Sanity.PassOut;
using DontStarve.Player.Stats.Sanity.PassOut.Compatibility;
using Xunit;

namespace DontStarve.Tests.Sanity.PassOut;

public sealed class NightOwlPlusCompatibilityTests
{
    [Fact]
    public void MissingExactUidLeavesCurrentTwoAmBehaviorEnabled()
    {
        var result = Resolve(null, Array.Empty<NightOwlPlusManifestEvidence>());

        Assert.Equal(NightOwlPlusCompatibilityStatus.NotInstalled, result.Status);
        Assert.True(result.AllowsConflictingSpecialDeathChain);
        Assert.False(result.ExactUidDetected);
        Assert.Equal(
            "passout.compatibility.night-owl-plus.not-installed",
            result.StableReason
        );
    }

    [Fact]
    public void ExactUidAndReviewedVersionSuppressOnlyConflictingSpecialChain()
    {
        var result = Resolve(
            Manifest(
                NightOwlPlusCompatibility.ExactUniqueId,
                NightOwlPlusCompatibility.ExplicitlySupportedVersion,
                NightOwlPlusCompatibility.DiagnosticUpdateKey
            ),
            Array.Empty<NightOwlPlusManifestEvidence>()
        );

        Assert.Equal(
            NightOwlPlusCompatibilityStatus.InstalledSupported,
            result.Status
        );
        Assert.False(result.AllowsConflictingSpecialDeathChain);
        Assert.True(result.ExactUidDetected);
        Assert.True(result.DiagnosticUpdateKeyObserved);
        Assert.Equal("1.0.0", result.DetectedVersion);
    }

    [Theory]
    [InlineData("1.0.1")]
    [InlineData("2.0.0")]
    [InlineData("1.0.0-beta")]
    public void FutureOrPrereleaseVersionsFailClosedUntilReviewed(string version)
    {
        var result = Resolve(
            Manifest(NightOwlPlusCompatibility.ExactUniqueId, version),
            Array.Empty<NightOwlPlusManifestEvidence>()
        );

        Assert.Equal(
            NightOwlPlusCompatibilityStatus.InstalledUnsupported,
            result.Status
        );
        Assert.False(result.AllowsConflictingSpecialDeathChain);
        Assert.Equal(version, result.DetectedVersion);
    }

    [Fact]
    public void MissingInstalledVersionAndRegistryFailuresFailClosed()
    {
        var missingVersion = Resolve(
            Manifest(NightOwlPlusCompatibility.ExactUniqueId, string.Empty),
            Array.Empty<NightOwlPlusManifestEvidence>()
        );
        var exactLookupFailure = NightOwlPlusCompatibility.Resolve(
            _ => throw new InvalidOperationException("registry unavailable"),
            () => Array.Empty<NightOwlPlusManifestEvidence>()
        );
        var diagnosticFailure = NightOwlPlusCompatibility.Resolve(
            _ => null,
            () => throw new InvalidOperationException("manifest scan unavailable")
        );
        var boundedFailure = NightOwlPlusCompatibility.Resolve(
            _ => throw new InvalidOperationException(new string('x', 300) + "\r\nmore"),
            () => Array.Empty<NightOwlPlusManifestEvidence>()
        );

        Assert.Equal(
            NightOwlPlusCompatibilityStatus.FactsUnavailable,
            missingVersion.Status
        );
        Assert.Equal(
            NightOwlPlusCompatibilityStatus.FactsUnavailable,
            exactLookupFailure.Status
        );
        Assert.Equal(
            NightOwlPlusCompatibilityStatus.FactsUnavailable,
            diagnosticFailure.Status
        );
        Assert.False(missingVersion.AllowsConflictingSpecialDeathChain);
        Assert.False(exactLookupFailure.AllowsConflictingSpecialDeathChain);
        Assert.False(diagnosticFailure.AllowsConflictingSpecialDeathChain);
        Assert.Equal(
            NightOwlPlusCompatibility.MaximumDiagnosticDetailLength,
            boundedFailure.DiagnosticDetail.Length
        );
        Assert.DoesNotContain('\r', boundedFailure.DiagnosticDetail);
        Assert.DoesNotContain('\n', boundedFailure.DiagnosticDetail);
    }

    [Fact]
    public void SameDisplayNameWithWrongUidIsNotDetected()
    {
        var lookalike = new NightOwlPlusManifestEvidence(
            "Example.NotNightOwl",
            "Night Owl Plus",
            "1.0.0",
            Array.Empty<string>()
        );
        var result = Resolve(null, new[] { lookalike });

        Assert.Equal(NightOwlPlusCompatibilityStatus.NotInstalled, result.Status);
        Assert.True(result.AllowsConflictingSpecialDeathChain);
        Assert.False(result.ExactUidDetected);
    }

    [Fact]
    public void NexusKeyWithoutExactUidIsDiagnosticOnlyAndFailClosed()
    {
        var wrongUid = Manifest(
            "Example.WrongUid",
            "1.0.0",
            NightOwlPlusCompatibility.DiagnosticUpdateKey
        );
        var result = Resolve(null, new[] { wrongUid });

        Assert.Equal(
            NightOwlPlusCompatibilityStatus.FactsUnavailable,
            result.Status
        );
        Assert.False(result.ExactUidDetected);
        Assert.True(result.DiagnosticUpdateKeyObserved);
        Assert.False(result.AllowsConflictingSpecialDeathChain);
        Assert.Equal(
            "passout.compatibility.night-owl-plus.update-key-without-exact-uid",
            result.StableReason
        );
    }

    [Fact]
    public void RegistryInconsistencyAndUnboundedDiagnosticsFailClosed()
    {
        var exactOnlyInScan = Manifest(
            NightOwlPlusCompatibility.ExactUniqueId,
            "1.0.0"
        );
        var tooMany = Enumerable
            .Range(0, NightOwlPlusCompatibility.MaximumDiagnosticManifests + 1)
            .Select(index => Manifest($"Example.Mod{index}", "1.0.0"))
            .ToArray();

        var inconsistent = Resolve(null, new[] { exactOnlyInScan });
        var unbounded = Resolve(null, tooMany);

        Assert.Equal(
            NightOwlPlusCompatibilityStatus.FactsUnavailable,
            inconsistent.Status
        );
        Assert.Equal(
            NightOwlPlusCompatibilityStatus.FactsUnavailable,
            unbounded.Status
        );
        Assert.False(inconsistent.AllowsConflictingSpecialDeathChain);
        Assert.False(unbounded.AllowsConflictingSpecialDeathChain);
    }

    [Fact]
    public void CompatibilityGateKeepsSafeOffJunimoAndOrdinaryReasonsUntouched()
    {
        foreach (var mode in Enum.GetValues<DarknessDamageMode>())
            foreach (var safe in new[] { false, true })
                foreach (var junimoEnabled in new[] { false, true })
                    foreach (var junimoEligible in new[] { false, true })
                    {
                        var decision = PassOutReasonPolicy.ResolveTimeLimitRoute(
                            new PassOutTimeLimitPolicyContext(
                                true,
                                mode,
                                true,
                                junimoEnabled,
                                safe,
                                junimoEligible,
                                "location.test",
                                false,
                                "passout.compatibility.night-owl-plus.installed-supported-special-chain-suppressed"
                            )
                        );

                        Assert.Equal(PassOutPolicyAction.ApplyDelta, decision.Action);
                        Assert.Equal(SanityChangeSource.TimeLimitPassOut, decision.Source);
                        Assert.Equal(-20d, decision.Delta);
                    }

        var voluntarySleep = PassOutReasonPolicy.ResolveDayEnding(
            Evidence(PassOutReason.VoluntarySleep, 2200)
        );
        var exhaustion = PassOutReasonPolicy.ResolveDayEnding(
            Evidence(PassOutReason.ExhaustionPassOut, 1800)
        );
        var healthDeath = PassOutReasonPolicy.ResolveHealthDeathRecovery(
            true,
            false,
            10,
            100
        );
        Assert.Equal(PassOutPolicyAction.ApplyDelta, voluntarySleep.Action);
        Assert.Equal(PassOutPolicyAction.ApplyDelta, exhaustion.Action);
        Assert.Equal(SanityChangeSource.ExhaustionPassOut, exhaustion.Source);
        Assert.Equal(-20d, exhaustion.Delta);
        Assert.Equal(PassOutPolicyAction.SetToMaximumFraction, healthDeath.Action);
    }

    [Fact]
    public void RuntimeContractUsesGameLaunchedRegistryFactsWithoutPrivateApiOrManifestDependency()
    {
        var compatibility = Contract("NightOwlPlusCompatibility.cs");
        var runtime = Contract("SmapiSanityTwoAmSpecialDeathService.cs");
        var manifest = Contract("..", "MainManifest.json");

        Assert.Contains(
            "MaximillianRW.NightOwlPlus",
            compatibility,
            StringComparison.Ordinal
        );
        Assert.Contains("Nexus:29714", compatibility, StringComparison.Ordinal);
        Assert.DoesNotContain("Reflection", compatibility, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.", compatibility, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", compatibility, StringComparison.Ordinal);
        Assert.Contains("GameLoop.GameLaunched += OnGameLaunched", runtime, StringComparison.Ordinal);
        Assert.Contains("helper.ModRegistry.Get(uniqueId)", runtime, StringComparison.Ordinal);
        Assert.Contains("helper.ModRegistry.GetAll()", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("MaximillianRW.NightOwlPlus", manifest, StringComparison.Ordinal);
    }

    private static NightOwlPlusCompatibilityResult Resolve(
        NightOwlPlusManifestEvidence? exact,
        IReadOnlyList<NightOwlPlusManifestEvidence> loaded
    )
    {
        return NightOwlPlusCompatibility.Resolve(_ => exact, () => loaded);
    }

    private static NightOwlPlusManifestEvidence Manifest(
        string uniqueId,
        string version,
        params string[] updateKeys
    )
    {
        return new NightOwlPlusManifestEvidence(
            uniqueId,
            "arbitrary display name",
            version,
            updateKeys
        );
    }

    private static PassOutReasonEvidence Evidence(PassOutReason reason, int timeOfDay)
    {
        return new PassOutReasonEvidence(
            "11111111111141118111111111111111",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "1",
            42,
            reason,
            timeOfDay,
            "passout.reason.test"
        );
    }

    private static string Contract(params string[] path)
    {
        return File.ReadAllText(
            Path.Combine(
                new[] { AppContext.BaseDirectory, "Contracts", "PassOut" }
                    .Concat(path)
                    .ToArray()
            )
        );
    }
}
