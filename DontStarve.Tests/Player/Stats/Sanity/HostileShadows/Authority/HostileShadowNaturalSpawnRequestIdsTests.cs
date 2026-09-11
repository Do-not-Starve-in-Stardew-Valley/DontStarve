using DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity.HostileShadows.Authority;

public sealed class HostileShadowNaturalSpawnRequestIdsTests
{
    private const string Session = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string NextSession = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string OwnerA = "123456789";

    [Fact]
    public void Natural_refresh_ids_stay_unique_when_the_game_clock_is_rewound()
    {
        var ids = new HostileShadowNaturalSpawnRequestIds();

        Assert.True(ids.TryNext(Session, OwnerA, out var beforeRewind));
        // The source deliberately has no game-minute input, so a later call after CJB rewinds
        // the clock cannot recreate the earlier successful request ID.
        Assert.True(ids.TryNext(Session, OwnerA, out var afterRewind));

        Assert.NotEqual(beforeRewind, afterRewind);
        Assert.EndsWith(".nonce.1", beforeRewind, StringComparison.Ordinal);
        Assert.EndsWith(".nonce.2", afterRewind, StringComparison.Ordinal);
    }

    [Fact]
    public void Reset_starts_a_new_counter_for_the_next_authority_session()
    {
        var ids = new HostileShadowNaturalSpawnRequestIds();

        Assert.True(ids.TryNext(Session, OwnerA, out var first));
        ids.Reset();
        Assert.True(ids.TryNext(NextSession, OwnerA, out var afterReset));

        Assert.NotEqual(first, afterReset);
        Assert.EndsWith(".nonce.1", afterReset, StringComparison.Ordinal);
    }
}
