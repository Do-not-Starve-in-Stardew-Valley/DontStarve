#nullable enable

using System;
using System.Collections.Generic;
using DontStarve.Resource.Sanity;

namespace DontStarve.Player.Stats.Sanity.Illusions.Projection;

/// <summary>
/// The two 50% shadow appearances are the only harmless projection species allowed to consume the
/// task-family 02 shared owner permit. Ordinary projection policies intentionally cannot implement
/// this contract or carry a permit.
/// </summary>
internal sealed class ShadowCreatureHarmlessProjectionPolicy
    : IHarmlessProjectionPlacementPolicy
{
    internal ShadowCreatureHarmlessProjectionPolicy(
        string speciesId,
        string visualProfileId,
        string idleVisualSlotId,
        string textureSlotId,
        int frameCount,
        int frameDurationMilliseconds,
        SanityResourcePoint expectedPivotSourcePx,
        double expectedDrawScale
    )
    {
        SpeciesId = RequireId(speciesId, nameof(speciesId));
        VisualProfileId = RequireId(visualProfileId, nameof(visualProfileId));
        IdleVisualSlotId = RequireId(idleVisualSlotId, nameof(idleVisualSlotId));
        TextureSlotId = RequireId(textureSlotId, nameof(textureSlotId));
        if (frameCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        if (frameDurationMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameDurationMilliseconds));
        if (expectedDrawScale <= 0d || !double.IsFinite(expectedDrawScale))
            throw new ArgumentOutOfRangeException(nameof(expectedDrawScale));

        FrameCount = frameCount;
        FrameDurationMilliseconds = frameDurationMilliseconds;
        ExpectedPivotSourcePx = expectedPivotSourcePx;
        ExpectedDrawScale = expectedDrawScale;
    }

    internal string SpeciesId { get; }

    internal string VisualProfileId { get; }

    internal string IdleVisualSlotId { get; }

    internal string TextureSlotId { get; }

    internal int FrameCount { get; }

    internal int FrameDurationMilliseconds { get; }

    internal SanityResourcePoint ExpectedPivotSourcePx { get; }

    internal double ExpectedDrawScale { get; }

    public int MinimumDistanceTiles => 5;

    public int MaximumDistanceTiles => 15;

    public int CandidateAttemptLimit => HarmlessProjectionPolicy.MaximumCandidateAttempts;

    public HarmlessProjectionPlacementKind PlacementKind =>
        HarmlessProjectionPlacementKind.Ground;

    internal bool TryValidateResource(
        SanitySlotResourceResult? resource,
        out string reason
    )
    {
        if (resource is null)
        {
            reason = "shadow-projection.visual-resource-result-null";
            return false;
        }
        if (!resource.Success)
        {
            reason = resource.Diagnostic.Code;
            return false;
        }

        var preview = resource.VisualPreview;
        if (
            resource.PhysicalResource?.Kind != SanityPhysicalResourceKind.Texture
            || preview is null
            || preview.Kind != SanityVisualPreviewKind.AnimationFrame
            || preview.FrameIndex != 0
            || preview.FrameCount != FrameCount
            || preview.SourceRectangle.Width <= 0
            || preview.SourceRectangle.Height <= 0
            || preview.PivotSourcePx != ExpectedPivotSourcePx
            || Math.Abs(preview.DrawScale - ExpectedDrawScale) > 0.0001d
            || !string.Equals(
                preview.RequestedSlotId,
                IdleVisualSlotId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                preview.TextureSlotId,
                TextureSlotId,
                StringComparison.Ordinal
            )
        )
        {
            reason = "shadow-projection.visual-contract-invalid";
            return false;
        }

        // These sheets are also future hostile appearance candidates, so their frozen metadata is
        // not marked owner-local-only. Locality comes from this mod-private index/render path; none
        // of the profile's gameplay-oriented metadata is consumed here.
        reason = "shadow-projection.visual-contract-valid";
        return true;
    }

    private static string RequireId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A stable ID is required.", parameterName);
        return value;
    }
}

internal static class ShadowCreatureHarmlessProjectionCatalog
{
    internal const string CreeperFearSpeciesId =
        "sanity.projection.creeper-fear";
    internal const string TerrorbeakSpeciesId = "sanity.projection.terrorbeak";

    internal const string CreeperFearProfileId =
        "sanity.animation.creeper-fear.profile";
    internal const string CreeperFearIdleVisualSlotId =
        "sanity.animation.creeper-fear.idle";
    internal const string CreeperFearTextureSlotId =
        "sanity.asset.creeper-fear.sprite";

