#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

/// <summary>
/// A small value type shared by the pure PushBox solver for positions and movement vectors.
/// It deliberately has no game-entity meaning.
/// </summary>
internal readonly record struct HostileShadowPushBoxPoint(double X, double Y)
{
    internal bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
}

/// <summary>
/// Immutable input for one object that is allowed to take part in the shadow-creature PushBox
/// solve. The caller supplies the current world box and the normal target position; this class
/// does not discover entities, query a world, or mutate the supplied object.
/// </summary>
internal sealed class HostileShadowCrowdParticipant
{
    internal HostileShadowCrowdParticipant(
        string? stableId,
        string? locationId,
        string? groupId,
        HostileShadowPushBoxPoint currentPosition,
        HostileShadowPushBoxPoint normalTargetPosition,
        HostileShadowPushBoxPoint movementVector,
        HostileShadowPushBoxWorldRectangle pushBox,
        double pushForce,
        bool isActive = true,
        bool isBinding = false,
        bool isBindingRepresentative = false
    )
    {
        StableId = stableId ?? string.Empty;
        LocationId = locationId ?? string.Empty;
        GroupId = groupId ?? string.Empty;
        CurrentPosition = currentPosition;
        NormalTargetPosition = normalTargetPosition;
        MovementVector = movementVector;
        PushBox = pushBox;
        PushForce = pushForce;
        IsActive = isActive;
        IsBinding = isBinding;
        IsBindingRepresentative = isBindingRepresentative;
    }

    internal string StableId { get; }

    internal string LocationId { get; private set; }

    internal string GroupId { get; private set; }

    internal HostileShadowPushBoxPoint CurrentPosition { get; private set; }

    internal HostileShadowPushBoxPoint NormalTargetPosition { get; private set; }

    internal HostileShadowPushBoxPoint MovementVector { get; private set; }

    internal HostileShadowPushBoxWorldRectangle PushBox { get; private set; }

    internal double PushForce { get; private set; }

    internal bool IsActive { get; private set; }

    internal bool IsBinding { get; private set; }

    internal bool IsBindingRepresentative { get; private set; }

    /// <summary>
    /// Refreshes the per-tick geometry and movement intent without allocating a second participant
    /// object. StableId remains immutable so resolver ordering stays deterministic.
    /// </summary>
    internal void Update(
        HostileShadowPushBoxPoint currentPosition,
        HostileShadowPushBoxPoint normalTargetPosition,
        HostileShadowPushBoxPoint movementVector,
        HostileShadowPushBoxWorldRectangle pushBox,
        bool isActive,
        bool isBinding,
        bool isBindingRepresentative
    )
    {
        CurrentPosition = currentPosition;
        NormalTargetPosition = normalTargetPosition;
        MovementVector = movementVector;
        PushBox = pushBox;
        IsActive = isActive;
        IsBinding = isBinding;
        IsBindingRepresentative = isBindingRepresentative;
    }

    internal bool IsBindingProjection => IsBinding && !IsBindingRepresentative;

    internal bool IsEligible =>
        IsActive && (!IsBinding || IsBindingRepresentative);

    internal bool IsValid =>
        !string.IsNullOrWhiteSpace(StableId)
        && !string.IsNullOrWhiteSpace(LocationId)
        && !string.IsNullOrWhiteSpace(GroupId)
        && CurrentPosition.IsFinite
        && NormalTargetPosition.IsFinite
        && MovementVector.IsFinite
        && PushBox.IsValid
        && double.IsFinite(PushForce)
        && PushForce > 0d
        && (!IsBindingRepresentative || IsBinding);
}

