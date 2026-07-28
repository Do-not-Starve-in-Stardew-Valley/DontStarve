using System.Text.Json;
using DontStarve.Player.Stats.Sanity;
using DontStarve.Resource.Sanity;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class SanityHarmlessProjectionFactContractTests
{
    private static string ShippedModRoot =>
        Path.Combine(AppContext.BaseDirectory, "ShippedMod");

    private static string ManifestPath =>
        Path.Combine(ShippedModRoot, "Asset", "Sanity", "Data", "sanity-assets.json");

    private static string AnimationPath =>
        Path.Combine(ShippedModRoot, "Asset", "Sanity", "Data", "animations.json");

    [Fact]
    public void OrdinaryPoliciesArePerOwnerPerSpeciesAndNeverUseTheShadowBudgetLane()
    {
        var policies = SanityHarmlessProjectionFactContract.OrdinaryPolicies;

        Assert.Equal(4, policies.Count);
        Assert.Equal(4, policies.Select(policy => policy.SpeciesId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(
            policies,
            policy =>
            {
                Assert.True(policy.FirstTierEntryAttemptIsImmediate);
                Assert.Equal(20, policy.AttemptIntervalMinutes);
                Assert.Equal(1, policy.ActiveCap);
                Assert.Equal(
                    HarmlessProjectionBudgetLane.OrdinaryPerOwnerPerSpecies,
                    policy.BudgetLane
                );
            }
        );

        var contractMemberNames = typeof(HarmlessProjectionFactPolicy)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(
            contractMemberNames,
            name => name.Contains("Intensity", StringComparison.OrdinalIgnoreCase)
        );
        Assert.DoesNotContain(
            contractMemberNames,
            name => name.Contains("Permit", StringComparison.OrdinalIgnoreCase)
        );
        Assert.DoesNotContain(
            contractMemberNames,
            name => name.Contains("ShadowBudget", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void OrdinaryPolicyMatrixMatchesTheFrozenTierCadenceCapAndTtlFacts()
    {
        AssertPolicy(
            "sanity.projection.mr-skitts",
            SanityTierIds.MrSkitts,
            5,
            10,
            20,
            true,
            HarmlessProjectionExitAction.Cleanup,
            HarmlessProjectionExitAction.None
        );
        AssertPolicy(
            "sanity.projection.dark-hand",
            SanityTierIds.DarkHand,
            10,
            20,
            40,
            false,
            HarmlessProjectionExitAction.DarkHandRetreatToOrigin,
            HarmlessProjectionExitAction.DarkHandRetreatToOrigin
        );
        AssertPolicy(
            "sanity.projection.dark-watcher",
            SanityTierIds.DarkWatcher,
            5,
            10,
            20,
            true,
            HarmlessProjectionExitAction.Cleanup,
            HarmlessProjectionExitAction.Cleanup
        );
        AssertPolicy(
            "sanity.projection.eyes",
            SanityTierIds.Eyes,
            5,
            15,
            20,
            true,
            HarmlessProjectionExitAction.Cleanup,
            HarmlessProjectionExitAction.Cleanup
        );
    }

    [Theory]
    [InlineData(HarmlessProjectionAttemptOutcome.Succeeded)]
    [InlineData(HarmlessProjectionAttemptOutcome.Failed)]
    public void SuccessAndFailureUseTheSameTwentyMinuteAttemptDeadline(
        HarmlessProjectionAttemptOutcome outcome
    )
    {
        Assert.Equal(
            120,
            SanityHarmlessProjectionFactContract.GetNextAttemptMinute(100, outcome)
        );
    }

    [Fact]
    public void AtCapIsAGateAndDoesNotPretendThatAnAttemptOccurred()
    {
        Assert.Null(
            SanityHarmlessProjectionFactContract.GetNextAttemptMinute(
                100,
                HarmlessProjectionAttemptOutcome.NotAttemptedAtCap
            )
        );
    }

    [Fact]
    public void CleanupReasonIdsRemainStableAndCoverEveryFrozenEarlyExit()
    {
        Assert.Equal(
            new[]
            {
                "config-disabled",
                "conversion-requested",
                "dark-hand-returned",
                "day-ending",
                "day-started-recovery",
                "event-override",
                "hard-ttl-expired",
                "light-restored",
                "location-invalid",
                "owner-approached",
                "owner-invalidated",
                "owner-warped",
                "resource-invalidated",
                "returned-title",
                "screen-invalid",
                "tier-exited",
                "world-cleanup",
            },
            SanityHarmlessProjectionFactContract.CleanupReasonIds
                .OrderBy(value => value, StringComparer.Ordinal)
        );
    }

    [Fact]
    public void UnknownEnvironmentLightCapabilityIsAlwaysHarmlessDimFallback()
    {
        var result = SanityHarmlessProjectionFactContract.UnknownLight(
            "environment-light.night-vision-api-unavailable"
        );

        Assert.Equal(EnvironmentLightFactLevel.Dim, result.Level);
        Assert.Equal(EnvironmentLightFactEvidenceStatus.Fallback, result.EvidenceStatus);
        Assert.False(result.CanCauseDamage);
        Assert.Equal("environment-light.night-vision-api-unavailable", result.Reason);
    }

    [Fact]
    public void RuntimeMetadataMatchesEveryFrozenOrdinaryResourceIdentity()
    {
        var manifestResult = SanityAssetManifestParser.Parse(File.ReadAllText(ManifestPath));
        Assert.True(manifestResult.Success, manifestResult.Reason);
        var manifest = Assert.IsType<SanityAssetManifest>(manifestResult.Manifest);

        using var animations = JsonDocument.Parse(File.ReadAllText(AnimationPath));
        var profiles = animations.RootElement
            .GetProperty("AnimationProfiles")
            .EnumerateArray()
            .ToArray();

        foreach (var policy in SanityHarmlessProjectionFactContract.OrdinaryPolicies)
        {
            var textureSlot = Assert.Single(
                manifest.Slots.Where(slot => slot.SlotId == policy.TextureSlotId)
            );
            Assert.Equal(policy.TextureIsPlaceholder, textureSlot.IsPlaceholder);

            var profile = Assert.Single(
                profiles.Where(value =>
                    value.GetProperty("AnimationProfileId").GetString()
                    == policy.AnimationProfileId
                )
            );
            Assert.Equal(
                policy.TextureSlotId,
                profile.GetProperty("TextureSlotId").GetString()
            );
            Assert.True(profile.GetProperty("OwnerLocalOnly").GetBoolean());
            Assert.True(profile.GetProperty("IsPlaceholder").GetBoolean());

            var states = profile.GetProperty("States").EnumerateArray().ToArray();
            Assert.Equal(
                policy.AnimationIds.OrderBy(value => value, StringComparer.Ordinal),
                states
                    .Select(value => value.GetProperty("AnimationId").GetString())
                    .OrderBy(value => value, StringComparer.Ordinal)
            );
            Assert.All(
                states,
                state =>
                {
                    Assert.True(state.GetProperty("IsProvisional").GetBoolean());
                    Assert.True(state.GetProperty("FrameDurationMs").GetInt32() > 0);
                    Assert.Contains(
                        state.GetProperty("SortLayer").GetString(),
                        new[] { "World", "Screen" }
                    );
                }
            );
        }
    }

    private static void AssertPolicy(
        string speciesId,
        string tierId,
        int minimumDistanceTiles,
        int maximumDistanceTiles,
        int hardTtlMinutes,
        bool clearOnTierExit,
        HarmlessProjectionExitAction ownerApproachAction,
        HarmlessProjectionExitAction lightRestoredAction
    )
    {
        var policy = Assert.Single(
            SanityHarmlessProjectionFactContract.OrdinaryPolicies.Where(value =>
                value.SpeciesId == speciesId
            )
        );

        Assert.Equal(tierId, policy.TierId);
        Assert.Equal(minimumDistanceTiles, policy.MinimumDistanceTiles);
        Assert.Equal(maximumDistanceTiles, policy.MaximumDistanceTiles);
        Assert.Equal(hardTtlMinutes, policy.HardTtlMinutes);
        Assert.Equal(clearOnTierExit, policy.ClearOnTierExit);
        Assert.Equal(ownerApproachAction, policy.OwnerApproachAction);
        Assert.Equal(lightRestoredAction, policy.LightRestoredAction);
    }
}

internal enum HarmlessProjectionBudgetLane
{
    OrdinaryPerOwnerPerSpecies,
}

internal enum HarmlessProjectionExitAction
{
    None,
    Cleanup,
    DarkHandRetreatToOrigin,
}

public enum HarmlessProjectionAttemptOutcome
{
    Succeeded,
    Failed,
    NotAttemptedAtCap,
}

internal enum EnvironmentLightFactLevel
{
    Lit,
    Dim,
    PitchBlack,
}

internal enum EnvironmentLightFactEvidenceStatus
{
    Confirmed,
    Fallback,
}

internal sealed record EnvironmentLightFactResult(
    EnvironmentLightFactLevel Level,
    EnvironmentLightFactEvidenceStatus EvidenceStatus,
    string Reason,
    bool CanCauseDamage
);

internal sealed record HarmlessProjectionFactPolicy(
    string SpeciesId,
    string TierId,
    int MinimumDistanceTiles,
    int MaximumDistanceTiles,
    bool FirstTierEntryAttemptIsImmediate,
    int AttemptIntervalMinutes,
    int ActiveCap,
    int HardTtlMinutes,
    bool ClearOnTierExit,
    HarmlessProjectionExitAction OwnerApproachAction,
    HarmlessProjectionExitAction LightRestoredAction,
    HarmlessProjectionBudgetLane BudgetLane,
    string TextureSlotId,
    bool TextureIsPlaceholder,
    string AnimationProfileId,
    IReadOnlyList<string> AnimationIds
);

internal static class SanityHarmlessProjectionFactContract
{
    internal static IReadOnlyList<HarmlessProjectionFactPolicy> OrdinaryPolicies { get; } =
        Array.AsReadOnly(
            new[]
            {
                new HarmlessProjectionFactPolicy(
                    "sanity.projection.mr-skitts",
                    SanityTierIds.MrSkitts,
                    5,
                    10,
                    true,
                    20,
                    1,
                    20,
                    true,
                    HarmlessProjectionExitAction.Cleanup,
                    HarmlessProjectionExitAction.None,
                    HarmlessProjectionBudgetLane.OrdinaryPerOwnerPerSpecies,
                    "sanity.asset.mr-skitts.sprite",
                    false,
                    "sanity.animation.mr-skitts.profile",
                    Array.AsReadOnly(
                        new[]
                        {
                            "sanity.animation.mr-skitts.idle",
                            "sanity.animation.mr-skitts.disappear",
                        }
                    )
                ),
                new HarmlessProjectionFactPolicy(
                    "sanity.projection.dark-hand",
                    SanityTierIds.DarkHand,
                    10,
                    20,
                    true,
                    20,
                    1,
                    40,
                    false,
                    HarmlessProjectionExitAction.DarkHandRetreatToOrigin,
                    HarmlessProjectionExitAction.DarkHandRetreatToOrigin,
                    HarmlessProjectionBudgetLane.OrdinaryPerOwnerPerSpecies,
                    "sanity.asset.dark-hand.sprite",
                    false,
                    "sanity.animation.dark-hand.profile",
                    Array.AsReadOnly(
                        new[]
                        {
                            "sanity.animation.dark-hand.appear",
                            "sanity.animation.dark-hand.move",
                            "sanity.animation.dark-hand.interact",
                            "sanity.animation.dark-hand.retreat",
                            "sanity.animation.dark-hand.disappear",
                        }
                    )
                ),
                new HarmlessProjectionFactPolicy(
                    "sanity.projection.dark-watcher",
                    SanityTierIds.DarkWatcher,
                    5,
                    10,
                    true,
                    20,
                    1,
                    20,
                    true,
                    HarmlessProjectionExitAction.Cleanup,
                    HarmlessProjectionExitAction.Cleanup,
                    HarmlessProjectionBudgetLane.OrdinaryPerOwnerPerSpecies,
                    "sanity.asset.dark-watcher.sprite",
                    false,
                    "sanity.animation.dark-watcher.profile",
                    Array.AsReadOnly(
                        new[]
                        {
                            "sanity.animation.dark-watcher.appear",
                            "sanity.animation.dark-watcher.idle",
                            "sanity.animation.dark-watcher.disappear",
                        }
                    )
                ),
                new HarmlessProjectionFactPolicy(
                    "sanity.projection.eyes",
                    SanityTierIds.Eyes,
                    5,
                    15,
                    true,
                    20,
                    1,
                    20,
                    true,
                    HarmlessProjectionExitAction.Cleanup,
                    HarmlessProjectionExitAction.Cleanup,
                    HarmlessProjectionBudgetLane.OrdinaryPerOwnerPerSpecies,
                    "sanity.asset.eyes.sprite",
                    true,
                    "sanity.animation.eyes.profile",
                    Array.AsReadOnly(new[] { "sanity.animation.eyes.blink" })
                ),
            }
        );

    internal static IReadOnlyList<string> CleanupReasonIds { get; } =
        Array.AsReadOnly(
            new[]
            {
                "owner-approached",
                "tier-exited",
                "light-restored",
                "config-disabled",
                "event-override",
                "owner-warped",
                "day-ending",
                "day-started-recovery",
                "returned-title",
                "owner-invalidated",
                "screen-invalid",
                "location-invalid",
                "hard-ttl-expired",
                "resource-invalidated",
                "dark-hand-returned",
                "conversion-requested",
                "world-cleanup",
            }
        );

    internal static long? GetNextAttemptMinute(
        long attemptedAtMinute,
        HarmlessProjectionAttemptOutcome outcome
    )
    {
        return outcome is HarmlessProjectionAttemptOutcome.Succeeded
            or HarmlessProjectionAttemptOutcome.Failed
            ? checked(attemptedAtMinute + 20)
            : null;
    }

    internal static EnvironmentLightFactResult UnknownLight(string reason)
    {
        return new EnvironmentLightFactResult(
            EnvironmentLightFactLevel.Dim,
            EnvironmentLightFactEvidenceStatus.Fallback,
            reason,
            CanCauseDamage: false
        );
    }
}