    internal const string TerrorbeakProfileId =
        "sanity.animation.terrorbeak.profile";
    internal const string TerrorbeakIdleVisualSlotId =
        "sanity.animation.terrorbeak.idle";
    internal const string TerrorbeakTextureSlotId =
        "sanity.asset.terrorbeak.sprite";

    internal const int OwnerProximityPixels =
        HarmlessProjectionSpawnPointSelector.TileSize;

    private static readonly IReadOnlyList<ShadowCreatureHarmlessProjectionPolicy>
        FrozenPolicies = Array.AsReadOnly(
            new[]
            {
                new ShadowCreatureHarmlessProjectionPolicy(
                    CreeperFearSpeciesId,
                    CreeperFearProfileId,
                    CreeperFearIdleVisualSlotId,
                    CreeperFearTextureSlotId,
                    frameCount: 4,
                    frameDurationMilliseconds: 390,
                    new SanityResourcePoint(32, 48),
                    expectedDrawScale: 4d
                ),
                new ShadowCreatureHarmlessProjectionPolicy(
                    TerrorbeakSpeciesId,
                    TerrorbeakProfileId,
                    TerrorbeakIdleVisualSlotId,
                    TerrorbeakTextureSlotId,
                    frameCount: 4,
                    frameDurationMilliseconds: 100,
                    new SanityResourcePoint(24, 48),
                    expectedDrawScale: 4d
                ),
            }
        );

    internal static IReadOnlyList<ShadowCreatureHarmlessProjectionPolicy> Policies =>
        FrozenPolicies;

    internal static bool IsPermitConsumer(string? speciesId)
    {
        return string.Equals(
                speciesId,
                CreeperFearSpeciesId,
                StringComparison.Ordinal
            )
            || string.Equals(
                speciesId,
                TerrorbeakSpeciesId,
                StringComparison.Ordinal
            );
    }
}

internal static class ShadowCreatureProjectionPermitGate
{
    internal static bool TryAuthorize(
        string speciesId,
        string playerKey,
        long gameMinute,
        int occupancy,
        SanityShadowSpawnPermit permit,
        out string reason
    )
    {
        if (!ShadowCreatureHarmlessProjectionCatalog.IsPermitConsumer(speciesId))
        {
            reason = "shadow-permit.species-not-authorized";
            return false;
        }
        if (
            !SanityPlayerKey.IsCanonical(playerKey)
            || !string.Equals(permit.PlayerKey, playerKey, StringComparison.Ordinal)
        )
        {
            reason = "shadow-permit.owner-mismatch";
            return false;
        }
        if (permit.PoolTier != SanityShadowPoolTier.Harmless50)
        {
            reason = "shadow-permit.pool-not-harmless";
            return false;
        }
        if (gameMinute < 0 || permit.IssuedAtMinute != gameMinute)
        {
            reason = "shadow-permit.minute-mismatch";
            return false;
        }
        if (
            occupancy < 0
            || permit.Occupancy != occupancy
            || permit.Cap <= occupancy
            || permit.NextDueMinute <= permit.IssuedAtMinute
        )
        {
            reason = "shadow-permit.occupancy-invalid";
            return false;
        }

        reason = "shadow-permit.authorized";
        return true;
    }
}

internal sealed class ShadowCreatureHarmlessProjectionInstance
{
    private long frameElapsedMilliseconds;

    internal ShadowCreatureHarmlessProjectionInstance(
        string correlationId,
        HarmlessProjectionOwnerContext owner,
        ShadowCreatureHarmlessProjectionPolicy policy,
        HarmlessProjectionWorldPoint spawnWorldPixel,
        long spawnedAtMinute,
        SanitySlotResourceResult? visualResource = null
    )
    {
        CorrelationId = RequireId(correlationId, nameof(correlationId));
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        if (!spawnWorldPixel.IsFinite)
            throw new ArgumentOutOfRangeException(nameof(spawnWorldPixel));
        if (spawnedAtMinute < 0)
            throw new ArgumentOutOfRangeException(nameof(spawnedAtMinute));

        SpawnWorldPixel = spawnWorldPixel;
        SpawnedAtMinute = spawnedAtMinute;
        VisualResource = visualResource;
    }

    internal string CorrelationId { get; }

    internal HarmlessProjectionOwnerContext Owner { get; }

