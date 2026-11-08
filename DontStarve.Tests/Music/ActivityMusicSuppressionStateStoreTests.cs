using System.Reflection;
using Xunit;

namespace DontStarve.Tests.Music;

public sealed class ActivityMusicSuppressionStateStoreTests
{
    [Fact]
    public void Dusk_and_insane_cooldowns_are_separate_owner_scoped_and_do_not_extend()
    {
        var type = typeof(ActivityMusicSuppressionStateStoreTests).Assembly.GetType(
            "DontStarve.Music.ActivityMusicSuppressionStateStore"
        );
        Assert.NotNull(type);
        var store = Activator.CreateInstance(type!, nonPublic: true);
        Assert.NotNull(store);

        var startDusk = Method(type!, "StartDusk");
        var startInsane = Method(type!, "StartInsane");
        var isFarmingBlocked = Method(type!, "IsFarmingBlocked");
        var isInsaneBlocked = Method(type!, "IsInsaneCooldownActive");
        var advance = Method(type!, "Advance");

        Assert.True((bool)startDusk.Invoke(store, new object[] { 1L, 15d })!);
        Assert.False((bool)startDusk.Invoke(store, new object[] { 1L, 15d })!);
        Assert.True((bool)isFarmingBlocked.Invoke(store, new object[] { 1L })!);
        Assert.False((bool)isFarmingBlocked.Invoke(store, new object[] { 2L })!);

        Assert.True((bool)startInsane.Invoke(store, new object[] { 1L, 15d })!);
        Assert.True((bool)isInsaneBlocked.Invoke(store, new object[] { 1L })!);

        advance.Invoke(store, new object[] { 15d });
        Assert.False((bool)isFarmingBlocked.Invoke(store, new object[] { 1L })!);
        Assert.False((bool)isInsaneBlocked.Invoke(store, new object[] { 1L })!);
    }

    [Fact]
    public void Dawn_and_dusk_phase_windows_both_block_farming_without_extending_each_other()
    {
        var type = typeof(ActivityMusicSuppressionStateStoreTests).Assembly.GetType(
            "DontStarve.Music.ActivityMusicSuppressionStateStore"
        );
        Assert.NotNull(type);
        var store = Activator.CreateInstance(type!, nonPublic: true);
        Assert.NotNull(store);

        var startDawn = Method(type!, "StartDawn");
        var startDusk = Method(type!, "StartDusk");
        var isFarmingBlocked = Method(type!, "IsFarmingBlocked");
        var advance = Method(type!, "Advance");

        Assert.True((bool)startDawn.Invoke(store, new object[] { 3L, 15d })!);
        Assert.False((bool)startDawn.Invoke(store, new object[] { 3L, 15d })!);
        Assert.True((bool)isFarmingBlocked.Invoke(store, new object[] { 3L })!);

        Assert.True((bool)startDusk.Invoke(store, new object[] { 3L, 15d })!);
        advance.Invoke(store, new object[] { 14d });
        Assert.True((bool)isFarmingBlocked.Invoke(store, new object[] { 3L })!);

        advance.Invoke(store, new object[] { 1d });
        Assert.False((bool)isFarmingBlocked.Invoke(store, new object[] { 3L })!);
    }

    [Fact]
    public void Cooldown_store_exposes_owner_and_global_cleanup()
    {
        var type = typeof(ActivityMusicSuppressionStateStoreTests).Assembly.GetType(
            "DontStarve.Music.ActivityMusicSuppressionStateStore"
        );
        Assert.NotNull(type);
        var store = Activator.CreateInstance(type!, nonPublic: true);
        Assert.NotNull(store);

        var startDusk = Method(type!, "StartDusk");
        var clearOwner = Method(type!, "ClearOwner");
        var clear = Method(type!, "Clear");
        var isFarmingBlocked = Method(type!, "IsFarmingBlocked");

        startDusk.Invoke(store, new object[] { 7L, 15d });
        clearOwner.Invoke(store, new object[] { 7L });
        Assert.False((bool)isFarmingBlocked.Invoke(store, new object[] { 7L })!);

        startDusk.Invoke(store, new object[] { 7L, 15d });
        clear.Invoke(store, null);
        Assert.False((bool)isFarmingBlocked.Invoke(store, new object[] { 7L })!);
    }

    private static MethodInfo Method(Type type, string name)
    {
        var method = type.GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(method);
        return method!;
    }
}
