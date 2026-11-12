using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.Darkness;
using DontStarve.Player.Stats.Sanity.PassOut;
using Xunit;

namespace DontStarve.Tests.Sanity.PassOut;

public sealed class SpecialPassOutRoutePolicyTests
{
    [Theory]
    [InlineData((int)PassOutReason.TimeLimitPassOut, false, 2600, (int)DarknessDamageMode.Default, true)]
    [InlineData((int)PassOutReason.ExhaustionPassOut, false, 2200, (int)DarknessDamageMode.Default, true)]
    [InlineData((int)PassOutReason.TimeLimitPassOut, true, 2600, (int)DarknessDamageMode.Default, true)]
    [InlineData((int)PassOutReason.ExhaustionPassOut, true, 2200, (int)DarknessDamageMode.Default, false)]
    [InlineData((int)PassOutReason.ExhaustionPassOut, true, 2600, (int)DarknessDamageMode.Default, true)]
    public void SpecialFlowRequiresASettlementEligibleForcedPassOut(
        int reason,
        bool multiplayer,
        int timeOfDay,
        int mode,
        bool expectedSpecial
    )
    {
        var result = SpecialPassOutRoutePolicy.Resolve(
            (PassOutReason)reason,
            new SpecialPassOutRouteContext(
                HasDarknessDamageMode: true,
                DarknessDamageMode: (DarknessDamageMode)mode,
                HasJunimoBlessing: true,
                JunimoBlessingEnabled: false,
                LocationSafe: false,
                JunimoBlessingEligible: false,
                NightOwlAllowsConflictingSpecialDeathChain: true,
                NightOwlCompatibilityReason: "not-installed",
                IsMultiplayer: multiplayer,
                TimeOfDay: timeOfDay
            )
        );

        Assert.Equal(
            expectedSpecial
                ? PassOutPolicyAction.DelegateToTwoAmSpecialDeath
                : PassOutPolicyAction.ApplyDelta,
            result.Action
        );
        Assert.Equal(expectedSpecial ? 0d : -20d, result.Delta);
    }

    [Theory]
    [InlineData(true, false, false, true, false)]
    [InlineData(false, true, true, true, false)]
    [InlineData(false, false, true, true, true)]
    [InlineData(false, false, false, false, false)]
    public void SafetyAndCompatibilityFactsKeepVanillaPassOut(
        bool locationSafe,
        bool junimoEnabled,
        bool junimoEligible,
        bool nightOwlAllows,
        bool expectedSpecial
    )
    {
        var result = SpecialPassOutRoutePolicy.Resolve(
            PassOutReason.TimeLimitPassOut,
            new SpecialPassOutRouteContext(
                HasDarknessDamageMode: true,
                DarknessDamageMode: DarknessDamageMode.Default,
                HasJunimoBlessing: true,
                JunimoBlessingEnabled: junimoEnabled,
                LocationSafe: locationSafe,
                JunimoBlessingEligible: junimoEligible,
                NightOwlAllowsConflictingSpecialDeathChain: nightOwlAllows,
                NightOwlCompatibilityReason: "compatibility",
                IsMultiplayer: false,
                TimeOfDay: 2800
            )
        );

        Assert.Equal(
            expectedSpecial
                ? PassOutPolicyAction.DelegateToTwoAmSpecialDeath
                : PassOutPolicyAction.ApplyDelta,
            result.Action
        );
    }

    [Fact]
    public void Observed_external_late_night_owner_suppresses_special_chain_without_identity()
    {
        var result = SpecialPassOutRoutePolicy.Resolve(
            PassOutReason.TimeLimitPassOut,
            new SpecialPassOutRouteContext(
                HasDarknessDamageMode: true,
                DarknessDamageMode: DarknessDamageMode.Default,
                HasJunimoBlessing: true,
                JunimoBlessingEnabled: false,
                LocationSafe: false,
                JunimoBlessingEligible: false,
                NightOwlAllowsConflictingSpecialDeathChain: true,
                NightOwlCompatibilityReason: "passout.compatibility.night-owl-plus.not-installed",
                IsMultiplayer: false,
                TimeOfDay: 2800,
                ExternalLateNightOwnerDetected: true
            )
        );

        Assert.Equal(PassOutPolicyAction.ApplyDelta, result.Action);
        Assert.Equal(SanityChangeSource.TimeLimitPassOut, result.Source);
        Assert.Equal(-20d, result.Delta);
        Assert.Equal(
            "passout.compatibility.late-night.behavior-clock-rollback",
            result.StableReason
        );
    }
}
