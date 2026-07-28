using DontStarve.Player.Stats.Sanity;
using DontStarve.Player.Stats.Sanity.SanityBehaviors;
using DontStarve.Time;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityBehaviorRulesTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 0.5)]
    [InlineData(9.5, 0.05)]
    [InlineData(10, 0)]
    [InlineData(11, 0)]
    public void TenTileFalloffUsesTileDistance(double distance, double expected)
    {
        Assert.Equal(
            expected,
            SanityBehaviorRules.GetDistancePercentage(distance),
            10
        );
    }

    [Theory]
    [InlineData(0, 0.294)]
    [InlineData(5, 0.147)]
    [InlineData(10, 0)]
    public void JunimoRecoveryAppliesTheSameTenTilePercentage(
        double distance,
        double expected
    )
    {
        Assert.Equal(
            expected,
            SanityBehaviorRules.CalculateFriendlyNpcRecovery(
                FriendlyNpcSanityKind.Junimo,
                0,
                distance
            ),
            10
        );
    }

    [Theory]
    [InlineData(1, 0, 5, 0.588)]
    [InlineData(0, 8, 0, 0.588)]
    [InlineData(0, 5, 0, 0.294)]
    [InlineData(0, 4, 0, 0)]
    [InlineData(2, 5, 0, 0.588)]
    [InlineData(2, 3, 0, 0.294)]
    [InlineData(2, 0, 0, 0.147)]
    public void FriendlyNpcFormulasRemainFrozen(
        int kindId,
        int hearts,
        double distance,
        double expected
    )
    {
        Assert.Equal(
            expected,
            SanityBehaviorRules.CalculateFriendlyNpcRecovery(
                (FriendlyNpcSanityKind)kindId,
                hearts,
                distance
            ),
            10
        );
    }

    [Theory]
    [InlineData(0.588, 0, 0.588)]
    [InlineData(0.588, 5, 0.294)]
    [InlineData(0.588, 10, 0)]
    [InlineData(-1, 0, 0)]
    public void MonsterLossUsesConfiguredPositiveValueAndDistance(
        double configured,
        double distance,
        double expected
    )
    {
        Assert.Equal(
            expected,
            SanityBehaviorRules.CalculateMonsterLoss(configured, distance),
            10
        );
    }

    [Fact]
    public void EquipmentAddsEveryExistingSlotAndTheSingleCurrentTrinketSlot()
    {
        var hats = new Dictionary<string, double> { ["hat"] = 1 };
        var shirts = new Dictionary<string, double> { ["shirt"] = 2 };
        var pants = new Dictionary<string, double> { ["pants"] = 3 };
        var boots = new Dictionary<string, double> { ["boots"] = 4 };
        var rings = new Dictionary<string, double>
        {
            ["left"] = 5,
            ["right"] = -1,
        };
        var trinkets = new Dictionary<string, double> { ["trinket"] = 6 };

        var delta = SanityBehaviorRules.CalculateEquipmentDelta(
            new EquipmentSanityLoadout(
                "hat",
                "shirt",
                "pants",
                "boots",
                "left",
                "right",
                "trinket"
            ),
            hats,
            shirts,
            pants,
            boots,
            rings,
            trinkets
        );

        Assert.Equal(20, delta, 10);
    }

    [Fact]
    public void UnknownEquipmentIdsContributeZero()
    {
        var empty = new Dictionary<string, double>();
        var delta = SanityBehaviorRules.CalculateEquipmentDelta(
            new EquipmentSanityLoadout(
                "missing",
                null,
                null,
                null,
                null,
                null,
                null
            ),
            empty,
            empty,
            empty,
            empty,
            empty,
            empty
        );

        Assert.Equal(0, delta);
    }

    [Theory]
    [InlineData(1200, false, 0)]
    [InlineData(1201, false, 0.0588)]
    [InlineData(1439, false, 0.0588)]
    [InlineData(1440, false, 0.0588)]
    [InlineData(1441, false, 0.1176)]
    [InlineData(1441, true, 0.0588)]
    [InlineData(1800, false, 0.1176)]
    [InlineData(1800, true, 0.0588)]
    [InlineData(1801, false, 0)]
    public void NightLossFreezesSunsetMidnightAndSixAmBoundaries(
        long time,
        bool indoors,
        double expected
    )
    {
        Assert.Equal(
            expected,
            SanityBehaviorRules.CalculateNightLossForMinute(
                time,
                20 * 60,
                !indoors
            ),
            10
        );
    }

    [Theory]
    [MemberData(nameof(MineCases))]
    public void MineShaftOverrideAndAdditiveOrderRemainsFrozen(
        object context,
        double expected
    )
    {
        Assert.Equal(
            expected,
            SanityBehaviorRules.CalculateMineLoss((MineSanityContext)context),
            10
        );
    }

    public static IEnumerable<object[]> MineCases()
    {
        yield return new object[]
        {
            new MineSanityContext(MineSanityLocationKind.Other, 0, false, false, false, false, false, 0),
            0d,
        };
        yield return new object[]
        {
            new MineSanityContext(MineSanityLocationKind.VolcanoDungeon, 0, false, false, false, false, false, 0),
            0.1176,
        };
        yield return new object[]
        {
            new MineSanityContext(MineSanityLocationKind.MineShaft, 1, false, false, false, false, false, 0),
            0.0588,
        };
        yield return new object[]
        {
            new MineSanityContext(MineSanityLocationKind.MineShaft, 1, true, false, false, false, false, 0),
            0.588,
        };
        yield return new object[]
        {
            new MineSanityContext(MineSanityLocationKind.MineShaft, 121, true, false, false, false, false, 0),
            0.1176,
        };
        yield return new object[]
        {
            new MineSanityContext(MineSanityLocationKind.MineShaft, 1001, true, false, false, false, false, 0),
            0.2352,
        };
        yield return new object[]
        {
            new MineSanityContext(MineSanityLocationKind.MineShaft, 1001, true, true, false, false, false, 0),
            0.1764,
        };
        yield return new object[]
        {
            new MineSanityContext(MineSanityLocationKind.MineShaft, 120, false, false, true, true, true, 1),
            0.7644,
        };
        yield return new object[]
        {
            new MineSanityContext(MineSanityLocationKind.MineShaft, 121, false, true, false, false, false, 1),
            0.4116,
        };
    }

    [Fact]
    public void SleepClockFailsClosedBeforeSessionInitialization()
    {
        var clock = new SleepSanityClock();

        var result = clock.Resolve();

        Assert.False(result.Success);
        Assert.Equal(0, result.Delta);
        Assert.Equal("sleep-time-was-not-initialized", result.Reason);
    }

    [Theory]
    [InlineData(600, 360)]
    [InlineData(2200, 72)]
    [InlineData(2550, 3)]
    [InlineData(2600, -20)]
    public void OrdinarySleepUsesThreePerTenMinutesBeforeTwoAm(
        int timeOfDay,
        double expected
    )
    {
        var clock = new SleepSanityClock();
        Assert.True(clock.BeginSession(timeOfDay, out _));

        var result = clock.Resolve();

        Assert.True(result.Success);
        Assert.Equal(expected, result.Delta);
    }

    [Fact]
    public void InvalidObservedTimeDoesNotReplaceTheLastSafeTime()
    {
        var clock = new SleepSanityClock();
        Assert.True(clock.BeginSession(2200, out _));

        Assert.False(clock.Observe(2260, out var reason));
        var result = clock.Resolve();

        Assert.Equal("sleep-time-is-outside-the-ordinary-600-to-2600-range", reason);
        Assert.True(result.Success);
        Assert.Equal(72, result.Delta);
        Assert.Equal(2200, clock.LastTime);
    }

    [Fact]
    public void SleepClockClearPreventsCrossSaveSettlement()
    {
        var clock = new SleepSanityClock();
        Assert.True(clock.BeginSession(2300, out _));

        clock.Clear();

        Assert.False(clock.HasObservedTime);
        Assert.False(clock.Resolve().Success);
    }

    [Fact]
    public void NightRuleConsumesExactlyOnceMinutesWithoutConsumerCatchup()
    {
        var timeline = new MinuteTimeTimeline();
        var totalLoss = 0d;
        timeline.OnUpdate.Add(time =>
            totalLoss += SanityBehaviorRules.CalculateNightLossForMinute(
                time,
                20 * 60,
                false
            )
        );
        timeline.Load(1199);

        timeline.Synchronize(1202);
        timeline.Synchronize(1202);

        Assert.Equal(0.1176, totalLoss, 10);
    }

    [Fact]
    public void ExistingBehaviorSourcesRemainHostOnlyExceptFoodInteraction()
    {
        Assert.True(SanityRequestPolicy.For(SanityChangeSource.Food).ClientRequestAllowed);
        foreach (
            var source in new[]
            {
                SanityChangeSource.Equipment,
                SanityChangeSource.Npc,
                SanityChangeSource.Junimo,
                SanityChangeSource.Monster,
                SanityChangeSource.Night,
                SanityChangeSource.Mine,
                SanityChangeSource.Sleep,
                SanityChangeSource.Buff,
                SanityChangeSource.VoluntarySleep,
                SanityChangeSource.TimeLimitPassOut,
                SanityChangeSource.ExhaustionPassOut,
                SanityChangeSource.HealthDeath,
                SanityChangeSource.SanityDarknessSpecialDeath,
            }
        )
        {
            Assert.False(SanityRequestPolicy.For(source).ClientRequestAllowed);
        }
    }
}
