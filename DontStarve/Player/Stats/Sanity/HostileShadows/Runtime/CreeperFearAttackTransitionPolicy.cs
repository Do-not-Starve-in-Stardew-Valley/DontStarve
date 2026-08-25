#nullable enable

using System;
using DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Runtime;

internal interface IHostileAttackTransitionRandom
{
    double NextSample();
}

/// <summary>
/// Per-entity stable RNG. It never consumes the shared game RNG, so retries and unrelated game
/// systems do not perturb one another; the policy memoizes each completed attack revision.
/// </summary>
internal sealed class StableHostileAttackTransitionRandom
    : IHostileAttackTransitionRandom
{
    private uint state;

    internal StableHostileAttackTransitionRandom(int seed)
    {
        state = unchecked((uint)seed);
        if (state == 0)
            state = 0x9E3779B9u;
    }

    public double NextSample()
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (state & 0x00FFFFFFu) / 16777216d;
    }
}

internal static class HostileAttackTransitionSeed
{
    internal static int Derive(
        string sessionId,
        long entityId,
        string postAttackPolicyId
    )
    {
        // FNV-1a is a stable session correlation seed, not a security primitive.
        var hash = 2166136261u;
        Add(ref hash, sessionId);
        Add(ref hash, unchecked((ulong)entityId));
        Add(ref hash, postAttackPolicyId);
        var seed = (int)(hash & 0x7FFFFFFFu);
        return seed == 0 ? 1 : seed;
    }

    private static void Add(ref uint hash, string value)
    {
        foreach (var character in value ?? string.Empty)
        {
            hash ^= character;
            hash *= 16777619u;
        }
    }

    private static void Add(ref uint hash, ulong value)
    {
        for (var index = 0; index < sizeof(ulong); index++)
        {
            hash ^= (byte)(value & byte.MaxValue);
            hash *= 16777619u;
            value >>= 8;
        }
    }
}

/// <summary>
/// Shared memoization for species which use the validated taunt-or-delay contract. Probability and
/// post-Taunt delay remain explicit species inputs; the common attack state still owns no values.
/// </summary>
internal abstract class HostileTauntOrDelayAttackTransitionPolicy
    : IHostileAttackTransitionPolicy
{
    private readonly long entityId;
    private readonly IHostileAttackTransitionRandom random;
    private readonly double tauntProbability;
    private readonly double delayAfterTauntSeconds;
    private bool hasCachedTransition;
    private bool cachedTransitionValid;
    private HostileAttackTransitionContext cachedContext;
    private HostileAttackPostAttackTransition cachedTransition;

    protected HostileTauntOrDelayAttackTransitionPolicy(
        long entityId,
        IHostileAttackTransitionRandom random,
        double tauntProbability,
        double delayAfterTauntSeconds
    )
    {
        if (entityId <= 0)
            throw new ArgumentOutOfRangeException(nameof(entityId));
        if (
            !double.IsFinite(tauntProbability)
            || tauntProbability < 0d
            || tauntProbability > 1d
        )
        {
            throw new ArgumentOutOfRangeException(nameof(tauntProbability));
        }
        if (
            !double.IsFinite(delayAfterTauntSeconds)
            || delayAfterTauntSeconds < 0d
        )
        {
            throw new ArgumentOutOfRangeException(nameof(delayAfterTauntSeconds));
        }
        this.entityId = entityId;
        this.random = random ?? throw new ArgumentNullException(nameof(random));
        this.tauntProbability = tauntProbability;
        this.delayAfterTauntSeconds = delayAfterTauntSeconds;
    }

    public bool ShouldTauntBeforeFirstChase(
        HostileAttackTransitionContext context
    )
    {
        return IsContextValid(context);
    }

    public bool TryResolvePostAttackTransition(
        HostileAttackTransitionContext context,
        double configuredProfileIntervalSeconds,
        out HostileAttackPostAttackTransition transition
    )
    {
        transition = default;
        if (
            !IsContextValid(context)
            || !double.IsFinite(configuredProfileIntervalSeconds)
            || configuredProfileIntervalSeconds < 0d
        )
        {
            return false;
        }

        if (hasCachedTransition)
        {
            if (context.Equals(cachedContext))
            {
                transition = cachedTransition;
                return cachedTransitionValid;
            }
            if (context.AttackInstanceRevision <= cachedContext.AttackInstanceRevision)
                return false;
        }

        cachedContext = context;
        cachedTransition = default;
        cachedTransitionValid = false;
        hasCachedTransition = true;

        var sample = random.NextSample();
        if (!double.IsFinite(sample) || sample < 0d || sample >= 1d)
            return false;

        cachedTransition = sample < tauntProbability
            ? new HostileAttackPostAttackTransition(
                true,
                delayAfterTauntSeconds
            )
            : new HostileAttackPostAttackTransition(
                false,
                configuredProfileIntervalSeconds
            );
        cachedTransitionValid = true;
        transition = cachedTransition;
        return true;
    }

    private bool IsContextValid(HostileAttackTransitionContext context)
    {
        return context.EntityId == entityId
            && context.AttackInstanceRevision > 0
            && SanityPlayerKey.IsCanonical(context.TargetPlayerKey);
    }
}

