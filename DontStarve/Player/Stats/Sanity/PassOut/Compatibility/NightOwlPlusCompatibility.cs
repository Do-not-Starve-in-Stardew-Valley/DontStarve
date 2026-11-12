#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity.PassOut.Compatibility;

internal enum NightOwlPlusCompatibilityStatus
{
    NotInstalled,
    InstalledSupported,
    InstalledUnsupported,
    FactsUnavailable,
}

internal readonly record struct NightOwlPlusManifestEvidence(
    string UniqueId,
    string Name,
    string Version,
    IReadOnlyList<string> UpdateKeys
);

internal readonly record struct NightOwlPlusCompatibilityResult(
    NightOwlPlusCompatibilityStatus Status,
    string DetectedVersion,
    bool ExactUidDetected,
    bool DiagnosticUpdateKeyObserved,
    string StableReason,
    string DiagnosticDetail
)
{
    internal bool AllowsConflictingSpecialDeathChain =>
        Status == NightOwlPlusCompatibilityStatus.NotInstalled;
}

/// <summary>
/// Legacy type name retained for the original 29714 integration. It now resolves all known
/// late-night mods once from loaded-manifest facts. Exact UIDs are the positive
/// identities; their Nexus update keys are bounded diagnostics/fail-closed collision evidence and
/// never grant positive support by themselves.
/// </summary>
internal static class NightOwlPlusCompatibility
{
    internal const string CapabilityId = "passout.compatibility.night-owl-plus";
    internal const string ExactUniqueId = "MaximillianRW.NightOwlPlus";
    internal const string OriginalNightOwlUniqueId = "Omegasis.NightOwl";
    internal const string StayUpUniqueId = "Su226.StayUp";
    internal const string DiagnosticUpdateKey = "Nexus:29714";
    internal const string OriginalNightOwlDiagnosticUpdateKey = "Nexus:433";
    internal const string StayUpDiagnosticUpdateKey = "Nexus:7592";
    internal const string ExplicitlySupportedVersion = "1.0.0";
    internal const string OriginalNightOwlSupportedVersion = "1.11.0";
    internal const string StayUpSupportedVersion = "1.1.3";
    internal const int MaximumDiagnosticManifests = 512;
    internal const int MaximumDiagnosticDetailLength = 256;

    internal static NightOwlPlusCompatibilityResult Unresolved()
    {
        return Unavailable(
            "passout.compatibility.night-owl-plus.registry-not-yet-resolved"
        );
    }

    internal static NightOwlPlusCompatibilityResult Resolve(
        Func<string, NightOwlPlusManifestEvidence?> exactUidLookup,
        Func<IReadOnlyList<NightOwlPlusManifestEvidence>> diagnosticLoadedManifests
    )
    {
        if (exactUidLookup is null || diagnosticLoadedManifests is null)
        {
            return Unavailable(
                "passout.compatibility.night-owl-plus.registry-accessor-unavailable"
            );
        }

        foreach (var identity in KnownIdentities)
        {
            NightOwlPlusManifestEvidence? exact;
            try
            {
                exact = exactUidLookup(identity.UniqueId);
            }
            catch (Exception exception)
            {
                return Unavailable(
                    "passout.compatibility.night-owl-plus.registry-exact-lookup-failed",
                    diagnosticDetail: Describe(exception)
                );
            }

            if (exact is { } manifest)
                return ResolveExactManifest(identity, manifest);
        }

        IReadOnlyList<NightOwlPlusManifestEvidence> loaded;
        try
        {
            loaded = diagnosticLoadedManifests();
        }
        catch (Exception exception)
        {
            return Unavailable(
                "passout.compatibility.night-owl-plus.registry-diagnostic-scan-failed",
                diagnosticDetail: Describe(exception)
            );
        }

        if (loaded is null || loaded.Count > MaximumDiagnosticManifests)
        {
            return Unavailable(
                "passout.compatibility.night-owl-plus.registry-diagnostic-scan-unavailable"
            );
        }

        foreach (var candidate in loaded)
        {
            if (FindIdentity(candidate.UniqueId) is { } identity)
            {
                return Unavailable(
                    "passout.compatibility.night-owl-plus.registry-results-inconsistent",
                    candidate.Version,
                    exactUidDetected: true,
                    HasDiagnosticUpdateKey(candidate.UpdateKeys),
                    diagnosticDetail: identity.UniqueId
                );
            }

            if (FindDiagnosticIdentity(candidate.UpdateKeys) is { } diagnosticIdentity)
            {
                // This is only a diagnostic collision. A wrong UID never becomes a positive
                // identity, but the conflicting manifest means absence cannot safely authorize
                // our 2:00 chain.
                return Unavailable(
                    "passout.compatibility.night-owl-plus.update-key-without-exact-uid",
                    candidate.Version,
                    exactUidDetected: false,
                    diagnosticUpdateKeyObserved: true,
                    diagnosticDetail: diagnosticIdentity.DiagnosticUpdateKey
                );
            }
        }

        return new NightOwlPlusCompatibilityResult(
            NightOwlPlusCompatibilityStatus.NotInstalled,
            string.Empty,
            ExactUidDetected: false,
            DiagnosticUpdateKeyObserved: false,
            "passout.compatibility.night-owl-plus.not-installed",
            string.Empty
        );
    }

