using Xunit;

namespace DontStarve.Tests.Audio.StatusFeedback;

public sealed class StatusFeedbackAudioRuntimeContractTests
{
    [Fact]
    public void Playing_a_duplicate_status_cue_does_not_stop_the_existing_instance()
    {
        var source = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "StatusFeedback",
                "StatusFeedbackAudioService.cs"
            )
        );
        var playStart = source.IndexOf("internal bool Play(string cueId)", StringComparison.Ordinal);
        var disposeStart = source.IndexOf("public void Dispose()", playStart, StringComparison.Ordinal);

        Assert.True(playStart >= 0);
        Assert.True(disposeStart > playStart);

        var playMethod = source[playStart..disposeStart];
        Assert.DoesNotContain("instance.Stop()", playMethod, StringComparison.Ordinal);
    }
}
