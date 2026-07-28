#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DontStarve.Config;

internal interface IContentPatcherTokenRegistrationApi
{
    void RegisterToken(string name, Func<IEnumerable<string>?> getValue);
}

internal interface IContentPatcherTokenApiProvider
{
    bool TryGetApi(
        out IContentPatcherTokenRegistrationApi? api,
        out string reason
    );
}

internal enum ContentPatcherTokenRegistrationStatus
{
    Available,
    Degraded,
    Unavailable,
}

internal sealed class ContentPatcherTokenRegistrationResult
{
    internal ContentPatcherTokenRegistrationResult(
        ContentPatcherTokenRegistrationStatus status,
        string reason,
        IReadOnlyCollection<string>? registeredKeys = null
    )
    {
        Status = status;
        Reason = reason;
        RegisteredKeys = new ReadOnlyCollection<string>(
            registeredKeys is null
                ? Array.Empty<string>()
                : new List<string>(registeredKeys)
        );
    }

    internal ContentPatcherTokenRegistrationStatus Status { get; }

    internal string Reason { get; }

    internal IReadOnlyList<string> RegisteredKeys { get; }
}

internal static class ContentPatcherTokenRegistrar
{
    internal static ContentPatcherTokenRegistrationResult Register(
        ConfigRegistry registry,
        TypedConfigResolver resolver,
        IContentPatcherTokenApiProvider provider,
        Action<string> report
    )
    {
        if (registry is null)
            throw new ArgumentNullException(nameof(registry));
        if (resolver is null)
            throw new ArgumentNullException(nameof(resolver));
        if (provider is null)
            throw new ArgumentNullException(nameof(provider));
        if (report is null)
            throw new ArgumentNullException(nameof(report));

        var reportedReasons = new HashSet<string>(StringComparer.Ordinal);

        void ReportOnce(string reason)
        {
            if (reportedReasons.Add(reason))
                report(reason);
        }

        IContentPatcherTokenRegistrationApi? api;
        string unavailableReason;
        try
        {
            if (!provider.TryGetApi(out api, out unavailableReason) || api is null)
            {
                if (string.IsNullOrWhiteSpace(unavailableReason))
                    unavailableReason = "cp.api-unavailable";

                ReportOnce(unavailableReason);
                return new ContentPatcherTokenRegistrationResult(
                    ContentPatcherTokenRegistrationStatus.Unavailable,
                    unavailableReason
                );
            }
        }
        catch (Exception)
        {
            const string reason = "cp.api-probe-failed";
            ReportOnce(reason);
            return new ContentPatcherTokenRegistrationResult(
                ContentPatcherTokenRegistrationStatus.Unavailable,
                reason
            );
        }

        var registeredKeys = new List<string>();
        var degraded = false;
        foreach (var option in registry.Options)
        {
            if (!option.ExposeToContentPatcher)
                continue;

            var key = option.Key;
            try
            {
                // Content Patcher adds the provider manifest ID itself. Register only
                // the bare schema key so the public name is Yurin.DontStarve/<Key>.
                api.RegisterToken(
                    key,
                    () =>
                    {
                        // Resolver lookup is an in-memory snapshot read. Never parse or
                        // touch disk from a token query, and never guess a fallback value.
                        if (!resolver.TryGetCanonical(key, out var canonical, out _))
                        {
                            ReportOnce($"cp.token-value-unavailable:{key}");
                            return null;
                        }

                        return new[] { canonical };
                    }
                );
                registeredKeys.Add(key);
            }
            catch (Exception)
            {
                degraded = true;
                ReportOnce($"cp.token-registration-failed:{key}");
            }
        }

        if (registeredKeys.Count == 0)
        {
            const string reason = "cp.no-tokens-registered";
            ReportOnce(reason);
            return new ContentPatcherTokenRegistrationResult(
                ContentPatcherTokenRegistrationStatus.Unavailable,
                reason
            );
        }

        var status = degraded
            ? ContentPatcherTokenRegistrationStatus.Degraded
            : ContentPatcherTokenRegistrationStatus.Available;
        return new ContentPatcherTokenRegistrationResult(
            status,
            degraded ? "cp.tokens-registration-degraded" : "cp.tokens-registered",
            registeredKeys
        );
    }
}