    private static NightOwlPlusCompatibilityResult ResolveExactManifest(
        KnownIdentity identity,
        NightOwlPlusManifestEvidence manifest
    )
    {
        var updateKeyObserved = HasDiagnosticUpdateKey(manifest.UpdateKeys);
        if (!string.Equals(manifest.UniqueId, identity.UniqueId, StringComparison.Ordinal))
        {
            return Unavailable(
                "passout.compatibility.night-owl-plus.manifest-identity-mismatch",
                manifest.Version,
                exactUidDetected: false,
                updateKeyObserved,
                diagnosticDetail: manifest.UniqueId
            );
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            return Unavailable(
                "passout.compatibility.night-owl-plus.installed-version-unavailable",
                string.Empty,
                exactUidDetected: true,
                updateKeyObserved
            );
        }

        // Support is deliberately exact per known upstream source/manifest. A future version stays
        // fail closed until its 2:00 contract is reviewed and tested.
        if (
            string.Equals(
                manifest.Version,
                identity.SupportedVersion,
                StringComparison.Ordinal
            )
        )
        {
            return new NightOwlPlusCompatibilityResult(
                NightOwlPlusCompatibilityStatus.InstalledSupported,
                manifest.Version,
                ExactUidDetected: true,
                DiagnosticUpdateKeyObserved: updateKeyObserved,
                identity.SupportedReason,
                string.Empty
            );
        }

        return new NightOwlPlusCompatibilityResult(
            NightOwlPlusCompatibilityStatus.InstalledUnsupported,
            manifest.Version,
            ExactUidDetected: true,
            DiagnosticUpdateKeyObserved: updateKeyObserved,
            identity.UnsupportedReason,
            string.Empty
        );
    }

    private static KnownIdentity? FindIdentity(string? uniqueId)
    {
        if (string.IsNullOrWhiteSpace(uniqueId))
            return null;

        foreach (var identity in KnownIdentities)
            if (string.Equals(identity.UniqueId, uniqueId, StringComparison.Ordinal))
                return identity;
        return null;
    }

    private static KnownIdentity? FindDiagnosticIdentity(IReadOnlyList<string>? updateKeys)
    {
        if (updateKeys is null)
            return null;

        foreach (var updateKey in updateKeys)
            foreach (var identity in KnownIdentities)
                if (string.Equals(updateKey, identity.DiagnosticUpdateKey, StringComparison.Ordinal))
                    return identity;
        return null;
    }

    private static bool HasDiagnosticUpdateKey(IReadOnlyList<string>? updateKeys)
    {
        if (updateKeys is null)
            return false;

        foreach (var updateKey in updateKeys)
            if (FindDiagnosticIdentity(new[] { updateKey }) is not null)
                return true;
        return false;
    }

    private static NightOwlPlusCompatibilityResult Unavailable(
        string reason,
        string? detectedVersion = null,
        bool exactUidDetected = false,
        bool diagnosticUpdateKeyObserved = false,
        string? diagnosticDetail = null
    )
    {
        return new NightOwlPlusCompatibilityResult(
            NightOwlPlusCompatibilityStatus.FactsUnavailable,
            detectedVersion ?? string.Empty,
            exactUidDetected,
            diagnosticUpdateKeyObserved,
            reason,
            NormalizeDiagnosticDetail(diagnosticDetail)
        );
    }

    private static string Describe(Exception exception)
    {
        return NormalizeDiagnosticDetail(string.Concat(
            exception.GetType().Name,
            ": ",
            exception.Message
        ));
    }

    private static string NormalizeDiagnosticDetail(string? value)
    {
        var normalized = (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        return normalized.Length <= MaximumDiagnosticDetailLength
            ? normalized
            : normalized[..MaximumDiagnosticDetailLength];
    }

    private readonly record struct KnownIdentity(
        string UniqueId,
        string DiagnosticUpdateKey,
        string SupportedVersion,
        string SupportedReason,
        string UnsupportedReason
    );

    private static readonly KnownIdentity[] KnownIdentities =
    {
        new(
            ExactUniqueId,
            DiagnosticUpdateKey,
            ExplicitlySupportedVersion,
            "passout.compatibility.night-owl-plus.installed-supported-special-chain-suppressed",
            "passout.compatibility.night-owl-plus.installed-version-unsupported-special-chain-suppressed"
        ),
        new(
            OriginalNightOwlUniqueId,
            OriginalNightOwlDiagnosticUpdateKey,
            OriginalNightOwlSupportedVersion,
            "passout.compatibility.night-owl.installed-supported-special-chain-suppressed",
            "passout.compatibility.night-owl.installed-version-unsupported-special-chain-suppressed"
        ),
        new(
            StayUpUniqueId,
            StayUpDiagnosticUpdateKey,
            StayUpSupportedVersion,
            "passout.compatibility.stay-up.installed-supported-special-chain-suppressed",
            "passout.compatibility.stay-up.installed-version-unsupported-special-chain-suppressed"
        ),
    };
}
