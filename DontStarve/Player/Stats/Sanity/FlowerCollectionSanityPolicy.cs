#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity;

internal static class FlowerCollectionSanityPolicy
{
    internal static double GetRecovery(string? qualifiedItemId, int category)
    {
        // Category drives custom flowers too; the two vanilla spring forage items are exceptions.
        return qualifiedItemId is not null
            && qualifiedItemId.StartsWith("(O)", StringComparison.Ordinal)
            && (category == -80 || qualifiedItemId is "(O)18" or "(O)22")
                ? 5d : 0d;
    }

    internal static bool WasCropHarvested(
        bool wasDead, bool wasReady, bool junimoHarvest, bool returnedRemoveCrop,
        bool wasFullyGrown, int previousRegrowDays, bool fullyGrown, int regrowDays)
    {
        // Crop.harvest returns "remove this crop", not "harvest succeeded" for regrowing crops.
        return !wasDead && wasReady && !junimoHarvest
            && (returnedRemoveCrop
                || (fullyGrown && regrowDays > 0
                    && (!wasFullyGrown || previousRegrowDays <= 0)));
    }
}

internal enum FlowerCollectionKind { Crop, Forage }

/// <summary>Collection evidence only. The client never supplies a Sanity delta.</summary>
internal sealed class FlowerCollectionRequest
{
    public string SessionId { get; set; } = string.Empty;
    public long PlayerId { get; set; }
    public long Nonce { get; set; }
    public uint Day { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public int TileX { get; set; }
    public int TileY { get; set; }
    public string QualifiedItemId { get; set; } = string.Empty;
    public FlowerCollectionKind Kind { get; set; }

    internal bool IsValid(string sessionId, uint day, long senderPlayerId)
    {
        return SanityProtocol.IsValidSessionId(sessionId)
            && SessionId == sessionId && Day == day
            && PlayerId != 0 && PlayerId == senderPlayerId && Nonce > 0
            && !string.IsNullOrWhiteSpace(LocationName) && LocationName.Length <= 256
            && TileX >= 0 && TileY >= 0
            && QualifiedItemId is not null && QualifiedItemId.Length is > 3 and <= 256
            && QualifiedItemId.StartsWith("(O)", StringComparison.Ordinal)
            && Kind is FlowerCollectionKind.Crop or FlowerCollectionKind.Forage;
    }
}

internal readonly record struct FlowerCollectionAuthorityObservation(
    bool OwnerAtLocation, bool WithinReach, bool SourceCollected, int ItemCategory);

internal enum FlowerCollectionSettlementStatus { Rejected, AwaitingWorld, Duplicate, Applied }

/// <summary>Host-only, additive collection receipts; no snapshot revision can overwrite another pick.</summary>
internal sealed class FlowerCollectionSanitySettlement
{
    private const int MaximumReceipts = 16384;
    private readonly HashSet<(long Owner, long Nonce)> receipts = new();
    private readonly HashSet<(string Location, int X, int Y, string Item, FlowerCollectionKind Kind)> sources = new();
    private string session = string.Empty;
    private uint day;

    internal FlowerCollectionSettlementStatus TryApply(
        FlowerCollectionRequest request, long senderPlayerId, string hostSession, uint currentDay,
        FlowerCollectionAuthorityObservation observation,
        Func<long, double, SanityChangeResult> apply)
    {
        if (request is null || !request.IsValid(hostSession, currentDay, senderPlayerId))
            return FlowerCollectionSettlementStatus.Rejected;
        if (session != hostSession || day != currentDay)
        {
            Clear();
            session = hostSession;
            day = currentDay;
        }
        var source = (request.LocationName, request.TileX, request.TileY, request.QualifiedItemId, request.Kind);
        if (receipts.Contains((senderPlayerId, request.Nonce)) || sources.Contains(source))
            return FlowerCollectionSettlementStatus.Duplicate;
        var recovery = FlowerCollectionSanityPolicy.GetRecovery(request.QualifiedItemId, observation.ItemCategory);
        if (!observation.OwnerAtLocation || !observation.WithinReach || recovery <= 0
            || receipts.Count >= MaximumReceipts)
            return FlowerCollectionSettlementStatus.Rejected;
        if (!observation.SourceCollected)
            return FlowerCollectionSettlementStatus.AwaitingWorld;

        var result = apply(request.PlayerId, recovery);
        if (result.Status is not (SanityChangeStatus.Applied or SanityChangeStatus.NoChange))
            return FlowerCollectionSettlementStatus.Rejected;
        receipts.Add((senderPlayerId, request.Nonce));
        // Vanilla cannot mature a second crop or naturally respawn forage on this tile in the
        // same day. Share the source receipt across owners to prevent two network claims.
        sources.Add(source);
        return FlowerCollectionSettlementStatus.Applied;
    }

    internal void Clear()
    {
        receipts.Clear();
        sources.Clear();
        session = string.Empty;
        day = 0;
    }
}
