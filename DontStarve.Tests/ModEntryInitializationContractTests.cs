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
            "var coreServices = InitializeCoreServices(helper);",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "var hungerDebugController = coreServices.HungerDebugController;",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "private (HungerDebugController HungerDebugController, Health Health) InitializeCoreServices(",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "new PlayerResourceDebugCommands(Monitor, coreServices.Health).Register(helper);",
            source,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void DisabledProjectionStartupDoesNotLogInternalPlanDetails()
    {
        var source = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "ShadowProjection",
                "ModEntry.cs"
            )
        );

        Assert.DoesNotContain("see 文档/当前在做计划", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "OwnerLocalProjectionDisableList.DisabledReason",
            source,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "owner-local projection registration skipped",
            source,
            StringComparison.Ordinal
        );
        Assert.Equal(
            4,
            source.Split(
                "if (!OwnerLocalProjectionDisableList.IsDisabled(",
                StringSplitOptions.None
            ).Length - 1
        );
    }
}
