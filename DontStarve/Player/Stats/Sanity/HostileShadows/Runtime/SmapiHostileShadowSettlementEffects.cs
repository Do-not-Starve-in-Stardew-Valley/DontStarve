#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Objects;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

/// <summary>
/// Narrow Stardew adapter for the pure settlement service. The qualified item ID lives only at
/// this semantic-to-game bridge; profile quantity/chance/reward remain the canonical data source.
/// </summary>
internal sealed class SmapiHostileShadowSettlementEffects
    : IHostileShadowDropSpawnAuthority,
        IHostileShadowLastHitterAuthority,
        IHostileShadowSanityRewardAuthority,
        IHostileShadowRingSnapshotAuthority,
        IHostileShadowKillEffectAuthority
{
    internal const string VoidEssenceQualifiedItemId = "(O)769";
    internal const string CoffeeQualifiedItemId = "(O)395";
    internal const string TripleShotEspressoQualifiedItemId = "(O)253";
    internal const string WarriorBuffId = "20";
    internal const string AdrenalineRushBuffId = "22";

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
        var stacks = request.DropStacks;
        if (stacks is null)
        {
            stacks = new[]
            {
                new HostileShadowDropStack(request.ItemSemanticId, request.Quantity),
            };
        }
        if (stacks.Count == 0 || stacks.Count > 64)
        {
            return HostileShadowDropSpawnReceipt.Rejected(
                "hostile-shadow.settlement-drop-stacks-invalid"
            );
        }
        var items = new List<Item>(stacks.Count);
        foreach (var stack in stacks)
        {
            if (
                stack.Quantity <= 0
                || stack.Quantity > 999
                || !TryGetQualifiedItemId(stack.ItemSemanticId, out var qualifiedItemId)
            )
            {
                return HostileShadowDropSpawnReceipt.Rejected(
                    "hostile-shadow.settlement-item-stack-invalid"
                );
            }
            if (!ItemRegistry.Exists(qualifiedItemId))
            {
                return HostileShadowDropSpawnReceipt.Rejected(
                    "hostile-shadow.settlement-item-unavailable"
                );
            }
            var item = ItemRegistry.Create(qualifiedItemId, stack.Quantity, 0, true);
            if (item is null)
            {
                return HostileShadowDropSpawnReceipt.Rejected(
                    "hostile-shadow.settlement-item-create-returned-null"
                );
            }
            items.Add(item);
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
        var debrisCountBefore = location.debris.Count;
        foreach (var item in items)
        {
            Game1.createItemDebris(
                item,
                new Vector2((float)request.PositionX, (float)request.PositionY),
                2,
                location
            );
        }
        if (location.debris.Count <= debrisCountBefore)
        {
            return HostileShadowDropSpawnReceipt.Rejected(
                "hostile-shadow.settlement-debris-add-not-observed"
            );
        }
        return HostileShadowDropSpawnReceipt.Success(
            string.Concat(
                "hostile-shadow.settlement-ground-drop-spawned:",
                items.Count
            )
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

    public HostileShadowRingSnapshotReceipt Resolve(
        HostileShadowRingSnapshotRequest request
    )
    {
        if (!TryValidateHostSession(request.Key, out var reason))
            return HostileShadowRingSnapshotReceipt.Invalid(reason);
        if (!TryGetOnlinePlayerInLocation(
                request.PlayerKey,
                request.LocationId,
                out var player,
                out reason
            ))
        {
            return HostileShadowRingSnapshotReceipt.Invalid(reason);
        }

        var ringIds = new List<string>(8);
        AppendSettlementRingIds(player!.leftRing.Value, ringIds);
        AppendSettlementRingIds(player.rightRing.Value, ringIds);
        var sequence = string.Join(",", ringIds);
        var hasBurglar = ringIds.Contains(
            HostileShadowVanillaRingIds.Burglar,
            StringComparer.Ordinal
        );
        return HostileShadowRingSnapshotReceipt.Valid(
            new HostileShadowRingSnapshot(
                sequence,
                hasBurglar,
                player.stats.Get("Book_Void") != 0,
                player.LuckLevel
            ),
            "hostile-shadow.settlement-ring-snapshot-captured"
        );
    }

    public HostileShadowKillEffectReceipt Apply(
        HostileShadowKillEffectRequest request
    )
    {
        if (!TryValidateHostSession(request.Key, out var reason))
            return RejectedKillEffects(request, reason);
        if (
            !SanityPlayerKey.IsCanonical(request.PlayerKey)
            || request.VampireHealth < 0
            || request.SoulSapperEnergy < 0
            || request.WarriorTriggerCount < 0
            || request.SavageTriggerCount < 0
            || request.NapalmExplosionCount < 0
            || !double.IsFinite(request.PositionX)
            || !double.IsFinite(request.PositionY)
            || !double.IsFinite(request.ExplosionTileX)
            || !double.IsFinite(request.ExplosionTileY)
        )
        {
            return RejectedKillEffects(
                request,
                "hostile-shadow.settlement-kill-effects-request-invalid"
            );
        }
        if (!TryGetOnlinePlayerInLocation(
                request.PlayerKey,
                request.LocationId,
                out var player,
                out reason
            ))
        {
            return RejectedKillEffects(request, reason);
        }

        player!.health = Math.Min(
            player.maxHealth,
            player.health + request.VampireHealth
        );
        player.Stamina = Math.Min(
            player.MaxStamina,
            player.Stamina + request.SoulSapperEnergy
        );
        for (var index = 0; index < request.WarriorTriggerCount; index++)
        {
            player.applyBuff(WarriorBuffId);
            if (player.IsLocalPlayer)
                Game1.playSound("warrior");
        }
        for (var index = 0; index < request.SavageTriggerCount; index++)
            player.applyBuff(AdrenalineRushBuffId);

        var location = player.currentLocation;
        if (location is null)
            return RejectedKillEffects(
                request,
                "hostile-shadow.settlement-kill-effects-location-missing"
            );
        for (var index = 0; index < request.NapalmExplosionCount; index++)
        {
            // This is the vanilla Ring.onMonsterSlay call. In Farm/SlimeHutch the call still
            // happens; only destroyObjects is false, so nearby monsters can still be affected.
            location.explode(
                new Vector2(
                    (float)request.ExplosionTileX,
                    (float)request.ExplosionTileY
                ),
                2,
                player,
                damageFarmers: false,
                damage_amount: -1,
                destroyObjects: location is not Farm && location is not SlimeHutch
            );
        }
        return new HostileShadowKillEffectReceipt(
            HostileShadowKillEffectStatus.Applied,
            request.PlayerKey,
            request.VampireHealth,
            request.SoulSapperEnergy,
            request.WarriorTriggerCount,
            request.SavageTriggerCount,
            request.NapalmExplosionCount,
            "hostile-shadow.settlement-kill-effects-applied"
        );
    }

    private static void AppendSettlementRingIds(
        Ring? ring,
        List<string> result
    )
    {
        if (ring is null)
            return;
        if (ring is CombinedRing combined)
        {
            foreach (var child in combined.combinedRings)
                AppendSettlementRingIds(child, result);
        }
        if (HostileShadowVanillaRingIds.IsSettlementRelevant(ring.ItemId))
            result.Add(ring.ItemId);
    }

    private static bool TryGetQualifiedItemId(
        string semanticId,
        out string qualifiedItemId
    )
    {
        qualifiedItemId = semanticId switch
        {
            HostileShadowSettlementItemSemanticIds.VoidEssence => VoidEssenceQualifiedItemId,
            HostileShadowSettlementItemSemanticIds.Coffee => CoffeeQualifiedItemId,
            HostileShadowSettlementItemSemanticIds.TripleShotEspresso
                => TripleShotEspressoQualifiedItemId,
            _ => string.Empty,
        };
        return qualifiedItemId.Length > 0;
    }

    private static HostileShadowKillEffectReceipt RejectedKillEffects(
        HostileShadowKillEffectRequest request,
        string reason
    )
    {
        return new HostileShadowKillEffectReceipt(
            HostileShadowKillEffectStatus.Rejected,
            request.PlayerKey,
            0,
            0,
            0,
            0,
            0,
            string.IsNullOrWhiteSpace(reason)
                ? "hostile-shadow.settlement-kill-effects-rejected"
                : reason
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
