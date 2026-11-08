using System.IO;
using System.Text.Json;
using Xunit;

namespace DontStarve.Tests.Player.Sleep;

public sealed class SleepConfirmationLocalizationTests
{
    [Theory]
    [InlineData("zh.json", "当前饥饿值太低，现在睡下去会很危险，要继续吗？")]
    [InlineData("default.json", "Your current Hunger is too low. Going to sleep now will be very dangerous. Continue?")]
    public void Sleep_confirmation_body_uses_the_explicit_current_hunger_warning(
        string localeFile,
        string expectedText
    )
    {
        var path = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../DontStarve/i18n",
                localeFile
            )
        );

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var actualText = document.RootElement
            .GetProperty("sleep-confirmation.body")
            .GetString();

        Assert.Equal(expectedText, actualText);
    }
}