/// <summary>
/// One stable-id keyed result. NormalTargetPosition is the position calculated by the caller's
/// ordinary movement logic; FinalPosition is the only value this stage is allowed to change.
/// </summary>
internal readonly record struct HostileShadowCrowdCollisionResolutionEntry(
    string StableId,
    HostileShadowPushBoxPoint NormalTargetPosition,
    HostileShadowPushBoxPoint FinalPosition,
    HostileShadowPushBoxPoint AppliedCorrection
)
{
    internal bool IsFinite =>
        !string.IsNullOrWhiteSpace(StableId)
        && NormalTargetPosition.IsFinite
        && FinalPosition.IsFinite
        && AppliedCorrection.IsFinite;
}

/// <summary>
/// Diagnostics and final positions from one deterministic pure-logic solve.
/// </summary>
internal sealed class HostileShadowCrowdCollisionResolution
{
    internal HostileShadowCrowdCollisionResolution(
        IReadOnlyList<HostileShadowCrowdCollisionResolutionEntry> entries,
        int inputParticipantCount,
        int eligibleParticipantCount,
        int skippedParticipantCount,
        int candidatePairCount,
        int resolvedPairCount,
        int pairFailureCount,
        int iterationsExecuted,
        int residualOverlappingPairCount,
        string reason
    )
    {
        Entries = entries;
        InputParticipantCount = inputParticipantCount;
        EligibleParticipantCount = eligibleParticipantCount;
        SkippedParticipantCount = skippedParticipantCount;
        CandidatePairCount = candidatePairCount;
        ResolvedPairCount = resolvedPairCount;
        PairFailureCount = pairFailureCount;
        IterationsExecuted = iterationsExecuted;
        ResidualOverlappingPairCount = residualOverlappingPairCount;
        Reason = reason;
    }

    internal IReadOnlyList<HostileShadowCrowdCollisionResolutionEntry> Entries { get; }

    internal int InputParticipantCount { get; }

    internal int EligibleParticipantCount { get; }

    internal int SkippedParticipantCount { get; }

    /// <summary>
    /// Number of unique same-location/same-group broad-phase pairs seen across all iterations.
    /// It is not a count of network messages or movement actions.
    /// </summary>
    internal int CandidatePairCount { get; }

    internal int ResolvedPairCount { get; }

    internal int PairFailureCount { get; }

    internal int IterationsExecuted { get; }

    internal int ResidualOverlappingPairCount { get; }

    internal string Reason { get; }

    internal bool IsResolved =>
        ResidualOverlappingPairCount == 0 && PairFailureCount == 0;
}

/// <summary>
/// Deterministic, world-only soft PushBox resolver. It consumes normal target positions and
/// returns corrected positions; it never changes speed, AI state, or any external object.
/// Each candidate pair contributes at most one bounded reaction during a solve. The next fixed
/// update observes the remaining overlap again, so collision response cannot become a one-tick
/// teleport to strict separation.
/// </summary>
internal sealed class HostileShadowCrowdCollisionResolver
{
    // One bounded reaction pass is the fixed-update contract; higher budgets remain available
    // only for explicit callers that intentionally request additional relaxation passes.
    internal const int DefaultMaximumIterations = 1;
    internal const int MaximumSupportedIterations = 32;
    internal const int MaximumCellsPerParticipant = 128;
    internal const long MaximumCellCoordinate = 1_000_000_000L;
    internal const double SpatialCellSize = 64d;

    private const double AxisEpsilon = 1e-9d;
    private const double PositionEpsilon = 1e-7d;
    // Keep the resolver baseline neutral. Species-specific tuning belongs to the shared
    // PushForce resource value, so a future species is not implicitly multiplied by a global
    // balance adjustment.
    internal const double ReactionDisplacementScale = 1d;
    private const double EqualReactionShare = 0.5d;
    // Keep every positive overlap active, but make the response a smooth function of depth. The
    // fraction starts below one and has no immunity band or hard depth threshold.
    private const double MinimumReactionFraction = 0.20d;
    private const double DepthReactionGain = 0.60d;

