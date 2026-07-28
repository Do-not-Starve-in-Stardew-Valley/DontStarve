using DontStarve.Player.Stats.Sanity.Darkness;
using DontStarve.Player.Stats.Sanity.Illusions.Lighting;
using Xunit;

namespace DontStarve.Tests.Player.Stats.Sanity;

public sealed class EnvironmentLightProductionTests
{
    private const string SessionId = "11111111111111111111111111111111";
    private const string PlayerKey = "123";
    private const string LocationName = "Farm";
    private const string ModVersion = "1.0.0";
    private static readonly EnvironmentLightThresholds Thresholds =
        EnvironmentLightThresholds.Default;
    private static readonly EnvironmentLightConfigIdentity Config =
        new(2, new string('A', 64));

    [Fact]
    public void ReverseSubtractVisibilityMapsWhiteDarknessToBlackAndBlackToLit()
    {
        Assert.Equal(
            0d,
            EnvironmentLightVisibilityMath.ComputeVisibilityScore(
                1d,
                1d,
                1d,
                rainOverlayApplied: false
            ),
            precision: 10
        );
        Assert.Equal(
            1d,
            EnvironmentLightVisibilityMath.ComputeVisibilityScore(
                0d,
                0d,
                0d,
                rainOverlayApplied: false
            ),
            precision: 10
        );
        Assert.True(
            EnvironmentLightVisibilityMath.ComputeVisibilityScore(
                0.75d,
                0.75d,
                0.75d,
                rainOverlayApplied: true
            )
            < EnvironmentLightVisibilityMath.ComputeVisibilityScore(
                0.75d,
                0.75d,
                0.75d,
                rainOverlayApplied: false
            )
        );
    }

    [Fact]
    public void FarmHasARealConfirmedPitchBlackAuthorizationPath()
    {
        var result = new EnvironmentLightClassifier().Classify(
            Snapshot(score: 0.10d, darknessAttackSafe: true)
        );

        Assert.Equal(EnvironmentLightLevel.PitchBlack, result.Level);
        Assert.Equal(EnvironmentLightEvidenceStatus.Confirmed, result.EvidenceStatus);
        Assert.True(result.PitchBlackAuthorized);
        Assert.Equal(
            EnvironmentLightReasonIds.FinalVisibilityPitchBlackConfirmed,
            result.Reason
        );
    }

    [Fact]
    public void StandardLightRecoveryImmediatelyRevokesPitchBlackAuthorization()
    {
        var classifier = new EnvironmentLightClassifier();
        var dark = classifier.Classify(
            Snapshot(score: 0.10d, darknessAttackSafe: true)
        );
        var lit = classifier.Classify(
            Snapshot(score: 0.80d, darknessAttackSafe: true, revision: 2),
            dark.Level
        );

        Assert.True(dark.PitchBlackAuthorized);
        Assert.Equal(EnvironmentLightLevel.Lit, lit.Level);
        Assert.Equal(EnvironmentLightEvidenceStatus.Confirmed, lit.EvidenceStatus);
        Assert.False(lit.PitchBlackAuthorized);
    }

    [Fact]
    public void PitchBlackExitHysteresisNeverExtendsHarmAuthorization()
    {
        var classifier = new EnvironmentLightClassifier();
        var hysteresis = classifier.Classify(
            Snapshot(score: 0.25d, darknessAttackSafe: true),
            EnvironmentLightLevel.PitchBlack
        );
        var withoutHistory = classifier.Classify(
            Snapshot(score: 0.25d, darknessAttackSafe: true, revision: 2)
        );

        Assert.Equal(EnvironmentLightLevel.PitchBlack, hysteresis.Level);
        Assert.False(hysteresis.PitchBlackAuthorized);
        Assert.Equal(EnvironmentLightLevel.Dim, withoutHistory.Level);
    }

    [Fact]
    public void UnsafeLocationCanClassifyPitchBlackButNeverAuthorizeDamage()
    {
        var result = new EnvironmentLightClassifier().Classify(
            Snapshot(score: 0.10d, darknessAttackSafe: false)
        );

        Assert.Equal(EnvironmentLightLevel.PitchBlack, result.Level);
        Assert.Equal(EnvironmentLightEvidenceStatus.Confirmed, result.EvidenceStatus);
        Assert.False(result.PitchBlackAuthorized);
        Assert.Equal(
            EnvironmentLightReasonIds.FinalVisibilityPitchBlackLocationUnsafe,
            result.Reason
        );
    }

    [Fact]
    public void ThresholdsAreClampedOrderedAndRevisionBound()
    {
        var clamped = EnvironmentLightThresholds.CreateClamped(
            double.NaN,
            -10d,
            2d,
            double.PositiveInfinity
        );

        Assert.InRange(clamped.PitchBlackEnter, 0.05d, 0.45d);
        Assert.True(clamped.PitchBlackEnter < clamped.PitchBlackExit);
        Assert.True(clamped.PitchBlackExit < clamped.LitExit);
        Assert.True(clamped.LitExit < clamped.LitEnter);
        Assert.Equal(
            EnvironmentLightProductionContract.RuleRevision,
            Thresholds.RuleRevision
        );
    }

