#nullable enable

using System;
using System.Collections.Generic;
using Common.ConfigurationServices;
using StardewModdingAPI;

namespace DontStarve.Config;

internal static class ModContentPatcherTokenRegistrar
{
    private const string ContentPatcherId = "Pathoschild.ContentPatcher";
    private const string MinimumContentPatcherVersion = "2.9.1";

    internal static ContentPatcherTokenRegistrationResult Register(
        IModHelper helper,
        IMonitor monitor,
        IManifest manifest,
        ConfigurationRuntime? runtime
    )
    {
        if (runtime is null)
        {
            const string reason = "cp.config-unavailable";
            monitor.Log($"[DontStarve][CP] {reason}", LogLevel.Warn);
            return new ContentPatcherTokenRegistrationResult(
                ContentPatcherTokenRegistrationStatus.Unavailable,
                reason
            );
        }

        return ContentPatcherTokenRegistrar.Register(
            runtime.Registry,
            runtime.Resolver,
            new SmapiContentPatcherApiProvider(helper, manifest),
            reason =>
            {
                var level = string.Equals(reason, "cp.not-installed", StringComparison.Ordinal)
                    ? LogLevel.Debug
                    : LogLevel.Warn;
                monitor.Log($"[DontStarve][CP] {reason}", level);
            }
        );
    }

    private sealed class SmapiContentPatcherApiProvider
        : IContentPatcherTokenApiProvider
    {
        private readonly IModHelper helper;
        private readonly IManifest providerManifest;

        internal SmapiContentPatcherApiProvider(
            IModHelper helper,
            IManifest providerManifest
        )
        {
            this.helper = helper;
            this.providerManifest = providerManifest;
        }

        public bool TryGetApi(
            out IContentPatcherTokenRegistrationApi? api,
            out string reason
        )
        {
            api = null;
            var installedManifest = helper.ModRegistry.Get(ContentPatcherId)?.Manifest;
            if (installedManifest is null)
            {
                reason = "cp.not-installed";
                return false;
            }

            // Stage 04 mirrors only the locally verified 2.9.1 API surface.
            if (installedManifest.Version.IsOlderThan(MinimumContentPatcherVersion))
            {
                reason = "cp.version-too-old";
                return false;
            }

            var contentPatcher = helper.ModRegistry.GetApi<IContentPatcherApi>(
                ContentPatcherId
            );
            if (contentPatcher is null)
            {
                reason = "cp.api-mismatch";
                return false;
            }

            api = new SmapiContentPatcherTokenRegistrationApi(
                contentPatcher,
                providerManifest
            );
            reason = "cp.api-available";
            return true;
        }
    }

    private sealed class SmapiContentPatcherTokenRegistrationApi
        : IContentPatcherTokenRegistrationApi
    {
        private readonly IContentPatcherApi api;
        private readonly IManifest manifest;

        internal SmapiContentPatcherTokenRegistrationApi(
            IContentPatcherApi api,
            IManifest manifest
        )
        {
            this.api = api;
            this.manifest = manifest;
        }

        public void RegisterToken(
            string name,
            Func<IEnumerable<string>?> getValue
        )
        {
            api.RegisterToken(manifest, name, getValue);
        }
    }
}
