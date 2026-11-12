#nullable enable

using System;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;

namespace DontStarve.Player.Combat.InvincibilityFrames;

internal readonly record struct SmapiInvincibilityFrameDamageResult(
    bool Accepted,
    InvincibilityFrameDamageOutcome Outcome,
    int HealthBefore,
    int HealthAfter,
    bool DurationOverridden,
    int DurationMilliseconds,
    string Reason
);

/// <summary>
/// Victim-local bridge over vanilla Farmer.takeDamage. It never owns health or death; it only
/// replaces the invincibility duration granted by an accepted hit when the policy requests it.
/// </summary>
internal sealed class SmapiInvincibilityFrameDamageService
{
    private readonly IMonitor monitor;
    private readonly InvincibilityFrameBonusRegistry bonusRegistry;

    internal SmapiInvincibilityFrameDamageService(
        IMonitor monitor,
        InvincibilityFrameBonusRegistry bonusRegistry
    )
    {
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.bonusRegistry = bonusRegistry
            ?? throw new ArgumentNullException(nameof(bonusRegistry));
    }

    internal static SmapiInvincibilityFrameDamageService CreateDefault(
        IMonitor monitor
    )
    {
        var registry = new InvincibilityFrameBonusRegistry();
        if (
            !registry.TryRegister(
                new ProtectiveRingInvincibilityFrameBonusSource(),
                out var reason
            )
        )
        {
            monitor.Log(
                string.Concat(
                    "Invincibility-frame default bonus registration failed (",
                    reason,
                    ")."
                ),
                LogLevel.Error
            );
        }

        return new SmapiInvincibilityFrameDamageService(monitor, registry);
    }

    internal SmapiInvincibilityFrameDamageResult Apply(
        Farmer target,
        int damage,
        Monster? damager,
        InvincibilityFramePolicy policy
    )
    {
        if (target is null)
        {
            return Rejected(
                InvincibilityFrameDamageOutcome.NoChange,
                "invincibility-frame.target-null"
            );
        }

        if (!target.IsLocalPlayer)
        {
            return Rejected(
                InvincibilityFrameDamageOutcome.NoChange,
                "invincibility-frame.target-not-local"
            );
        }

        if (damage <= 0)
        {
            return Rejected(
                InvincibilityFrameDamageOutcome.NoChange,
                "invincibility-frame.damage-nonpositive"
            );
        }

        var canBeDamagedBefore = target.CanBeDamaged();
        var temporarilyInvincibleBefore = target.temporarilyInvincible;
        var healthBefore = target.health;
        var yobaBuffBefore = target.hasBuff("21");

        try
        {
            target.takeDamage(damage, overrideParry: false, damager);
        }
        catch (Exception exception)
        {
            monitor.Log(
                string.Concat(
                    "Invincibility-frame vanilla damage failed (",
                    exception.GetType().Name,
                    ")."
                ),
                LogLevel.Warn
            );
            return new SmapiInvincibilityFrameDamageResult(
                Accepted: false,
                Outcome: InvincibilityFrameDamageOutcome.NoChange,
                HealthBefore: healthBefore,
                HealthAfter: target.health,
                DurationOverridden: false,
                DurationMilliseconds: 0,
                Reason: string.Concat(
                    "invincibility-frame.vanilla-damage-threw-",
                    exception.GetType().Name
                )
            );
        }

        var healthAfter = target.health;
        var yobaBuffAfter = target.hasBuff("21");
        var outcome = InvincibilityFrameRules.Classify(
            new InvincibilityFrameObservation(
                canBeDamagedBefore,
                temporarilyInvincibleBefore,
                healthBefore,
                healthAfter,
                yobaBuffBefore,
                yobaBuffAfter,
                !temporarilyInvincibleBefore && target.temporarilyInvincible
            )
        );

        if (
            outcome != InvincibilityFrameDamageOutcome.Applied
            || policy.Mode == InvincibilityFramePolicyMode.Normal
        )
        {
            return new SmapiInvincibilityFrameDamageResult(
                Accepted: true,
                Outcome: outcome,
                HealthBefore: healthBefore,
                HealthAfter: healthAfter,
                DurationOverridden: false,
                DurationMilliseconds: 0,
                Reason: outcome == InvincibilityFrameDamageOutcome.Applied
                    ? "invincibility-frame.normal-vanilla-preserved"
                    : string.Concat("invincibility-frame.hit-not-overridden:", outcome)
            );
        }

        var bonusMilliseconds = 0;
        if (policy.Mode == InvincibilityFramePolicyMode.Weak)
        {
            if (
                !bonusRegistry.TryResolve(
                    target,
                    out bonusMilliseconds,
                    out var bonusReason
                )
            )
            {
                monitor.Log(
                    string.Concat(
                        "Invincibility-frame bonus resolution failed (",
                        bonusReason,
                        "); vanilla duration preserved."
                    ),
                    LogLevel.Warn
                );
                return new SmapiInvincibilityFrameDamageResult(
                    Accepted: true,
                    Outcome: outcome,
                    HealthBefore: healthBefore,
                    HealthAfter: healthAfter,
                    DurationOverridden: false,
                    DurationMilliseconds: 0,
                    Reason: bonusReason
                );
            }
        }

        var resolution = InvincibilityFrameRules.ResolveDuration(
            policy,
            bonusMilliseconds
        );
        if (!resolution.Success || !resolution.OverridesVanilla)
        {
            monitor.Log(
                string.Concat(
                    "Invincibility-frame policy resolution failed (",
                    resolution.Reason,
                    "); vanilla duration preserved."
                ),
                LogLevel.Warn
            );
            return new SmapiInvincibilityFrameDamageResult(
                Accepted: true,
                Outcome: outcome,
                HealthBefore: healthBefore,
                HealthAfter: healthAfter,
                DurationOverridden: false,
                DurationMilliseconds: 0,
                Reason: resolution.Reason
            );
        }

        ApplyDuration(target, resolution.DurationMilliseconds);
        return new SmapiInvincibilityFrameDamageResult(
            Accepted: true,
            Outcome: outcome,
            HealthBefore: healthBefore,
            HealthAfter: healthAfter,
            DurationOverridden: true,
            DurationMilliseconds: resolution.DurationMilliseconds,
            Reason: resolution.Reason
        );
    }

    private static void ApplyDuration(Farmer target, int durationMilliseconds)
    {
        if (durationMilliseconds <= 0)
        {
            target.temporarilyInvincible = false;
            target.temporaryInvincibilityTimer = 0;
            target.currentTemporaryInvincibilityDuration = 0;
            return;
        }

        target.temporarilyInvincible = true;
        target.flashDuringThisTemporaryInvincibility = true;
        target.temporaryInvincibilityTimer = 0;
        target.currentTemporaryInvincibilityDuration = durationMilliseconds;
    }

    private static SmapiInvincibilityFrameDamageResult Rejected(
        InvincibilityFrameDamageOutcome outcome,
        string reason
    )
    {
        return new SmapiInvincibilityFrameDamageResult(
            Accepted: false,
            Outcome: outcome,
            HealthBefore: 0,
            HealthAfter: 0,
            DurationOverridden: false,
            DurationMilliseconds: 0,
            Reason: reason
        );
    }
}