    [Fact]
    public void BilinearReadbackUsesAllFourBoundedTexels()
    {
        var sampled = EnvironmentLightVisibilityMath.Interpolate(
            new EnvironmentLightColor(0, 0, 0, 255),
            new EnvironmentLightColor(100, 0, 0, 255),
            new EnvironmentLightColor(0, 100, 0, 255),
            new EnvironmentLightColor(100, 100, 0, 255),
            0.5d,
            0.5d
        );

        Assert.Equal((byte)50, sampled.R);
        Assert.Equal((byte)50, sampled.G);
        Assert.Equal((byte)0, sampled.B);
    }

    [Fact]
    public void ValidRemoteEvidencePassesEveryHostInvariant()
    {
        var validation = EnvironmentLightEvidenceProtocol.Validate(
            ValidMessage(),
            ValidationContext()
        );

        Assert.True(validation.Accepted, validation.Reason);
        Assert.Equal(EnvironmentLightLevel.PitchBlack, validation.Level);
        Assert.Equal(
            EnvironmentLightEvidenceStatus.Confirmed,
            validation.EvidenceStatus
        );
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("location")]
    [InlineData("version")]
    [InlineData("revision")]
    [InlineData("config")]
    [InlineData("sequence")]
    [InlineData("clock")]
    public void RemoteEvidenceFailsClosedOnAuthorityOrFreshnessMismatch(
        string mutation
    )
    {
        var message = ValidMessage();
        var context = ValidationContext();
        switch (mutation)
        {
            case "owner":
                message.PlayerKey = "999";
                break;
            case "location":
                message.LocationNameOrUniqueName = "Town";
                break;
            case "version":
                message.GameVersion = "1.6.16";
                break;
            case "revision":
                message.EvaluatorRevision = "unknown-evaluator";
                break;
            case "config":
                message.ConfigFingerprint = new string('B', 64);
                break;
            case "sequence":
                context = context with { LastAcceptedSequence = message.Sequence };
                break;
            case "clock":
                message.SampleUtcMilliseconds =
                    context.HostUtcMilliseconds
                    - EnvironmentLightMultiplayerContract
                        .MaximumSampleClockSkewMilliseconds
                    - 1L;
                break;
        }

        var validation = EnvironmentLightEvidenceProtocol.Validate(
            message,
            context
        );

        Assert.False(validation.Accepted);
    }

    [Fact]
    public void RemoteAuthorizationMustEqualHostRuleAndStrictThreshold()
    {
        var unsafeContext = ValidationContext() with
        {
            ExpectedDarknessAttackSafe = false,
        };
        var message = ValidMessage();

        var rejected = EnvironmentLightEvidenceProtocol.Validate(
            message,
            unsafeContext
        );
        message.PitchBlackAuthorized = false;
        message.Reason =
            EnvironmentLightReasonIds.FinalVisibilityPitchBlackLocationUnsafe;
        var accepted = EnvironmentLightEvidenceProtocol.Validate(
            message,
            unsafeContext
        );

        Assert.False(rejected.Accepted);
        Assert.True(accepted.Accepted, accepted.Reason);
    }

    [Fact]
    public void RemoteHysteresisClassificationIsAllowedButCannotAuthorize()
    {
        var message = ValidMessage();
        message.VisibilityScore = 0.25d;
        message.PitchBlackAuthorized = false;
        message.Reason =
            EnvironmentLightReasonIds.FinalVisibilityPitchBlackLocationUnsafe;

        var result = EnvironmentLightEvidenceProtocol.Validate(
            message,
            ValidationContext()
        );

        Assert.True(result.Accepted, result.Reason);
        Assert.Equal(EnvironmentLightLevel.PitchBlack, result.Level);
    }

