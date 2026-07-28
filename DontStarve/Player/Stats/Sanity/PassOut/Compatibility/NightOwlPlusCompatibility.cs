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
/// Resolves Night Owl Plus once from loaded-manifest facts. The exact UID is the only positive
/// identity; the Nexus key can only expose an inconsistent manifest and never grants support.
/// </summary>
internal static class NightOwlPlusCompatibility
{
    internal const string CapabilityId = "passout.compatibility.night-owl-plus";
    internal const string ExactUniqueId = "MaximillianRW.NightOwlPlus";
    internal const string DiagnosticUpdateKey = "Nexus:29714";
    internal const string ExplicitlySupportedVersion = "1.0.0";
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

        NightOwlPlusManifestEvidence? exact;
        try
        {
            exact = exactUidLookup(ExactUniqueId);
        }
        catch (Exception exception)
        {
            return Unavailable(
                "passout.compatibility.night-owl-plus.registry-exact-lookup-failed",
                diagnosticDetail: Describe(exception)
            );
        }

        if (exact is { } manifest)
            return ResolveExactManifest(manifest);

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
            if (string.Equals(candidate.UniqueId, ExactUniqueId, StringComparison.Ordinal))
            {
                return Unavailable(
                    "passout.compatibility.night-owl-plus.registry-results-inconsistent",
                    candidate.Version,
                    exactUidDetected: true,
                    HasDiagnosticUpdateKey(candidate.UpdateKeys),
                    diagnosticDetail: candidate.UniqueId
                );
            }

            if (HasDiagnosticUpdateKey(candidate.UpdateKeys))
            {
                // This is only a diagnostic collision. A wrong UID never becomes Night Owl Plus,
                // but the conflicting manifest means absence cannot safely authorize our 2:00 chain.
                return Unavailable(
                    "passout.compatibility.night-owl-plus.update-key-without-exact-uid",
                    candidate.Version,
                    exactUidDetected: false,
                    diagnosticUpdateKeyObserved: true,
                    diagnosticDetail: candidate.UniqueId
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
        NightOwlPlusManifestEvidence manifest
    )
    {
        var updateKeyObserved = HasDiagnosticUpdateKey(manifest.UpdateKeys);
        if (!string.Equals(manifest.UniqueId, ExactUniqueId, StringComparison.Ordinal))
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

        // Support is deliberately exact: 1.0.0 is the reviewed public manifest/source behavior.
        // A future version stays fail closed until its 2:00 contract is reviewed and tested.
        if (
            string.Equals(
                manifest.Version,
                ExplicitlySupportedVersion,
                StringComparison.Ordinal
            )
        )
        {
            return new NightOwlPlusCompatibilityResult(
                NightOwlPlusCompatibilityStatus.InstalledSupported,
                manifest.Version,
                ExactUidDetected: true,
                DiagnosticUpdateKeyObserved: updateKeyObserved,
                "passout.compatibility.night-owl-plus.installed-supported-special-chain-suppressed",
                string.Empty
            );
        }

        return new NightOwlPlusCompatibilityResult(
            NightOwlPlusCompatibilityStatus.InstalledUnsupported,
            manifest.Version,
            ExactUidDetected: true,
            DiagnosticUpdateKeyObserved: updateKeyObserved,
            "passout.compatibility.night-owl-plus.installed-version-unsupported-special-chain-suppressed",
            string.Empty
        );
    }

    private static bool HasDiagnosticUpdateKey(IReadOnlyList<string>? updateKeys)
    {
        if (updateKeys is null)
            return false;

        foreach (var updateKey in updateKeys)
        {
            if (string.Equals(updateKey, DiagnosticUpdateKey, StringComparison.Ordinal))
                return true;
        }
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
}
