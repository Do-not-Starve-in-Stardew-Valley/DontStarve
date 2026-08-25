using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class DarknessAttackLocationAuthorizationPolicyTests
{
    [Fact]
    public void DisabledJunimoBlessingAuthorizesMatchedAndUnmatchedLocations()
    {
        var policy = Policy(isAvailable: true, isEnabled: false);

        Assert.True(policy.Allows(Rule(EnvironmentLightLocationRuleStatus.Matched, true)));
        Assert.True(policy.Allows(Rule(EnvironmentLightLocationRuleStatus.Matched, false)));
        Assert.True(policy.Allows(Rule(EnvironmentLightLocationRuleStatus.Unmatched, false)));
    }

    [Fact]
    public void EnabledJunimoBlessingDeniesOnlyEligibleLocations()
    {
        var policy = Policy(isAvailable: true, isEnabled: true);

        Assert.False(policy.Allows(Rule(EnvironmentLightLocationRuleStatus.Matched, true)));
        Assert.True(policy.Allows(Rule(EnvironmentLightLocationRuleStatus.Matched, false)));
        Assert.True(policy.Allows(Rule(EnvironmentLightLocationRuleStatus.Unmatched, false)));
    }

    [Fact]
    public void NaturalDarknessCanReuseTheExactEligibleLocationMeaning()
    {
        var enabled = Policy(isAvailable: true, isEnabled: true);
        var disabled = Policy(isAvailable: true, isEnabled: false);

        Assert.True(
            enabled.IsJunimoBlessingProtecting(
                Rule(EnvironmentLightLocationRuleStatus.Matched, true)
            )
        );
        Assert.False(
            enabled.IsJunimoBlessingProtecting(
                Rule(EnvironmentLightLocationRuleStatus.Matched, false)
            )
        );
        Assert.False(
            enabled.IsJunimoBlessingProtecting(
                Rule(EnvironmentLightLocationRuleStatus.Unmatched, true)
            )
        );
        Assert.False(
            disabled.IsJunimoBlessingProtecting(
                Rule(EnvironmentLightLocationRuleStatus.Matched, true)
            )
        );
    }

    [Fact]
    public void UnavailableBlessingConfigurationFailsClosed()
    {
        var policy = Policy(isAvailable: false, isEnabled: false);

        Assert.False(policy.Allows(Rule(EnvironmentLightLocationRuleStatus.Matched, false)));
        Assert.False(policy.Allows(Rule(EnvironmentLightLocationRuleStatus.Unmatched, false)));
    }

    [Theory]
    [InlineData((int)EnvironmentLightLocationRuleStatus.Unavailable)]
    [InlineData((int)EnvironmentLightLocationRuleStatus.Ambiguous)]
    public void UnusableLocationRulesFailClosed(int rawStatus)
    {
        var status = (EnvironmentLightLocationRuleStatus)rawStatus;
        Assert.False(Policy(isAvailable: true, isEnabled: false).Allows(Rule(status, false)));
    }

    private static DarknessAttackLocationAuthorizationPolicy Policy(
        bool isAvailable,
        bool isEnabled
    )
    {
        return new DarknessAttackLocationAuthorizationPolicy(
            () => new EnvironmentLightJunimoBlessingState(isAvailable, isEnabled)
        );
    }

    private static EnvironmentLightLocationRuleResolution Rule(
        EnvironmentLightLocationRuleStatus status,
        bool junimoBlessingEligible
    )
    {
        var ruleId = status switch
        {
            EnvironmentLightLocationRuleStatus.Unmatched => EnvironmentLightLocationRuleIds.Unmatched,
            EnvironmentLightLocationRuleStatus.Unavailable => EnvironmentLightLocationRuleIds.Unavailable,
            EnvironmentLightLocationRuleStatus.Ambiguous => EnvironmentLightLocationRuleIds.Ambiguous,
            _ => "test.location",
        };
        var reason = status switch
        {
            EnvironmentLightLocationRuleStatus.Unmatched => EnvironmentLightReasonIds.LocationRuleUnmatched,
            EnvironmentLightLocationRuleStatus.Ambiguous => EnvironmentLightReasonIds.LocationRuleAmbiguous,
            EnvironmentLightLocationRuleStatus.Unavailable => EnvironmentLightReasonIds.LocationRuleUnavailable,
            _ => EnvironmentLightReasonIds.LocationRuleMatched,
        };
        return new EnvironmentLightLocationRuleResolution(
            status,
            status == EnvironmentLightLocationRuleStatus.Unavailable ? 0 : 3,
            ruleId,
            EnvironmentLightLocationLightProfile.FallbackOnly,
            TwoAmSpecialDeathSafe: false,
            HostileShadowSafe: false,
            JunimoBlessingEligible: junimoBlessingEligible,
            reason
        );
    }
}
