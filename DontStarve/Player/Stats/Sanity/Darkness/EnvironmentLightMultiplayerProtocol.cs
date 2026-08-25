#nullable enable

using System;
using DontStarve.Player.Stats.Sanity.Illusions.Lighting;

namespace DontStarve.Player.Stats.Sanity.Darkness;

/// <summary>
/// Wire contract for owner-local final-light evidence. The client reports evidence only; damage,
/// countdown state, and settlement remain host-owned.
/// </summary>
internal static class EnvironmentLightMultiplayerContract
{
    internal const int ProtocolVersion = 1;
    internal const string EvidenceMessageType = "EnvironmentLight.Evidence.v1";
    internal const string PresentationMessageType =
        "EnvironmentLight.Presentation.v1";
    internal const int MaximumLocationLength = 192;
    internal const int MaximumReasonLength = 192;
    internal const int MaximumFingerprintLength = 128;
    internal const int MaximumRemoteOwners = 16;
    internal const int EvidenceReportCadenceTicks = 15;
    internal const long MaximumHostEvidenceAgeMilliseconds = 1_500L;
    internal const long MaximumSampleClockSkewMilliseconds = 15_000L;
}

internal readonly record struct EnvironmentLightConfigIdentity(
    int SchemaVersion,
    string Fingerprint
)
{
    internal static EnvironmentLightConfigIdentity Unavailable => new(0, string.Empty);

    internal bool IsAvailable =>
        SchemaVersion > 0
        && Fingerprint.Length == 64
        && IsHex(Fingerprint);

    private static bool IsHex(string value)
    {
        foreach (var character in value)
        {
            if (
                character is not (>= '0' and <= '9')
                && character is not (>= 'a' and <= 'f')
                && character is not (>= 'A' and <= 'F')
            )
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>SMAPI JSON DTO. All properties remain bounded and primitive.</summary>
internal sealed class EnvironmentLightEvidenceMessage
{
    public int ProtocolVersion { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string PlayerKey { get; set; } = string.Empty;
    public int ScreenId { get; set; }
    public string LocationNameOrUniqueName { get; set; } = string.Empty;
    public int LocationRuleContractVersion { get; set; }
    public string LocationRuleId { get; set; } = string.Empty;
    public int ConfigSchemaVersion { get; set; }
    public string ConfigFingerprint { get; set; } = string.Empty;
    public string GameVersion { get; set; } = string.Empty;
    public string ModVersion { get; set; } = string.Empty;
    public string EvaluatorRevision { get; set; } = string.Empty;
    public string RuleRevision { get; set; } = string.Empty;
    public long Sequence { get; set; }
    public long SampleTick { get; set; }
    public long SampleUtcMilliseconds { get; set; }
    public long RendererRevision { get; set; }
    public double VisibilityScore { get; set; }
    public string Classification { get; set; } = string.Empty;
    public string EvidenceStatus { get; set; } = string.Empty;
    public bool PitchBlackAuthorized { get; set; }
    public bool ExplicitNightVisionActive { get; set; }
    public bool GameplaySettleable { get; set; }
    public bool Paused { get; set; }
    public string Reason { get; set; } = string.Empty;
}

internal readonly record struct EnvironmentLightEvidenceValidationContext(
    string ExpectedSessionId,
    string ExpectedPlayerKey,
    string ExpectedLocationNameOrUniqueName,
    int ExpectedLocationRuleContractVersion,
    string ExpectedLocationRuleId,
    bool ExpectedDarknessAttackLocationAuthorized,
    EnvironmentLightConfigIdentity ExpectedConfig,
    string ExpectedGameVersion,
    string ExpectedModVersion,
    long LastAcceptedSequence,
    long HostUtcMilliseconds
);

internal readonly record struct EnvironmentLightEvidenceValidationResult(
    bool Accepted,
    string Reason,
    EnvironmentLightLevel Level,
    EnvironmentLightEvidenceStatus EvidenceStatus
)
{
    internal static EnvironmentLightEvidenceValidationResult Rejected(
        string reason
    )
    {
        return new EnvironmentLightEvidenceValidationResult(
            false,
            reason,
            EnvironmentLightLevel.Dim,
            EnvironmentLightEvidenceStatus.Fallback
        );
    }
}

internal sealed record EnvironmentLightAcceptedRemoteEvidence(
    DarknessAttackOwnerKey Key,
    long SenderPlayerId,
    string LocationNameOrUniqueName,
    int LocationRuleContractVersion,
    string LocationRuleId,
    EnvironmentLightConfigIdentity Config,
    long Sequence,
    long SampleTick,
    long RendererRevision,
    long ReceivedAtMonotonicMilliseconds,
    double VisibilityScore,
    EnvironmentLightLevel Level,
    EnvironmentLightEvidenceStatus EvidenceStatus,
    bool PitchBlackAuthorized,
    bool GameplaySettleable,
    bool Paused,
    string Reason
);

internal static class EnvironmentLightEvidenceProtocol
{
    internal static EnvironmentLightEvidenceValidationResult Validate(
        EnvironmentLightEvidenceMessage? message,
        EnvironmentLightEvidenceValidationContext context,
        EnvironmentLightThresholds? thresholds = null
    )
    {
        thresholds ??= EnvironmentLightThresholds.Default;
        if (message is null)
            return Reject("environment-light.network.message-null");
        if (message.ProtocolVersion != EnvironmentLightMultiplayerContract.ProtocolVersion)
            return Reject("environment-light.network.protocol-mismatch");
        if (
            !SanityProtocol.IsValidSessionId(context.ExpectedSessionId)
            || !string.Equals(
                message.SessionId,
                context.ExpectedSessionId,
                StringComparison.Ordinal
            )
        )
        {
            return Reject("environment-light.network.session-mismatch");
        }
        if (
            !SanityPlayerKey.IsCanonical(context.ExpectedPlayerKey)
            || !string.Equals(
                message.PlayerKey,
                context.ExpectedPlayerKey,
                StringComparison.Ordinal
            )
            || message.ScreenId < 0
            || message.ScreenId >= EnvironmentLightMultiplayerContract.MaximumRemoteOwners
        )
        {
            return Reject("environment-light.network.owner-mismatch");
        }
        if (
            string.IsNullOrWhiteSpace(context.ExpectedLocationNameOrUniqueName)
            || context.ExpectedLocationNameOrUniqueName.Length
                > EnvironmentLightMultiplayerContract.MaximumLocationLength
            || !string.Equals(
                message.LocationNameOrUniqueName,
                context.ExpectedLocationNameOrUniqueName,
                StringComparison.Ordinal
            )
        )
        {
            return Reject("environment-light.network.location-mismatch");
        }
        if (
            message.LocationRuleContractVersion
                != context.ExpectedLocationRuleContractVersion
            || !string.Equals(
                message.LocationRuleId,
                context.ExpectedLocationRuleId,
                StringComparison.Ordinal
            )
        )
        {
            return Reject("environment-light.network.location-rule-mismatch");
        }
        if (
            !context.ExpectedConfig.IsAvailable
            || message.ConfigSchemaVersion != context.ExpectedConfig.SchemaVersion
            || !string.Equals(
                message.ConfigFingerprint,
                context.ExpectedConfig.Fingerprint,
                StringComparison.Ordinal
            )
        )
        {
            return Reject("environment-light.network.config-mismatch");
        }
        if (
            !string.Equals(
                message.GameVersion,
                context.ExpectedGameVersion,
                StringComparison.Ordinal
            )
            || !string.Equals(
                message.ModVersion,
                context.ExpectedModVersion,
                StringComparison.Ordinal
            )
        )
        {
            return Reject("environment-light.network.version-mismatch");
        }
        if (
            !string.Equals(
                message.EvaluatorRevision,
                EnvironmentLightProductionContract.EvaluatorRevision,
                StringComparison.Ordinal
            )
            || !string.Equals(
                message.RuleRevision,
                thresholds.RuleRevision,
                StringComparison.Ordinal
            )
        )
        {
            return Reject("environment-light.network.evaluator-revision-mismatch");
        }
        if (
            message.Sequence <= 0
            || message.Sequence <= context.LastAcceptedSequence
        )
        {
            return Reject("environment-light.network.sequence-stale");
        }
        if (
            message.SampleTick < 0
            || message.RendererRevision <= 0
            || message.SampleUtcMilliseconds <= 0
            || context.HostUtcMilliseconds <= 0
            || AbsoluteDifference(
                message.SampleUtcMilliseconds,
                context.HostUtcMilliseconds
            ) > EnvironmentLightMultiplayerContract.MaximumSampleClockSkewMilliseconds
        )
        {
            return Reject("environment-light.network.sample-stale");
        }
        if (
            !double.IsFinite(message.VisibilityScore)
            || message.VisibilityScore is < 0d or > 1d
            || string.IsNullOrWhiteSpace(message.Reason)
            || message.Reason.Length > EnvironmentLightMultiplayerContract.MaximumReasonLength
            || message.ExplicitNightVisionActive
        )
        {
            // The shipped provider is explicitly known-inactive. A future custom provider must
            // receive its own allowlisted revision instead of silently extending this contract.
            return Reject("environment-light.network.evidence-invalid");
        }
        if (
            !Enum.TryParse(
                message.Classification,
                ignoreCase: false,
                out EnvironmentLightLevel level
            )
            || !Enum.IsDefined(level)
            || !Enum.TryParse(
                message.EvidenceStatus,
                ignoreCase: false,
                out EnvironmentLightEvidenceStatus evidenceStatus
            )
            || !Enum.IsDefined(evidenceStatus)
            || evidenceStatus != EnvironmentLightEvidenceStatus.Confirmed
        )
        {
            return Reject("environment-light.network.classification-invalid");
        }

        var score = message.VisibilityScore;
        var levelInvariant = level switch
        {
            EnvironmentLightLevel.PitchBlack => score < thresholds.PitchBlackExit,
            EnvironmentLightLevel.Lit => score > thresholds.LitExit,
            EnvironmentLightLevel.Dim =>
                score > thresholds.PitchBlackEnter
                && score < thresholds.LitEnter,
            _ => false,
        };
        if (!levelInvariant)
            return Reject("environment-light.network.classification-invariant-failed");

        var expectedAuthorization =
            context.ExpectedDarknessAttackLocationAuthorized
            && level == EnvironmentLightLevel.PitchBlack
            && EnvironmentLightVisibilityMath.CanAuthorizePitchBlack(
                score,
                thresholds
            );
        if (message.PitchBlackAuthorized != expectedAuthorization)
            return Reject("environment-light.network.authorization-invariant-failed");

        var expectedReason = level switch
        {
            EnvironmentLightLevel.Lit =>
                EnvironmentLightReasonIds.FinalVisibilityLitConfirmed,
            EnvironmentLightLevel.Dim =>
                EnvironmentLightReasonIds.FinalVisibilityDimConfirmed,
            EnvironmentLightLevel.PitchBlack when expectedAuthorization =>
                EnvironmentLightReasonIds.FinalVisibilityPitchBlackConfirmed,
            _ => EnvironmentLightReasonIds.FinalVisibilityPitchBlackAuthorizationDenied,
        };
        if (!string.Equals(message.Reason, expectedReason, StringComparison.Ordinal))
            return Reject("environment-light.network.reason-invariant-failed");

        return new EnvironmentLightEvidenceValidationResult(
            true,
            "environment-light.network.evidence-accepted",
            level,
            evidenceStatus
        );
    }

    private static long AbsoluteDifference(long left, long right)
    {
        if (left >= right)
            return left - right;
        return right - left;
    }

    private static EnvironmentLightEvidenceValidationResult Reject(string reason)
    {
        return EnvironmentLightEvidenceValidationResult.Rejected(reason);
    }
}

internal enum EnvironmentLightPresentationKind
{
    None,
    EnteredDarkness,
    Warning,
    EscapedDarkness,
    Resolved,
    Clear,
}

internal sealed class EnvironmentLightPresentationMessage
{
    public int ProtocolVersion { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string PlayerKey { get; set; } = string.Empty;
    public int ScreenId { get; set; }
    public long Sequence { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string WarningAction { get; set; } = string.Empty;
    public string WarningRequestId { get; set; } = string.Empty;
    public long ObservationRevision { get; set; }
    public string Reason { get; set; } = string.Empty;
}

internal interface IEnvironmentLightRemoteEvidenceSource
{
    event Action<DarknessAttackOwnerKey, string>? EvidenceInvalidated;

    void VisitFresh(Action<EnvironmentLightAcceptedRemoteEvidence> visitor);
}

internal interface IEnvironmentLightRemotePresentationSink
{
    void Publish(
        DarknessAttackOwnerKey key,
        EnvironmentLightPresentationKind kind,
        DarknessWarningClaimAction warningAction,
        string warningRequestId,
        long observationRevision,
        string reason
    );
}
