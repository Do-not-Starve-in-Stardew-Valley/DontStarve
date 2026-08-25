using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.Darkness;
using DontStarve.Player.Stats.Sanity.PassOut;
using Xunit;

namespace DontStarve.Tests.Sanity.PassOut;

public sealed class PassOutReasonPolicyTests
{
    private const string SessionA = "11111111111141118111111111111111";

    [Theory]
    [InlineData(2590, -14f, (int)PassOutReasonClassificationStatus.Unavailable, (int)PassOutReason.Unknown)]
    [InlineData(2600, -14f, (int)PassOutReasonClassificationStatus.Available, (int)PassOutReason.TimeLimitPassOut)]
    [InlineData(2590, -15f, (int)PassOutReasonClassificationStatus.Available, (int)PassOutReason.ExhaustionPassOut)]
    [InlineData(2600, -15f, (int)PassOutReasonClassificationStatus.Ambiguous, (int)PassOutReason.Unknown)]
    public void SharedStartToPassOutBranchRequiresOneExclusivePredicate(
        int timeOfDay,
        float stamina,
        int expectedStatus,
        int expectedReason
    )
    {
        var result = PassOutReasonPolicy.ClassifyStartToPassOut(timeOfDay, stamina);

        Assert.Equal((PassOutReasonClassificationStatus)expectedStatus, result.Status);
        Assert.Equal((PassOutReason)expectedReason, result.Reason);
    }

    [Theory]
    [InlineData(600, 360d)]
    [InlineData(2200, 72d)]
    [InlineData(2550, 3d)]
    public void CommittedVoluntarySleepPreservesThreePerTenMinuteRecovery(
        int timeOfDay,
        double expectedDelta
    )
    {
        var classification = PassOutReasonPolicy.ClassifyVoluntarySleep(timeOfDay);
        var decision = PassOutReasonPolicy.ResolveDayEnding(
            Evidence(PassOutReason.VoluntarySleep, timeOfDay)
        );

        Assert.True(classification.IsAvailable);
        Assert.Equal(PassOutPolicyAction.ApplyDelta, decision.Action);
        Assert.Equal(SanityChangeSource.VoluntarySleep, decision.Source);
        Assert.Equal(expectedDelta, decision.Delta);
    }

    [Fact]
    public void InvalidCommittedSleepAndUnknownEvidenceFailClosed()
    {
        var invalid = PassOutReasonPolicy.ClassifyVoluntarySleep(2600);
        var unknown = PassOutReasonPolicy.ResolveDayEnding(
            Evidence(PassOutReason.Unknown, 2600)
        );

        Assert.False(invalid.IsAvailable);
        Assert.Equal(PassOutPolicyAction.None, unknown.Action);
        Assert.Equal(0d, unknown.Delta);
    }

    [Theory]
    [InlineData((int)PassOutReason.TimeLimitPassOut, (int)SanityChangeSource.TimeLimitPassOut)]
    [InlineData((int)PassOutReason.ExhaustionPassOut, (int)SanityChangeSource.ExhaustionPassOut)]
    public void OrdinaryForcedPassOutReasonsLoseTwentyOnce(
        int reason,
        int source
    )
    {
        var decision = PassOutReasonPolicy.ResolveDayEnding(Evidence((PassOutReason)reason, 2600));

        Assert.Equal(PassOutPolicyAction.ApplyDelta, decision.Action);
        Assert.Equal((SanityChangeSource)source, decision.Source);
        Assert.Equal(-20d, decision.Delta);
    }

    [Fact]
    public void SpecialDeathOnlyDelegatesAndHealthDeathUsesCurrentMaximumFraction()
    {
        var special = PassOutReasonPolicy.ResolveDayEnding(
            Evidence(PassOutReason.SanityDarknessSpecialDeath, 2600)
        );
        var health = PassOutReasonPolicy.ResolveHealthDeathRecovery(
            true,
            false,
            10,
            100
        );

        Assert.Equal(PassOutPolicyAction.DelegateToTwoAmSpecialDeath, special.Action);
        Assert.Equal(0d, special.Delta);
        Assert.Equal(PassOutPolicyAction.SetToMaximumFraction, health.Action);
        Assert.Equal(SanityChangeSource.HealthDeath, health.Source);
        Assert.Equal(0.5d, health.TargetMaximumFraction);
        Assert.Equal(137.5d, 275d * health.TargetMaximumFraction);
    }