    private readonly int maximumIterations;
    private readonly HostileShadowPushBoxCandidateCache candidateCache = new();
    private readonly List<WorkingParticipant> workingParticipants = new();
    private readonly List<WorkingParticipant> workingPool = new();
    private readonly HashSet<long> observedCandidatePairKeys = new();

    internal HostileShadowCrowdCollisionResolver(
        int maximumIterations = DefaultMaximumIterations
    )
    {
        this.maximumIterations = Math.Clamp(
            maximumIterations,
            1,
            MaximumSupportedIterations
        );
    }

    internal HostileShadowCrowdCollisionResolution Resolve(
        IReadOnlyList<HostileShadowCrowdParticipant>? participants
    )
    {
        ResetWorkingParticipants();
        observedCandidatePairKeys.Clear();
        if (participants is null)
        {
            return new HostileShadowCrowdCollisionResolution(
                Array.Empty<HostileShadowCrowdCollisionResolutionEntry>(),
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                "shadow-push-box.resolve.input-null"
            );
        }

        try
        {
            var inputCount = participants.Count;
            var skippedCount = 0;

            for (var index = 0; index < inputCount; index++)
            {
                var participant = participants[index];
                if (participant is null || !participant.IsValid || !participant.IsEligible)
                {
                    skippedCount++;
                    continue;
                }

                var working = RentWorkingParticipant();
                if (!working.TryLoad(participant))
                {
                    ReturnWorkingParticipant(working);
                    skippedCount++;
                    continue;
                }

                workingParticipants.Add(working);
            }

            SortWorkingParticipantsAndRemoveDuplicateIds(ref skippedCount);

            var eligibleCount = workingParticipants.Count;
            if (eligibleCount == 0)
            {
                return new HostileShadowCrowdCollisionResolution(
                    Array.Empty<HostileShadowCrowdCollisionResolutionEntry>(),
                    inputCount,
                    0,
                    skippedCount,
                    0,
                    0,
                    0,
                    0,
                    0,
                    skippedCount > 0
                        ? "shadow-push-box.resolve.no-eligible-participants"
                        : "shadow-push-box.resolve.empty"
                );
            }

            var candidatePairCount = 0;
            var resolvedPairCount = 0;
            var pairFailureCount = 0;
            var iterationsExecuted = 0;

            // This is intentionally one collision pass per fixed update. The broad phase still
            // handles every bounded candidate pair, including chains, while the next update
            // supplies the next reaction impulse from the remaining overlap. Keep the existing
            // bounded iteration constructor contract, even though the default soft reaction never
            // needs repeated same-pair settlement within one fixed update.
            var collisionPasses = maximumIterations;
            for (var iteration = 0; iteration < collisionPasses; iteration++)
            {
                candidateCache.Build(workingParticipants);
                candidatePairCount = checked(
                    candidatePairCount + CountNewCandidatePairs()
                );
                if (candidateCache.CandidatePairs.Count == 0)
                    break;

                foreach (var pair in candidateCache.CandidatePairs)
                {
                    var first = workingParticipants[pair.FirstIndex];
                    var second = workingParticipants[pair.SecondIndex];
                    if (
                        !first.Box.TryGetIntersectionDepth(
                            second.Box,
                            out var overlapX,
                            out var overlapY
                        )
                    )
                    {
                        continue;
                    }

                    if (
                        !TryCreatePairAdjustment(
                            first,
                            second,
                            overlapX,
                            overlapY,
                            out var firstDelta,
                            out var secondDelta
                        )
                    )
                    {
                        pairFailureCount++;
                        continue;
                    }

                    if (!TryApplyPair(first, second, firstDelta, secondDelta))
                    {
                        pairFailureCount++;
                        continue;
                    }
                    resolvedPairCount++;
                }

                iterationsExecuted++;
            }

            candidateCache.Build(workingParticipants);
            candidatePairCount = checked(
                candidatePairCount + CountNewCandidatePairs()
            );
            var residualOverlappingPairCount = candidateCache.CountOverlappingPairs(
                workingParticipants
            );

            var entries = new HostileShadowCrowdCollisionResolutionEntry[
                workingParticipants.Count
            ];
            for (var index = 0; index < workingParticipants.Count; index++)
                entries[index] = workingParticipants[index].CreateResult();

            var reason = residualOverlappingPairCount > 0
                ? "shadow-push-box.resolve.reaction-applied"
                : pairFailureCount > 0
                    ? "shadow-push-box.resolve.pair-failed"
                    : skippedCount > 0
                        ? "shadow-push-box.resolve.partial-input"
                        : "shadow-push-box.resolve.resolved";

            return new HostileShadowCrowdCollisionResolution(
                entries,
                inputCount,
                eligibleCount,
                skippedCount,
                candidatePairCount,
                resolvedPairCount,
                pairFailureCount,
                iterationsExecuted,
                residualOverlappingPairCount,
                reason
            );
        }
        catch (Exception)
        {
            ResetWorkingParticipants();
            return new HostileShadowCrowdCollisionResolution(
                Array.Empty<HostileShadowCrowdCollisionResolutionEntry>(),
                participants.Count,
                0,
                0,
                0,
                0,
                1,
                0,
                0,
                "shadow-push-box.resolve.exception"
            );
        }
    }