    [Fact]
    public void RuntimeSamplerUsesOneFixedTwoByTwoReadbackAndNoPrivateRenderTarget()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "EnvironmentLightApiFact"
        );
        var sampler = File.ReadAllText(
            Path.Combine(root, "SmapiEnvironmentLightFinalVisibilitySampler.cs")
        );

        Assert.Contains("RenderedWorld", sampler, StringComparison.Ordinal);
        Assert.Contains("lightmap.GetData(0, region, readback, 0, readback.Length)", sampler, StringComparison.Ordinal);
        Assert.Contains("ReadbackWidth", sampler, StringComparison.Ordinal);
        Assert.Contains("ReadbackHeight", sampler, StringComparison.Ordinal);
        Assert.Contains("DangerousSampleCadenceTicks", sampler, StringComparison.Ordinal);
        Assert.DoesNotContain("new RenderTarget2D", sampler, StringComparison.Ordinal);
        Assert.DoesNotContain("lightmap.GetData(readback)", sampler, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var pixel", sampler, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteRuntimeBindsSenderAndNeverEvaluatesHostFramebufferForFarmhand()
    {
        var source = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Contracts",
                "DarknessAttack",
                "SmapiEnvironmentLightMultiplayerCoordinator.cs"
            )
        );

        Assert.Contains("e.FromPlayerID", source, StringComparison.Ordinal);
        Assert.Contains("Game1.GetPlayer(e.FromPlayerID, onlyOnline: true)", source, StringComparison.Ordinal);
        Assert.Contains("EnvironmentLightEvidenceProtocol.Validate(", source, StringComparison.Ordinal);
        Assert.Contains("lastAcceptedSequenceByPlayer", source, StringComparison.Ordinal);
        Assert.Contains("MaximumHostEvidenceAgeMilliseconds", source, StringComparison.Ordinal);
        Assert.DoesNotContain("lightmap", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("currentLightSources", source, StringComparison.Ordinal);
    }

    private static EnvironmentLightSnapshot Snapshot(
        double score,
        bool darknessAttackSafe,
        long revision = 1
    )
    {
        return new EnvironmentLightSnapshot(
            PlayerKey,
            0,
            LocationName,
            10,
            new EnvironmentLightLocationSnapshot(
                "StardewValley.Farm",
                "Default",
                LocationName,
                isOutdoors: true,
                isTemporary: false,
                isEventActive: false,
                isFestivalActive: false
            ),
            new EnvironmentLightLocationRuleResolution(
                EnvironmentLightLocationRuleStatus.Matched,
                2,
                "vanilla.farm",
                EnvironmentLightLocationLightProfile.OpaqueWhiteBase,
                TwoAmSpecialDeathSafe: false,
                DarknessAttackSafe: darknessAttackSafe,
                HostileShadowSafe: false,
                JunimoBlessingEligible: true,
                EnvironmentLightReasonIds.LocationRuleMatched
            ),
            new EnvironmentLightWorldPoint(100d, 100d),
            EnvironmentLightCapabilityStatus.Available,
            EnvironmentLightBaseSource.Outdoor,
            new EnvironmentLightColor(255, 255, 255, 255),
            EnvironmentLightCapabilityStatus.Available,
            0f,
            isDarkOut: true,
            EnvironmentLightCapabilityStatus.Unavailable,
            null,
            new EnvironmentLightNightVisionSnapshot(
                EnvironmentLightCapabilityStatus.Available,
                false,
                EnvironmentLightReasonIds.NightVisionKnownInactive,
                PlayerKey,
                0,
                LocationName,
                10
            ),
            EnvironmentLightCapabilityStatus.Available,
            "environment-light.candidates-captured",
            0,
            0,
            Array.Empty<EnvironmentLightCandidateSnapshot>(),
            600,
            100,
            revision,
            EnvironmentLightFinalVisibilitySnapshot.Confirmed(
                PlayerKey,
                0,
                LocationName,
                10,
                score,
                0.5d,
                0.5d,
                0.5d,
                standardLightingDrawn: true,
                rainOverlayApplied: false,
                lightingQuality: 2,
                zoomLevel: 1d,
                useUnscaledLighting: false,
                capturedAtTick: 100,
                rendererRevision: revision,
                "environment-light.final-visibility-owner-foot-lightmap"
            )
        );
    }

    private static EnvironmentLightEvidenceMessage ValidMessage()
    {
        const long now = 2_000_000L;
        return new EnvironmentLightEvidenceMessage
        {
            ProtocolVersion = EnvironmentLightMultiplayerContract.ProtocolVersion,
            SessionId = SessionId,
            PlayerKey = PlayerKey,
            ScreenId = 0,
            LocationNameOrUniqueName = LocationName,
            LocationRuleContractVersion = 2,
            LocationRuleId = "vanilla.farm",
            ConfigSchemaVersion = Config.SchemaVersion,
            ConfigFingerprint = Config.Fingerprint,
            GameVersion = EnvironmentLightProductionContract.SupportedGameVersion,
            ModVersion = ModVersion,
            EvaluatorRevision = EnvironmentLightProductionContract.EvaluatorRevision,
            RuleRevision = EnvironmentLightProductionContract.RuleRevision,
            Sequence = 7,
            SampleTick = 100,
            SampleUtcMilliseconds = now,
            RendererRevision = 4,
            VisibilityScore = 0.10d,
            Classification = EnvironmentLightLevel.PitchBlack.ToString(),
            EvidenceStatus = EnvironmentLightEvidenceStatus.Confirmed.ToString(),
            PitchBlackAuthorized = true,
            ExplicitNightVisionActive = false,
            GameplaySettleable = true,
            Paused = false,
            Reason =
                EnvironmentLightReasonIds.FinalVisibilityPitchBlackConfirmed,
        };
    }

    private static EnvironmentLightEvidenceValidationContext ValidationContext()
    {
        return new EnvironmentLightEvidenceValidationContext(
            SessionId,
            PlayerKey,
            LocationName,
            2,
            "vanilla.farm",
            ExpectedDarknessAttackSafe: true,
            Config,
            EnvironmentLightProductionContract.SupportedGameVersion,
            ModVersion,
            LastAcceptedSequence: 6,
            HostUtcMilliseconds: 2_000_000L
        );
    }
}
