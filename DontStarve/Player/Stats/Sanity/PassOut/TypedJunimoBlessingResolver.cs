#nullable enable

using System;
using DontStarve.Config;

namespace DontStarve.Player.Stats.Sanity.PassOut;

internal readonly record struct JunimoBlessingResolution(
    bool HasValue,
    bool Enabled,
    string StableReason
)
{
    internal static JunimoBlessingResolution Available(bool enabled)
    {
        return new JunimoBlessingResolution(
            true,
            enabled,
            "passout.config.junimo-blessing-available"
        );
    }

    internal static JunimoBlessingResolution Unavailable(string reason)
    {
        return new JunimoBlessingResolution(
            false,
            false,
            string.IsNullOrWhiteSpace(reason)
                ? "passout.config.junimo-blessing-unavailable"
                : reason
        );
    }
}

internal interface IJunimoBlessingResolver
{
    JunimoBlessingResolution Resolve();
}

internal sealed class TypedJunimoBlessingResolver : IJunimoBlessingResolver
{
    private readonly TypedConfigResolver resolver;

    internal TypedJunimoBlessingResolver(TypedConfigResolver resolver)
    {
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public JunimoBlessingResolution Resolve()
    {
        var resolution = resolver.GetBoolean(ConfigKeys.EnableJunimoBlessing);
        return resolution.HasValue
            ? JunimoBlessingResolution.Available(resolution.Value)
            : JunimoBlessingResolution.Unavailable(resolution.Reason);
    }
}

internal sealed class UnavailableJunimoBlessingResolver
    : IJunimoBlessingResolver
{
    public JunimoBlessingResolution Resolve()
    {
        return JunimoBlessingResolution.Unavailable(
            "passout.config.junimo-blessing-runtime-unavailable"
        );
    }
}