    private void ResetWorkingParticipants()
    {
        for (var index = workingParticipants.Count - 1; index >= 0; index--)
            workingPool.Add(workingParticipants[index]);
        workingParticipants.Clear();
    }

    private WorkingParticipant RentWorkingParticipant()
    {
        if (workingPool.Count == 0)
            return new WorkingParticipant();

        var index = workingPool.Count - 1;
        var participant = workingPool[index];
        workingPool.RemoveAt(index);
        return participant;
    }

    private void ReturnWorkingParticipant(WorkingParticipant participant)
    {
        participant.Clear();
        workingPool.Add(participant);
    }

    private void SortWorkingParticipantsAndRemoveDuplicateIds(ref int skippedCount)
    {
        workingParticipants.Sort(static (first, second) =>
            string.CompareOrdinal(first.StableId, second.StableId)
        );

        var start = 0;
        while (start < workingParticipants.Count)
        {
            var end = start + 1;
            while (
                end < workingParticipants.Count
                && string.Equals(
                    workingParticipants[start].StableId,
                    workingParticipants[end].StableId,
                    StringComparison.Ordinal
                )
            )
            {
                end++;
            }

            if (end - start == 1)
            {
                start = end;
                continue;
            }

            for (var index = end - 1; index >= start; index--)
            {
                var duplicate = workingParticipants[index];
                workingParticipants.RemoveAt(index);
                ReturnWorkingParticipant(duplicate);
                skippedCount++;
            }
        }
    }

    private int CountNewCandidatePairs()
    {
        var count = 0;
        foreach (var pair in candidateCache.CandidatePairs)
        {
            var key = ((long)pair.FirstIndex << 32) | (uint)pair.SecondIndex;
            if (observedCandidatePairKeys.Add(key))
                count++;
        }
        return count;
    }

    private static bool TryApplyPair(
        WorkingParticipant first,
        WorkingParticipant second,
        HostileShadowPushBoxPoint firstDelta,
        HostileShadowPushBoxPoint secondDelta
    )
    {
        var firstBox = first.Box;
        var firstOffsetX = first.OffsetX;
        var firstOffsetY = first.OffsetY;
        var secondBox = second.Box;
        var secondOffsetX = second.OffsetX;
        var secondOffsetY = second.OffsetY;

        if (!first.TryApply(firstDelta.X, firstDelta.Y))
            return false;
        if (!second.TryApply(secondDelta.X, secondDelta.Y))
        {
            first.Restore(firstBox, firstOffsetX, firstOffsetY);
            second.Restore(secondBox, secondOffsetX, secondOffsetY);
            return false;
        }

        return true;
    }