    internal ShadowCreatureHarmlessProjectionPolicy Policy { get; }

    internal string SpeciesId => Policy.SpeciesId;

    internal HarmlessProjectionWorldPoint SpawnWorldPixel { get; }

    internal long SpawnedAtMinute { get; }

    internal SanitySlotResourceResult? VisualResource { get; }

    internal int CurrentFrameIndex { get; private set; }

    internal HarmlessProjectionCleanupReason? CleanupReason { get; private set; }

    internal string CleanupReasonId => CleanupReason.HasValue
        ? HarmlessProjectionCleanupReasonIds.GetId(CleanupReason.Value)
        : string.Empty;

    internal bool IsCleanedUp => CleanupReason.HasValue;

    internal bool AdvanceFrame(int elapsedMilliseconds)
    {
        if (elapsedMilliseconds < 0)
            return false;
        if (IsCleanedUp)
            return false;

        var cycleMilliseconds = checked(
            (long)Policy.FrameCount * Policy.FrameDurationMilliseconds
        );
        var elapsed = Math.Min(
            long.MaxValue - frameElapsedMilliseconds,
            (long)elapsedMilliseconds
        );
        frameElapsedMilliseconds = (frameElapsedMilliseconds + elapsed)
            % cycleMilliseconds;
        CurrentFrameIndex = (int)(
            frameElapsedMilliseconds / Policy.FrameDurationMilliseconds
        );
        return true;
    }

    internal bool TryMarkCleaned(HarmlessProjectionCleanupReason reason)
    {
        if (CleanupReason.HasValue)
            return false;
        CleanupReason = reason;
        return true;
    }

    private static string RequireId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A stable ID is required.", parameterName);
        return value;
    }
}

internal sealed class ShadowCreatureHarmlessProjectionSpawnRequest
{
    internal ShadowCreatureHarmlessProjectionSpawnRequest(
        string correlationId,
        HarmlessProjectionOwnerContext owner,
        ShadowCreatureHarmlessProjectionPolicy policy,
        HarmlessProjectionWorldPoint ownerStandingWorldPixel,
        long gameMinute,
        SanityShadowSpawnPermit permit
    )
    {
        if (string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("A correlation ID is required.", nameof(correlationId));
        CorrelationId = correlationId;
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        if (!ownerStandingWorldPixel.IsFinite)
            throw new ArgumentOutOfRangeException(nameof(ownerStandingWorldPixel));

        OwnerStandingWorldPixel = ownerStandingWorldPixel;
        GameMinute = gameMinute;
        Permit = permit;
    }

    internal string CorrelationId { get; }

    internal HarmlessProjectionOwnerContext Owner { get; }

    internal ShadowCreatureHarmlessProjectionPolicy Policy { get; }

    internal HarmlessProjectionWorldPoint OwnerStandingWorldPixel { get; }

    internal long GameMinute { get; }

    internal SanityShadowSpawnPermit Permit { get; }
}

internal sealed class ShadowCreatureHarmlessProjectionSpawnResult
{
    private ShadowCreatureHarmlessProjectionSpawnResult(
        bool success,
        string reason,
        ShadowCreatureHarmlessProjectionInstance? instance
    )
    {
        Success = success;
        Reason = string.IsNullOrWhiteSpace(reason)
            ? throw new ArgumentException("A stable result reason is required.", nameof(reason))
            : reason;
        Instance = instance;
    }

    internal bool Success { get; }

    internal string Reason { get; }

    internal ShadowCreatureHarmlessProjectionInstance? Instance { get; }

    internal static ShadowCreatureHarmlessProjectionSpawnResult Spawned(
        ShadowCreatureHarmlessProjectionInstance instance
    )
    {
        ArgumentNullException.ThrowIfNull(instance);
        return new ShadowCreatureHarmlessProjectionSpawnResult(
            true,
            "shadow-projection.spawned",
            instance
        );
    }

