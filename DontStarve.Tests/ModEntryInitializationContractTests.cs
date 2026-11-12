using Xunit;

namespace DontStarve.Tests;

public sealed class ModEntryInitializationContractTests
{
    [Fact]
    public void EntryDelegatesCoreServiceConstructionToANamedPhase()
    {
        var source = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "ShadowProjection",
                "ModEntry.cs"
            )
        );

        Assert.Contains(
            "var hungerDebugController = InitializeCoreServices(helper);",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "private HungerDebugController InitializeCoreServices(IModHelper helper)",
            source,
            StringComparison.Ordinal
        );
    }
}