    private static bool TryCreatePairAdjustment(
        WorkingParticipant first,
        WorkingParticipant second,
        double overlapX,
        double overlapY,
        out HostileShadowPushBoxPoint firstDelta,
        out HostileShadowPushBoxPoint secondDelta
    )
    {
        firstDelta = default;
        secondDelta = default;
        if (
            !double.IsFinite(overlapX)
            || !double.IsFinite(overlapY)
            || overlapX <= 0d
            || overlapY <= 0d
        )
        {
            return false;
        }

        var axis = SelectSeparationAxis(first, second, overlapX, overlapY);
        var axisOverlap = axis.IsHorizontal ? overlapX : overlapY;
        var smallestExtent = axis.IsHorizontal
            ? Math.Min(first.Box.Width, second.Box.Width)
            : Math.Min(first.Box.Height, second.Box.Height);
        var reactionForce = CalculatePairReactionForce(
            first,
            second,
            axis.NormalX,
            axis.NormalY
        );
        if (!double.IsFinite(reactionForce) || reactionForce <= 0d)
            return false;
        var correction = CalculateReactionDisplacement(
            axisOverlap,
            smallestExtent,
            reactionForce
        );
        if (!double.IsFinite(correction) || correction <= 0d)
            return false;

        // The current collision policy is symmetric: the two participants receive equal,
        // opposite position corrections. PushForce scales the pair's total reaction magnitude;
        // it must not bias the bilateral split or change either participant's base movement speed.
        var firstDistance = correction * EqualReactionShare;
        var secondDistance = correction * EqualReactionShare;
        if (!double.IsFinite(firstDistance) || !double.IsFinite(secondDistance))
            return false;

        firstDelta = new HostileShadowPushBoxPoint(
            -axis.NormalX * firstDistance,
            -axis.NormalY * firstDistance
        );
        secondDelta = new HostileShadowPushBoxPoint(
            axis.NormalX * secondDistance,
            axis.NormalY * secondDistance
        );
        return firstDelta.IsFinite && secondDelta.IsFinite;
    }

    private static AxisSelection SelectSeparationAxis(
        WorkingParticipant first,
        WorkingParticipant second,
        double overlapX,
        double overlapY
    )
    {
        var normalX = ResolveNormalSign(first, second, horizontal: true);
        var normalY = ResolveNormalSign(first, second, horizontal: false);

        if (overlapX < overlapY - AxisEpsilon)
            return new AxisSelection(true, normalX, 0d);
        if (overlapY < overlapX - AxisEpsilon)
            return new AxisSelection(false, 0d, normalY);

        // Equal-depth and fully coincident boxes use the stable-id ordered normal. X is the
        // final axis tie-breaker, so repeated solves cannot alternate between axes.
        return new AxisSelection(true, normalX, 0d);
    }

    private static double ResolveNormalSign(
        WorkingParticipant first,
        WorkingParticipant second,
        bool horizontal
    )
    {
        var firstCenter = horizontal
            ? first.Box.X + (first.Box.Width / 2d)
            : first.Box.Y + (first.Box.Height / 2d);
        var secondCenter = horizontal
            ? second.Box.X + (second.Box.Width / 2d)
            : second.Box.Y + (second.Box.Height / 2d);
        var centerDelta = secondCenter - firstCenter;
        if (centerDelta > AxisEpsilon)
            return 1d;
        if (centerDelta < -AxisEpsilon)
            return -1d;

        var relativeMovement = horizontal
            ? first.MovementVector.X - second.MovementVector.X
            : first.MovementVector.Y - second.MovementVector.Y;
        if (relativeMovement > AxisEpsilon)
            return 1d;
        if (relativeMovement < -AxisEpsilon)
            return -1d;

        return string.CompareOrdinal(first.StableId, second.StableId) <= 0
            ? 1d
            : -1d;
    }