    [Fact]
    public void RepeatedPostReviveCallbackCannotApplyHealthDeathAgain()
    {
        var first = PassOutReasonPolicy.ResolveHealthDeathRecovery(
            true,
            false,
            10,
            100
        );
        var repeated = PassOutReasonPolicy.ResolveHealthDeathRecovery(
            false,
            false,
            10,
            100
        );

        Assert.Equal(PassOutPolicyAction.SetToMaximumFraction, first.Action);
        Assert.Equal(PassOutPolicyAction.None, repeated.Action);
    }

    [Theory]
    [InlineData(false, false, 50, 100)] // Phoenix revives before a death screen exists.
    [InlineData(false, true, 0, 100)]
    [InlineData(true, true, 0, 100)]
    [InlineData(true, false, 0, 100)]
    [InlineData(true, false, 101, 100)]
    public void PhoenixAndIncompleteDeathTransitionsNeverResetSanity(
        bool wasKillScreen,
        bool isKillScreen,
        int health,
        int maximumHealth
    )
    {
        var result = PassOutReasonPolicy.ResolveHealthDeathRecovery(
            wasKillScreen,
            isKillScreen,
            health,
            maximumHealth
        );

        Assert.Equal(PassOutPolicyAction.None, result.Action);
    }

    [Fact]
    public void TimeLimitMatrixKeepsSafeOffAndJunimoOrdinaryWithoutModeCrossTalk()
    {
        foreach (var mode in Enum.GetValues<DarknessDamageMode>())
            foreach (var safe in new[] { false, true })
                foreach (var junimoEnabled in new[] { false, true })
                    foreach (var junimoEligible in new[] { false, true })
                    {
                        var result = PassOutReasonPolicy.ResolveTimeLimitRoute(
                            new PassOutTimeLimitPolicyContext(
                                true,
                                mode,
                                true,
                                junimoEnabled,
                                safe,
                                junimoEligible,
                                "location.test",
                                true,
                                "passout.compatibility.night-owl-plus.not-installed"
                            )
                        );
                        var ordinary = safe
                            || mode == DarknessDamageMode.Off
                            || (junimoEnabled && junimoEligible);

                        Assert.Equal(
                            ordinary
                                ? PassOutPolicyAction.ApplyDelta
                                : PassOutPolicyAction.DelegateToTwoAmSpecialDeath,
                            result.Action
                        );
                        Assert.Equal(ordinary ? -20d : 0d, result.Delta);
                    }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void UnavailableTypedInputsKeepVanillaTimePassOutOrdinary(
        bool hasMode,
        bool hasJunimo
    )
    {
        var result = PassOutReasonPolicy.ResolveTimeLimitRoute(
            new PassOutTimeLimitPolicyContext(
                hasMode,
                DarknessDamageMode.Default,
                hasJunimo,
                true,
                false,
                true,
                "location.test",
                true,
                "passout.compatibility.night-owl-plus.not-installed"
            )
        );

        Assert.Equal(PassOutPolicyAction.ApplyDelta, result.Action);
        Assert.Equal(SanityChangeSource.TimeLimitPassOut, result.Source);
        Assert.Equal(-20d, result.Delta);
    }

    [Fact]
    public void EvidenceLedgerIsBoundedIdempotentConflictAwareAndConsumeOnce()
    {
        var ledger = new PassOutReasonLedger();
        var original = Evidence(PassOutReason.VoluntarySleep, 2200, "1", "a");
        var duplicate = Evidence(PassOutReason.VoluntarySleep, 2200, "1", "b");
        var conflict = Evidence(PassOutReason.ExhaustionPassOut, 2200, "1", "c");

        Assert.Equal(PassOutReasonCaptureStatus.Captured, ledger.Capture(original).Status);
        Assert.Equal(PassOutReasonCaptureStatus.Duplicate, ledger.Capture(duplicate).Status);
        Assert.Equal(PassOutReasonCaptureStatus.Conflict, ledger.Capture(conflict).Status);
        Assert.True(ledger.TryConsumeDayEnding(SessionA, "1", 42, out var consumed, out _));
        Assert.Equal(PassOutReason.Unknown, consumed.Reason);
        Assert.False(ledger.TryConsumeDayEnding(SessionA, "1", 42, out _, out _));

        ledger.Clear();
        for (var index = 1; index <= PassOutReasonLedger.MaximumEntries; index++)
        {
            Assert.Equal(
                PassOutReasonCaptureStatus.Captured,
                ledger.Capture(Evidence(PassOutReason.TimeLimitPassOut, 2600, index.ToString(), index.ToString())).Status
            );
        }
        Assert.Equal(
            PassOutReasonCaptureStatus.Rejected,
            ledger.Capture(Evidence(PassOutReason.TimeLimitPassOut, 2600, "99", "overflow")).Status
        );
    }

    [Fact]
    public void EvidenceLedgerRejectsWrongSessionAndPrunesOldOrReturnedTitleState()
    {
        var ledger = new PassOutReasonLedger();
        Assert.Equal(
            PassOutReasonCaptureStatus.Captured,
            ledger.Capture(Evidence(PassOutReason.TimeLimitPassOut, 2600)).Status
        );

        Assert.False(
            ledger.TryConsumeDayEnding(
                "22222222222242228222222222222222",
                "1",
                42,
                out _,
                out _
            )
        );
        ledger.PruneBeforeDay(SessionA, 43);
        Assert.Equal(0, ledger.Count);

        Assert.Equal(
            PassOutReasonCaptureStatus.Captured,
            ledger.Capture(Evidence(PassOutReason.ExhaustionPassOut, 2200)).Status
        );
        ledger.ForgetPlayer("1");
        Assert.Equal(0, ledger.Count);
        Assert.Equal(
            PassOutReasonCaptureStatus.Captured,
            ledger.Capture(Evidence(PassOutReason.ExhaustionPassOut, 2200)).Status
        );
        ledger.Clear();
        Assert.Equal(0, ledger.Count);
    }

    [Fact]
    public void NewPassOutSourcesAreAppendOnlyStableIds()
    {
        Assert.Equal(14, (int)SanityChangeSource.VoluntarySleep);
        Assert.Equal(15, (int)SanityChangeSource.TimeLimitPassOut);
        Assert.Equal(16, (int)SanityChangeSource.ExhaustionPassOut);
        Assert.Equal(17, (int)SanityChangeSource.HealthDeath);
        Assert.Equal(18, (int)SanityChangeSource.SanityDarknessSpecialDeath);
    }

    [Fact]
    public void RuntimeContractsObserveCommittedSleepAndFinalDeathWithoutBypassingPhoenix()
    {
        var adapter = Contract("SmapiPassOutReasonService.cs");
        var sleep = Contract("Sleep.cs");
        var special = Contract("SmapiSanityTwoAmSpecialDeathService.cs");

        Assert.Contains("typeof(GameLocation),\r\n            \"doSleep\"", Normalize(adapter), StringComparison.Ordinal);
        Assert.Contains(
            "typeof(GameLocation),\r\n            nameof(GameLocation.checkForEvents)",
            Normalize(adapter),
            StringComparison.Ordinal
        );
        Assert.Contains("CheckForEventsPrefix", adapter, StringComparison.Ordinal);
        Assert.Contains("CheckForEventsPostfix", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("nameof(Game1.updatePause)", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdatePausePrefix", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdatePausePostfix", adapter, StringComparison.Ordinal);
        Assert.Contains("wasKillScreen,\r\n            Game1.killScreen", Normalize(adapter), StringComparison.Ordinal);
        Assert.Contains("farmer.GetMaxSanity() * decision.TargetMaximumFraction", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("takeDamage", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("hasUsedDailyRevive", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("Phoenix", adapter, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SleepSanityClock", sleep, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeChanged", sleep, StringComparison.Ordinal);
        Assert.Contains("TryConsumeDayEnding", sleep, StringComparison.Ordinal);
        Assert.Contains("ledger.ForgetPlayer(playerKey)", sleep, StringComparison.Ordinal);
        Assert.Contains("ReturnedToTitle", sleep, StringComparison.Ordinal);
        Assert.Contains("ledger.Clear()", sleep, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.killScreen", special, StringComparison.Ordinal);
        Assert.DoesNotContain("takeDamage", special, StringComparison.Ordinal);
    }

    private static PassOutReasonEvidence Evidence(
        PassOutReason reason,
        int timeOfDay,
        string playerKey = "1",
        string evidenceSeed = "evidence"
    )
    {
        return new PassOutReasonEvidence(
            SessionA,
            DeterministicGuid(evidenceSeed),
            playerKey,
            42,
            reason,
            timeOfDay,
            "passout.reason.test"
        );
    }

    private static string DeterministicGuid(string seed)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(seed)
        );
        return new Guid(bytes).ToString("N");
    }

    private static string Contract(string fileName)
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", "PassOut", fileName)
        );
    }

    private static string Normalize(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);
    }
}
