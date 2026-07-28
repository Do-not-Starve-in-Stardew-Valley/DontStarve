#nullable enable

using System;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

/// <summary>
/// Narrow Stardew adapter for the pure settlement service. The qualified item ID lives only at
/// this semantic-to-game bridge; profile quantity/chance/reward remain the canonical data source.
/// </summary>
internal sealed class SmapiHostileShadowSettlementEffects
    : IHostileShadowDropSpawnAuthority,
        IHostileShadowLastHitterAuthority,
        IHostileShadowSanityRewardAuthority
{
    internal const string VoidEssenceQualifiedItemId = "(O)769";

    private readonly SanitySystemLifecycleCoordinator lifecycle;

    internal SmapiHostileShadowSettlementEffects(
        SanitySystemLifecycleCoordinator lifecycle
    )
    {
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    public HostileShadowDropSpawnReceipt Spawn(
        HostileShadowDropSpawnRequest request
    )
    {
        if (!TryValidateHostSession(request.Key, out var reason))
            return HostileShadowDropSpawnReceipt.Rejected(reason);
        if (
            !string.Equals(
                request.ItemSemanticId,
                ShadowMonsterProfileContractIds.VoidEssenceSemanticItem,
                StringComparison.Ordinal
            )
        )
        {
            return HostileShadowDropSpawnReceipt.Rejected(
                "hostile-shadow.settlement-item-semantic-unsupported"
            );
        }
        if (request.Quantity <= 0 || request.Quantity > 999)
        {
            return HostileShadowDropSpawnReceipt.Rejected(
                "hostile-shadow.settlement-item-quantity-invalid"
            );
        }
        var location = Game1.getLocationFromName(request.LocationId);
        if (
            location is null
            || !string.Equals(
                location.NameOrUniqueName,
                request.LocationId,
                StringComparison.Ordinal
            )
        )
        {
            return HostileShadowDropSpawnReceipt.Rejected(
                "hostile-shadow.settlement-drop-location-invalid"
            );
        }
        if (!ItemRegistry.Exists(VoidEssenceQualifiedItemId))
        {
            return HostileShadowDropSpawnReceipt.Rejected(
                "hostile-shadow.settlement-void-essence-unavailable"
            );
        }

        var item = ItemRegistry.Create(
            VoidEssenceQualifiedItemId,
            request.Quantity,
            0,
            true
        );
        if (item is null)
        {
            return HostileShadowDropSpawnReceipt.Rejected(
                "hostile-shadow.settlement-item-create-returned-null"
            );
        }
        var debrisCountBefore = location.debris.Count;
        Game1.createItemDebris(
            item,
            new Vector2((float)request.PositionX, (float)request.PositionY),
            2,
            location
        );
        if (location.debris.Count <= debrisCountBefore)
        {
            return HostileShadowDropSpawnReceipt.Rejected(
                "hostile-shadow.settlement-debris-add-not-observed"
            );
        }
        return HostileShadowDropSpawnReceipt.Success(
            "hostile-shadow.settlement-ground-drop-spawned"
        );
    }

    public HostileShadowLastHitterReceipt Resolve(
        HostileShadowLastHitterRequest request
    )
    {
        if (!TryValidateHostSession(request.Key, out var reason))
            return HostileShadowLastHitterReceipt.Invalid(reason);
        if (!SanityPlayerKey.IsCanonical(request.AttributedPlayerKey))
        {
            return HostileShadowLastHitterReceipt.Invalid(
                "hostile-shadow.settlement-last-hitter-key-invalid"
            );
        }
        if (!TryGetOnlinePlayerInLocation(
                request.AttributedPlayerKey,
                request.LocationId,
                out _,
                out reason
            ))
        {
            return HostileShadowLastHitterReceipt.Invalid(reason);
        }
        return HostileShadowLastHitterReceipt.Valid(
            request.AttributedPlayerKey,
            "hostile-shadow.settlement-last-hitter-valid"
        );
    }

    public HostileShadowSanityRewardReceipt Apply(
        HostileShadowSanityRewardRequest request
    )
    {
        if (
            request.Source != SanityChangeSource.HostileShadowKill
            || request.Delta < 0d
            || !double.IsFinite(request.Delta)
        )
        {
            return RejectedReward(
                request,
                "hostile-shadow.settlement-sanity-request-invalid"
            );
        }
        if (!TryValidateHostSession(request.Key, out var reason))
            return RejectedReward(request, reason);
        if (!TryGetOnlinePlayerInLocation(
                request.PlayerKey,
                request.LocationId,
                out _,
                out reason
        ))
            return RejectedReward(request, reason);
        if (!lifecycle.TryGetBaseSnapshot(request.PlayerKey, out var before))
            return RejectedReward(
                request,
                "hostile-shadow.settlement-last-hitter-sanity-missing"
            );

        var result = lifecycle.ApplySanityChange(
            request.PlayerKey,
            request.Delta,
            SanityChangeSource.HostileShadowKill
        );
        if (
            result.Status
                is not SanityChangeStatus.Applied
                    and not SanityChangeStatus.NoChange
            || result.Snapshot is null
        )
        {
            return new HostileShadowSanityRewardReceipt(
                HostileShadowSanityRewardStatus.Rejected,
                request.PlayerKey,
                request.Delta,
                request.Source,
                before.Revision,
                before.Revision,
                before.Current,
                before.Current,
                string.Concat(
                    "hostile-shadow.settlement-sanity-rejected:",
                    result.Reason
                )
            );
        }
        var after = result.Snapshot;
        return new HostileShadowSanityRewardReceipt(
            result.Status == SanityChangeStatus.Applied
                ? HostileShadowSanityRewardStatus.Applied
                : HostileShadowSanityRewardStatus.NoChange,
            request.PlayerKey,
            request.Delta,
            request.Source,
            before.Revision,
            after.Revision,
            before.Current,
            after.Current,
            result.Reason
        );
    }

    private bool TryValidateHostSession(
        HostileShadowSettlementKey key,
        out string reason
    )
    {
        if (!Context.IsWorldReady || !Game1.IsMasterGame)
        {
            reason = "hostile-shadow.settlement-host-world-unavailable";
            return false;
        }
        if (
            lifecycle.AuthorityRole != SanityAuthorityRole.Host
            || !key.IsValid
            || !string.Equals(
                lifecycle.SessionId,
                key.SessionId,
                StringComparison.Ordinal
            )
        )
        {
            reason = "hostile-shadow.settlement-sanity-session-mismatch";
            return false;
        }
        reason = "hostile-shadow.settlement-host-session-valid";
        return true;
    }

    private static bool TryGetOnlinePlayerInLocation(
        string playerKey,
        string locationId,
        out Farmer? player,
        out string reason
    )
    {
        player = null;
        if (!SanityPlayerKey.IsCanonical(playerKey))
        {
            reason = "hostile-shadow.settlement-last-hitter-key-invalid";
            return false;
        }
        foreach (var candidate in Game1.getOnlineFarmers())
        {
            if (
                !string.Equals(
                    SanityPlayerKey.FromUniqueMultiplayerId(
                        candidate.UniqueMultiplayerID
                    ),
                    playerKey,
                    StringComparison.Ordinal
                )
            )
            {
                continue;
            }
            player = candidate;
            break;
        }
        if (player is null)
        {
            reason = "hostile-shadow.settlement-last-hitter-offline";
            return false;
        }
        var location = Game1.getLocationFromName(locationId);
        if (
            location is null
            || player.currentLocation is null
            || !ReferenceEquals(player.currentLocation, location)
            || !string.Equals(
                player.currentLocation.NameOrUniqueName,
                locationId,
                StringComparison.Ordinal
            )
        )
        {
            player = null;
            reason = "hostile-shadow.settlement-last-hitter-location-mismatch";
            return false;
        }
        reason = "hostile-shadow.settlement-last-hitter-online-in-death-location";
        return true;
    }

    private static HostileShadowSanityRewardReceipt RejectedReward(
        HostileShadowSanityRewardRequest request,
        string reason
    )
    {
        return new HostileShadowSanityRewardReceipt(
            HostileShadowSanityRewardStatus.Rejected,
            request.PlayerKey,
            request.Delta,
            request.Source,
            0,
            0,
            0d,
            0d,
            string.IsNullOrWhiteSpace(reason)
                ? "hostile-shadow.settlement-sanity-request-invalid"
                : reason
        );
    }
}
