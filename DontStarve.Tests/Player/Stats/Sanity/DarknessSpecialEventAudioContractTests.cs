using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class DarknessSpecialEventAudioContractTests
{
    [Fact]
    public void EventSuspensionKeepsSpecialAttackLaneEligible()
    {
        var source = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "AudioPool",
                "SanitySmapiAudioService.cs"
            )
        );

        Assert.Contains(
            "(suspended && !specialEventAudioAllowed)",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains("darknessAttackLane?.StopPlayback();", source, StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("if (!specialEventAudioAllowed)", StringComparison.Ordinal)
                < source.LastIndexOf("darknessAttackLane?.StopPlayback();", StringComparison.Ordinal)
        );
    }
}