/// <summary>
/// Creeper Fear samples one post-attack decision per entity/target/revision: below 0.25 enters
/// Taunt then waits 1.2 seconds; otherwise the canonical profile interval is used.
/// </summary>
internal sealed class CreeperFearAttackTransitionPolicy
    : HostileTauntOrDelayAttackTransitionPolicy
{
    internal const double TauntProbability = 0.25d;
    internal const double DelayAfterTauntSeconds = 1.2d;

    internal CreeperFearAttackTransitionPolicy(
        long entityId,
        IHostileAttackTransitionRandom random
    )
        : base(
            entityId,
            random,
            TauntProbability,
            DelayAfterTauntSeconds
        )
    { }
}

/// <summary>
/// Terrorbeak owns the same strict probability but a 0.6-second post-Taunt delay; its direct
/// branch uses the attack interval from the validated difficulty profile.
/// </summary>
internal sealed class TerrorbeakAttackTransitionPolicy
    : HostileTauntOrDelayAttackTransitionPolicy
{
    internal const double TauntProbability = 0.25d;
    internal const double DelayAfterTauntSeconds = 0.6d;

    internal TerrorbeakAttackTransitionPolicy(
        long entityId,
        IHostileAttackTransitionRandom random
    )
        : base(
            entityId,
            random,
            TauntProbability,
            DelayAfterTauntSeconds
        )
    { }
}

internal static class HostileAttackTransitionPolicyFactory
{
    internal static bool TryCreate(
        ShadowMonsterRuntimeProfile? profile,
        string sessionId,
        long entityId,
        out IHostileAttackTransitionPolicy? policy,
        out string reason
    )
    {
        policy = null;
        if (
            profile is null
            || !SanityProtocol.IsValidSessionId(sessionId)
            || entityId <= 0
        )
        {
            reason = "hostile-shadow.attack-transition-context-invalid";
            return false;
        }

        if (
            string.Equals(
                profile.AssetBindingId,
                ShadowMonsterAssetBindingIds.CreeperFear,
                StringComparison.Ordinal
            )
        )
        {
            if (
                !string.Equals(
                    profile.PostAttackPolicyId,
                    ShadowMonsterProfileContractIds.TauntOrDelayPostAttack,
                    StringComparison.Ordinal
                )
            )
            {
                reason = "hostile-shadow.creeper-fear-post-attack-policy-unsupported";
                return false;
            }
            policy = new CreeperFearAttackTransitionPolicy(
                entityId,
                new StableHostileAttackTransitionRandom(
                    HostileAttackTransitionSeed.Derive(
                        sessionId,
                        entityId,
                        profile.PostAttackPolicyId
                    )
                )
            );
            reason = "hostile-shadow.creeper-fear-transition-policy-ready";
            return true;
        }

        if (
            string.Equals(
                profile.AssetBindingId,
                ShadowMonsterAssetBindingIds.Terrorbeak,
                StringComparison.Ordinal
            )
        )
        {
            if (
                !string.Equals(
                    profile.PostAttackPolicyId,
                    ShadowMonsterProfileContractIds.TauntOrDelayPostAttack,
                    StringComparison.Ordinal
                )
            )
            {
                reason = "hostile-shadow.terrorbeak-post-attack-policy-unsupported";
                return false;
            }
            policy = new TerrorbeakAttackTransitionPolicy(
                entityId,
                new StableHostileAttackTransitionRandom(
                    HostileAttackTransitionSeed.Derive(
                        sessionId,
                        entityId,
                        profile.PostAttackPolicyId
                    )
                )
            );
            reason = "hostile-shadow.terrorbeak-transition-policy-ready";
            return true;
        }

        reason = "hostile-shadow.attack-transition-binding-unsupported";
        return false;
    }
}