    private static double CalculateReactionDisplacement(
        double overlap,
        double smallestExtent,
        double reactionForce
    )
    {
        if (
            !double.IsFinite(overlap)
            || !double.IsFinite(smallestExtent)
            || !double.IsFinite(reactionForce)
            || overlap <= 0d
            || smallestExtent <= 0d
            || reactionForce <= 0d
        )
        {
            return 0d;
        }

        if (overlap <= PositionEpsilon)
            return overlap * ReactionDisplacementScale * reactionForce;

        // overlap is at most the smaller extent, but use a saturating curve so malformed or
        // unusually large resource geometry cannot turn depth into an unbounded impulse.
        var depthRatio = overlap / smallestExtent;
        if (!double.IsFinite(depthRatio) || depthRatio <= 0d)
            return 0d;
        var depthResponse = depthRatio / (1d + depthRatio);
        var responseFraction = MinimumReactionFraction
            + (DepthReactionGain * depthResponse);
        var correction = overlap
            * responseFraction
            * ReactionDisplacementScale
            * reactionForce;
        return double.IsFinite(correction)
            ? Math.Min(overlap - PositionEpsilon, correction)
            : 0d;
    }

    private static double CalculatePairReactionForce(
        WorkingParticipant first,
        WorkingParticipant second,
        double normalX,
        double normalY
    )
    {
        var horizontal = normalX != 0d;
        var firstNormalMovement = horizontal
            ? first.MovementVector.X
            : first.MovementVector.Y;
        var secondNormalMovement = horizontal
            ? second.MovementVector.X
            : second.MovementVector.Y;
        var normal = horizontal ? normalX : normalY;
        var firstClosingMovement = Math.Max(0d, firstNormalMovement * normal);
        var secondClosingMovement = Math.Max(0d, -secondNormalMovement * normal);
        var totalClosingMovement = firstClosingMovement + secondClosingMovement;

        if (
            double.IsFinite(totalClosingMovement)
            && totalClosingMovement > AxisEpsilon
        )
        {
            var firstShare = firstClosingMovement / totalClosingMovement;
            var secondShare = secondClosingMovement / totalClosingMovement;
            var weightedForce = (first.PushForce * firstShare)
                + (second.PushForce * secondShare);
            if (double.IsFinite(weightedForce) && weightedForce > 0d)
                return weightedForce;
        }

        var restingForce = (first.PushForce + second.PushForce) * EqualReactionShare;
        return double.IsFinite(restingForce) && restingForce > 0d
            ? restingForce
            : 0d;
    }

    private readonly record struct AxisSelection(bool IsHorizontal, double NormalX, double NormalY);

    private sealed class WorkingParticipant
    {
        internal HostileShadowCrowdParticipant Source { get; private set; } = null!;

        internal string StableId => Source.StableId;

        internal HostileShadowPushBoxPoint MovementVector => Source.MovementVector;

        internal double PushForce => Source.PushForce;

        internal HostileShadowPushBoxWorldRectangle Box { get; set; }

        internal double OffsetX { get; private set; }

        internal double OffsetY { get; private set; }

        internal bool TryLoad(HostileShadowCrowdParticipant source)
        {
            var deltaX = source.NormalTargetPosition.X - source.CurrentPosition.X;
            var deltaY = source.NormalTargetPosition.Y - source.CurrentPosition.Y;
            if (
                !double.IsFinite(deltaX)
                || !double.IsFinite(deltaY)
                || !TryTranslate(source.PushBox, deltaX, deltaY, out var targetBox)
            )
            {
                return false;
            }

            Source = source;
            Box = targetBox;
            OffsetX = 0d;
            OffsetY = 0d;
            return true;
        }

