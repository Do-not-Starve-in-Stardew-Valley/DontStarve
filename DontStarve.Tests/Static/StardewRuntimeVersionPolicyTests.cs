using Xunit;

namespace DontStarve.Tests.Static;

public sealed class StardewRuntimeVersionPolicyTests
{
    [Fact]
    public void Desktop_contract_remains_exact_and_rejects_android_version()
    {
        Assert.True(
            DontStarve.StardewRuntimeVersionPolicy.IsSupportedGameVersion(
                "1.6.15",
                isAndroidPlatform: false
            )
        );
        Assert.False(
            DontStarve.StardewRuntimeVersionPolicy.IsSupportedGameVersion(
                "1.6.15.3",
                isAndroidPlatform: false
            )
        );
    }

    [Fact]
    public void Android_contract_accepts_only_android_version()
    {
        Assert.True(
            DontStarve.StardewRuntimeVersionPolicy.IsSupportedGameVersion(
                "1.6.15.3",
                isAndroidPlatform: true
            )
        );
        Assert.False(
            DontStarve.StardewRuntimeVersionPolicy.IsSupportedGameVersion(
                "1.6.15",
                isAndroidPlatform: true
            )
        );
        Assert.Equal(
            "1.6.15",
            DontStarve.StardewRuntimeVersionPolicy.ExpectedGameVersion(
                isAndroidPlatform: false
            )
        );
        Assert.Equal(
            "1.6.15.3",
            DontStarve.StardewRuntimeVersionPolicy.ExpectedGameVersion(
                isAndroidPlatform: true
            )
        );
    }
}
