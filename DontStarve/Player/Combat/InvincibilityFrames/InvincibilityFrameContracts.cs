#nullable enable

namespace DontStarve.Player.Combat.InvincibilityFrames;

internal enum InvincibilityFramePolicyMode
{
    Normal = 0,
    Strong = 1,
    Weak = 2,
}

internal readonly record struct InvincibilityFramePolicy(
    InvincibilityFramePolicyMode Mode,
    int DurationMilliseconds
)
{
    internal static InvincibilityFramePolicy Normal => new(
        InvincibilityFramePolicyMode.Normal,
        0
    );

    internal static InvincibilityFramePolicy Strong(int durationMilliseconds) => new(
        InvincibilityFramePolicyMode.Strong,
        durationMilliseconds
    );

    internal static InvincibilityFramePolicy Weak(int baseMilliseconds) => new(
        InvincibilityFramePolicyMode.Weak,
        baseMilliseconds
    );
}

internal enum InvincibilityFrameDamageOutcome
{
    Applied = 0,
    BlockedByExistingInvincibility = 1,
    YobaPreventedDamage = 2,
    NoChange = 3,
}

internal readonly record struct InvincibilityFrameObservation(
    bool CanBeDamagedBefore,
    bool TemporarilyInvincibleBefore,
    int HealthBefore,
    int HealthAfter,
    bool YobaBuffBefore,
    bool YobaBuffAfter,
    bool VanillaInvincibilityGranted
);

internal readonly record struct InvincibilityFrameResolution(
    bool Success,
    bool OverridesVanilla,
    int DurationMilliseconds,
    string Reason
)
{
    internal static InvincibilityFrameResolution PreserveVanilla() => new(
        Success: true,
        OverridesVanilla: false,
        DurationMilliseconds: 0,
        Reason: "invincibility-frame.normal-preserves-vanilla"
    );

    internal static InvincibilityFrameResolution Override(int durationMilliseconds) => new(
        Success: true,
        OverridesVanilla: true,
        DurationMilliseconds: durationMilliseconds,
        Reason: "invincibility-frame.duration-resolved"
    );

    internal static InvincibilityFrameResolution Failed(string reason) => new(
        Success: false,
        OverridesVanilla: false,
        DurationMilliseconds: 0,
        Reason: string.IsNullOrWhiteSpace(reason)
            ? "invincibility-frame.resolution-failed"
            : reason
    );
}