        internal bool TryApply(double deltaX, double deltaY)
        {
            if (
                !double.IsFinite(deltaX)
                || !double.IsFinite(deltaY)
                || !TryTranslate(Box, deltaX, deltaY, out var nextBox)
            )
            {
                return false;
            }

            var nextOffsetX = OffsetX + deltaX;
            var nextOffsetY = OffsetY + deltaY;
            var nextPositionX = Source.NormalTargetPosition.X + nextOffsetX;
            var nextPositionY = Source.NormalTargetPosition.Y + nextOffsetY;
            if (
                !double.IsFinite(nextOffsetX)
                || !double.IsFinite(nextOffsetY)
                || !double.IsFinite(nextPositionX)
                || !double.IsFinite(nextPositionY)
            )
            {
                return false;
            }

            Box = nextBox;
            OffsetX = nextOffsetX;
            OffsetY = nextOffsetY;
            return true;
        }

        internal void Restore(
            HostileShadowPushBoxWorldRectangle box,
            double offsetX,
            double offsetY
        )
        {
            Box = box;
            OffsetX = offsetX;
            OffsetY = offsetY;
        }

        internal HostileShadowCrowdCollisionResolutionEntry CreateResult()
        {
            return new HostileShadowCrowdCollisionResolutionEntry(
                Source.StableId,
                Source.NormalTargetPosition,
                new HostileShadowPushBoxPoint(
                    Source.NormalTargetPosition.X + OffsetX,
                    Source.NormalTargetPosition.Y + OffsetY
                ),
                new HostileShadowPushBoxPoint(OffsetX, OffsetY)
            );
        }

        internal void Clear()
        {
            Source = null!;
            Box = default;
            OffsetX = 0d;
            OffsetY = 0d;
        }

        private static bool TryTranslate(
            HostileShadowPushBoxWorldRectangle box,
            double deltaX,
            double deltaY,
            out HostileShadowPushBoxWorldRectangle translated
        )
        {
            translated = default;
            if (!box.IsValid || !double.IsFinite(deltaX) || !double.IsFinite(deltaY))
                return false;

            var x = box.X + deltaX;
            var y = box.Y + deltaY;
            if (!double.IsFinite(x) || !double.IsFinite(y))
                return false;

            translated = new HostileShadowPushBoxWorldRectangle(
                x,
                y,
                box.Width,
                box.Height
            );
            return translated.IsValid;
        }
    }

    private sealed class HostileShadowPushBoxCandidateCache
    {
        private readonly Dictionary<CellKey, List<int>> buckets = new();
        private readonly List<List<int>> bucketPool = new();
        private readonly List<int> oversizedParticipants = new();
        private readonly HashSet<long> pairKeys = new();
        private readonly List<CandidatePair> candidatePairs = new();

        internal IReadOnlyList<CandidatePair> CandidatePairs => candidatePairs;

        internal void Build(IReadOnlyList<WorkingParticipant> participants)
        {
            RecycleBuckets();
            oversizedParticipants.Clear();
            pairKeys.Clear();
            candidatePairs.Clear();

            for (var index = 0; index < participants.Count; index++)
            {
                var box = participants[index].Box;
                if (!box.IsValid)
                    continue;

                if (
                    !TryGetCellBounds(
                        box,
                        out var minCellX,
                        out var maxCellX,
                        out var minCellY,
                        out var maxCellY
                    )
                )
                {
                    oversizedParticipants.Add(index);
                    continue;
                }

                for (var cellX = minCellX; cellX <= maxCellX; cellX++)
                {
                    for (var cellY = minCellY; cellY <= maxCellY; cellY++)
                    {
                        var key = new CellKey(cellX, cellY);
                        if (!buckets.TryGetValue(key, out var bucket))
                        {
                            bucket = RentBucket();
                            buckets.Add(key, bucket);
                        }
                        bucket.Add(index);
                    }
                }
            }

            foreach (var bucket in buckets.Values)
            {
                for (var first = 0; first < bucket.Count - 1; first++)
                {
                    for (var second = first + 1; second < bucket.Count; second++)
                    {
                        AddCandidate(
                            bucket[first],
                            bucket[second],
                            participants
                        );
                    }
                }
            }

            foreach (var oversizedIndex in oversizedParticipants)
            {
                for (var otherIndex = oversizedIndex + 1; otherIndex < participants.Count; otherIndex++)
                    AddCandidate(oversizedIndex, otherIndex, participants);
            }

            candidatePairs.Sort(static (first, second) =>
            {
                var firstComparison = first.FirstIndex.CompareTo(second.FirstIndex);
                return firstComparison != 0
                    ? firstComparison
                    : first.SecondIndex.CompareTo(second.SecondIndex);
            });
        }

