using Xunit;

namespace DontStarve.Tests.Sanity.PassOut;

public sealed class SanityTwoAmSpecialDeathRuntimeContractTests
{
    [Fact]
    public void RuntimeOwnsThePostAnimationPassOutSeamAndFrozenPromptKeys()
    {
        var source = ReadContract("SmapiSanityTwoAmSpecialDeathService.cs");

        Assert.Contains("passOutFromTired", source, StringComparison.Ordinal);
        Assert.DoesNotContain("startToPassOut", source, StringComparison.Ordinal);
        Assert.Contains("darkness-attack.prompt.entered", source, StringComparison.Ordinal);
        Assert.Contains("darkness-attack.prompt.warning", source, StringComparison.Ordinal);
        Assert.Contains("darkness-attack.prompt.resolved", source, StringComparison.Ordinal);
        Assert.Contains("PlayerDeathCauseLedger", source, StringComparison.Ordinal);
        Assert.DoesNotContain("addMailForTomorrow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PassOutMail", source, StringComparison.Ordinal);
        Assert.DoesNotContain("passout.two-am.mail.body", source, StringComparison.Ordinal);
        Assert.DoesNotContain("warpFarmer(\"Hospital\"", source, StringComparison.Ordinal);
    }

    private static string ReadContract(string fileName) => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Contracts", "PassOut", fileName)
    );
}