    internal static ShadowCreatureHarmlessProjectionSpawnResult Failed(
        string reason
    )
    {
        return new ShadowCreatureHarmlessProjectionSpawnResult(false, reason, null);
    }
}

internal interface IShadowCreatureHarmlessProjectionSpawnFactory
{
    ShadowCreatureHarmlessProjectionSpawnResult TrySpawn(
        ShadowCreatureHarmlessProjectionSpawnRequest request
    );
}

internal interface IShadowCreatureProjectionBudgetAuthority
{
    SanityShadowBudgetEvaluationResult EvaluateShadowBudget(
        string playerKey,
        long gameMinute,
        int occupancy
    );
}

internal interface IShadowProjectionCorrelationSource
{
    string Next(string playerKey, string speciesId);
}

internal sealed class SessionShadowProjectionCorrelationSource
    : IShadowProjectionCorrelationSource
{
    private readonly string sessionNonce = Guid.NewGuid().ToString("N");
    private long nextSequence;

    public string Next(string playerKey, string speciesId)
    {
        if (!SanityPlayerKey.IsCanonical(playerKey))
            throw new ArgumentException("A canonical player key is required.", nameof(playerKey));
        if (!ShadowCreatureHarmlessProjectionCatalog.IsPermitConsumer(speciesId))
            throw new ArgumentException("An authorized shadow species is required.", nameof(speciesId));

        nextSequence = checked(nextSequence + 1);
        return string.Concat(
            "sanity.shadow-conversion.",
            sessionNonce,
            ".",
            nextSequence.ToString("D8", System.Globalization.CultureInfo.InvariantCulture)
        );
    }
}

internal sealed class ShadowProjectionConversionIntent
{
    internal const string ResponsibilityId = "future-host-authority";

    internal ShadowProjectionConversionIntent(
        string correlationId,
        string playerKey,
        string speciesId,
        long requestedAtMinute
    )
    {
        if (string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("A correlation ID is required.", nameof(correlationId));
        if (!SanityPlayerKey.IsCanonical(playerKey))
            throw new ArgumentException("A canonical player key is required.", nameof(playerKey));
        if (!ShadowCreatureHarmlessProjectionCatalog.IsPermitConsumer(speciesId))
            throw new ArgumentException("An authorized shadow species is required.", nameof(speciesId));

        CorrelationId = correlationId;
        PlayerKey = playerKey;
        SpeciesId = speciesId;
        RequestedAtMinute = requestedAtMinute;
    }

    internal string CorrelationId { get; }

    internal string PlayerKey { get; }

    internal string SpeciesId { get; }

    internal long RequestedAtMinute { get; }

    internal string AuthorityResponsibility => ResponsibilityId;

    /// <summary>
    /// A future reverse transition must create a new legal local candidate after a fresh budget
    /// decision. The removed visual instance is evidence only and can never be restored.
    /// </summary>
    internal bool RequiresFreshBudgetAndLegalSpawn => true;

    internal bool RestoresRemovedLocalInstance => false;
}

internal enum ShadowProjectionConversionSubmissionStatus
{
    Confirmed,
    Unconfirmed,
    Delayed,
    Rejected,
    Failed,
}

internal readonly record struct ShadowProjectionConversionSubmissionResult(
    ShadowProjectionConversionSubmissionStatus Status,
    string Reason
);

/// <summary>
/// This pure responsibility seam is not a multiplayer message, lease, receipt, or confirmation
/// signature. Task family 07 may later bridge it only after freezing the real host protocol.
/// </summary>
internal interface IShadowProjectionConversionIntentSink
{
    ShadowProjectionConversionSubmissionResult Record(
        ShadowProjectionConversionIntent intent
    );
}

internal sealed class UnavailableShadowProjectionConversionIntentSink
    : IShadowProjectionConversionIntentSink
{
    public ShadowProjectionConversionSubmissionResult Record(
        ShadowProjectionConversionIntent intent
    )
    {
        ArgumentNullException.ThrowIfNull(intent);
        return new ShadowProjectionConversionSubmissionResult(
            ShadowProjectionConversionSubmissionStatus.Unconfirmed,
            "shadow-conversion.host-contract-not-frozen"
        );
    }
}

internal sealed class ShadowProjectionConversionEvidence
{
    internal ShadowProjectionConversionEvidence(
        ShadowProjectionConversionIntent intent,
        ShadowProjectionConversionSubmissionResult submission
    )
    {
        Intent = intent ?? throw new ArgumentNullException(nameof(intent));
        if (string.IsNullOrWhiteSpace(submission.Reason))
        {
            submission = new ShadowProjectionConversionSubmissionResult(
                ShadowProjectionConversionSubmissionStatus.Failed,
                "shadow-conversion.result-reason-missing"
            );
        }
        Submission = submission;
    }

    internal ShadowProjectionConversionIntent Intent { get; }

    internal ShadowProjectionConversionSubmissionResult Submission { get; }

    internal string LocalCleanupReasonId =>
        HarmlessProjectionCleanupReasonIds.ConversionRequested;
}