        internal int CountOverlappingPairs(IReadOnlyList<WorkingParticipant> participants)
        {
            var count = 0;
            foreach (var pair in candidatePairs)
            {
                if (
                    participants[pair.FirstIndex].Box.TryGetIntersectionDepth(
                        participants[pair.SecondIndex].Box,
                        out _,
                        out _
                    )
                )
                {
                    count++;
                }
            }
            return count;
        }

        private void AddCandidate(
            int firstIndex,
            int secondIndex,
            IReadOnlyList<WorkingParticipant> participants
        )
        {
            if (firstIndex == secondIndex)
                return;
            if (firstIndex > secondIndex)
                (firstIndex, secondIndex) = (secondIndex, firstIndex);

            var first = participants[firstIndex];
            var second = participants[secondIndex];
            if (
                !string.Equals(first.Source.LocationId, second.Source.LocationId, StringComparison.Ordinal)
                || !string.Equals(first.Source.GroupId, second.Source.GroupId, StringComparison.Ordinal)
            )
            {
                return;
            }

            var key = ((long)firstIndex << 32) | (uint)secondIndex;
            if (pairKeys.Add(key))
                candidatePairs.Add(new CandidatePair(firstIndex, secondIndex));
        }

        private List<int> RentBucket()
        {
            if (bucketPool.Count == 0)
                return new List<int>();

            var index = bucketPool.Count - 1;
            var bucket = bucketPool[index];
            bucketPool.RemoveAt(index);
            return bucket;
        }

        private void RecycleBuckets()
        {
            foreach (var bucket in buckets.Values)
            {
                bucket.Clear();
                bucketPool.Add(bucket);
            }
            buckets.Clear();
        }

        private static bool TryGetCellBounds(
            HostileShadowPushBoxWorldRectangle box,
            out long minCellX,
            out long maxCellX,
            out long minCellY,
            out long maxCellY
        )
        {
            minCellX = 0;
            maxCellX = 0;
            minCellY = 0;
            maxCellY = 0;
            var right = box.X + box.Width;
            var bottom = box.Y + box.Height;
            if (
                !double.IsFinite(right)
                || !double.IsFinite(bottom)
                || !TryCellCoordinate(box.X, out minCellX)
                || !TryCellCoordinate(right, out maxCellX)
                || !TryCellCoordinate(box.Y, out minCellY)
                || !TryCellCoordinate(bottom, out maxCellY)
            )
            {
                return false;
            }

            if (minCellX > maxCellX || minCellY > maxCellY)
                return false;
            var width = maxCellX - minCellX + 1L;
            var height = maxCellY - minCellY + 1L;
            return width > 0L
                && height > 0L
                && width <= MaximumCellsPerParticipant
                && height <= MaximumCellsPerParticipant
                && width <= MaximumCellsPerParticipant / Math.Max(1L, height);
        }

        private static bool TryCellCoordinate(double value, out long coordinate)
        {
            coordinate = 0L;
            var cell = Math.Floor(value / SpatialCellSize);
            if (
                !double.IsFinite(cell)
                || cell < -MaximumCellCoordinate
                || cell > MaximumCellCoordinate
            )
            {
                return false;
            }

            coordinate = (long)cell;
            return true;
        }

        private readonly record struct CellKey(long X, long Y);

        internal readonly record struct CandidatePair(int FirstIndex, int SecondIndex);
    }
}
