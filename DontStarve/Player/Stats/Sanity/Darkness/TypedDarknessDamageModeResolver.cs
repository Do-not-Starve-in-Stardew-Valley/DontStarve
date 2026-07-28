#nullable enable

using System;
using DontStarve.Config;

namespace DontStarve.Player.Stats.Sanity.Darkness;

internal sealed class TypedDarknessDamageModeResolver : IDarknessDamageModeResolver
{
    private readonly TypedConfigResolver resolver;

    internal TypedDarknessDamageModeResolver(TypedConfigResolver resolver)
    {
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public DarknessDamageModeResolution Resolve()
    {
        var resolution = resolver.GetEnum(ConfigKeys.DarknessDamageMode);
        if (!resolution.HasValue)
        {
            return DarknessDamageModeResolution.Unavailable(
                $"darkness.mode.config-unavailable:{resolution.Reason}"
            );
        }

        return resolution.Value switch
        {
            "Off" => DarknessDamageModeResolution.Available(
                DarknessDamageMode.Off,
                resolution.Reason
            ),
            "NonLethal" => DarknessDamageModeResolution.Available(
                DarknessDamageMode.NonLethal,
                resolution.Reason
            ),
            "Default" => DarknessDamageModeResolution.Available(
                DarknessDamageMode.Default,
                resolution.Reason
            ),
            _ => DarknessDamageModeResolution.Unavailable(
                "darkness.mode.config-value-invalid"
            ),
        };
    }
}

internal sealed class UnavailableDarknessDamageModeResolver
    : IDarknessDamageModeResolver
{
    public DarknessDamageModeResolution Resolve()
    {
        return DarknessDamageModeResolution.Unavailable(
            "darkness.mode.typed-config-runtime-unavailable"
        );
    }
}
