using DontStarve.Player.Stats.Sanity;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class TrashCanSanityPolicyTests
{
    [Fact]
    public void Ordinary_search_applies_base_penalty_and_resident_penalty()
    {
        var plan = TrashCanSanityPolicy.Evaluate(
            Facts(residentSaw: true, hasItem: false)
        );

        Assert.Equal(-20, plan.Penalty);
        Assert.Equal(0, plan.Recovery);
    }

    [Fact]
    public void Garbage_hat_waives_both_penalties_but_does_not_change_item_rewards()
    {
        var plan = TrashCanSanityPolicy.Evaluate(
            Facts(
                hasGarbageHat: true,
                residentSaw: true,
                hasItem: true,
                itemBasePrice: 100
            )
        );

        Assert.Equal(0, plan.Penalty);
        Assert.Equal(10, plan.Recovery);
    }

    [Theory]
    [InlineData(250, 15)]
    [InlineData(249, 10)]
    [InlineData(100, 10)]
    [InlineData(99, 5)]
    [InlineData(30, 5)]
    [InlineData(29, 3)]
    [InlineData(0, 3)]
    public void Price_tiers_are_inclusive_at_the_lower_boundary(
        int price,
        int expectedRecovery
    )
    {
        var plan = TrashCanSanityPolicy.Evaluate(
            Facts(hasItem: true, itemBasePrice: price)
        );

        Assert.Equal(expectedRecovery, plan.Recovery);
    }

    [Fact]
    public void Trash_book_adds_five_only_for_an_edible_item()
    {
        var edible = TrashCanSanityPolicy.Evaluate(
            Facts(hasItem: true, itemBasePrice: 0, hasTrashBook: true, itemEdibility: 1)
        );
        var inedible = TrashCanSanityPolicy.Evaluate(
            Facts(
                hasItem: true,
                itemBasePrice: 0,
                hasTrashBook: true,
                itemEdibility: -300
            )
        );

        Assert.Equal(8, edible.Recovery);
        Assert.Equal(3, inedible.Recovery);
    }

    [Theory]
    [InlineData(TrashCanSanityPolicy.GarbageHatQualifiedItemId)]
    [InlineData(TrashCanSanityPolicy.TrashCatalogueQualifiedItemId)]
    public void Garbage_hat_and_catalogue_are_always_explosion_rewards(
        string qualifiedItemId
    )
    {
        var plan = TrashCanSanityPolicy.Evaluate(
            Facts(
                hasItem: true,
                itemQualifiedId: qualifiedItemId,
                itemBasePrice: 9999,
                itemEdibility: 1,
                hasTrashBook: true
            )
        );

        Assert.Equal(-10, plan.Penalty);
        Assert.Equal(20, plan.Recovery);
    }

    [Fact]
    public void Explicit_double_mega_success_uses_twenty_and_skips_price_bonus()
    {
        var plan = TrashCanSanityPolicy.Evaluate(
            Facts(hasItem: true, itemBasePrice: 999, isDoubleMegaSuccess: true)
        );

        Assert.Equal(20, plan.Recovery);
    }

    [Fact]
    public void Ordered_host_application_clamps_before_recovery()
    {
        var service = BeginHost(current: 5d);
        var result = service.ChangeGarbage(
            "1",
            Facts(hasItem: true, itemBasePrice: 0)
        );

        Assert.Equal(SanityChangeStatus.Applied, result.Status);
        Assert.Equal(3d, service.GetCurrent("1"));
    }

    [Fact]
    public void Explosion_at_zero_recovers_twenty_after_the_penalty_floor()
    {
        var service = BeginHost(current: 0d);
        var result = service.ChangeGarbage(
            "1",
            Facts(hasItem: true, isDoubleMegaSuccess: true)
        );

        Assert.Equal(SanityChangeStatus.Applied, result.Status);
        Assert.Equal(20d, service.GetCurrent("1"));
    }

    private static TrashCanSanityFacts Facts(
        bool residentSaw = false,
        bool hasGarbageHat = false,
        bool hasTrashBook = false,
        bool hasItem = false,
        string itemQualifiedId = "(O)390",
        int itemBasePrice = 0,
        int itemEdibility = -300,
        bool isDoubleMegaSuccess = false
    )
    {
        return new TrashCanSanityFacts
        {
            GarbageCanId = "Mayor",
            ReactNpcs = true,
            ResidentSaw = residentSaw,
            HasGarbageHat = hasGarbageHat,
            HasTrashBook = hasTrashBook,
            HasItem = hasItem,
            ItemQualifiedId = hasItem ? itemQualifiedId : string.Empty,
            ItemBasePrice = itemBasePrice,
            ItemEdibility = itemEdibility,
            IsDoubleMegaSuccess = isDoubleMegaSuccess,
            SelectedEntryId = hasItem ? "test-entry" : string.Empty,
        };
    }

    private static SanityChangeService BeginHost(double current)
    {
        var persistence = new SanityPersistenceResult(
            SanityPersistenceStatus.LoadedV2,
            SanityPersistenceCapability.ReadWrite,
            "test",
            current,
            SanitySaveDataCodec.NewData("1", current, 200d)
        );
        var service = new SanityChangeService(new DefaultSanityMaximumProvider());
        Assert.True(service.BeginHostSession("0123456789abcdef0123456789abcdef", persistence, "1", out var reason), reason);
        return service;
    }
}
