#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DontStarve.Resource.Sanity;

namespace DontStarve.Player.Stats.Sanity.HostileShadows.Profiles;

internal sealed record ShadowMonsterProfileTextReadResult(
    bool Success,
    string RelativePath,
    string Text,
    string Reason
);

internal interface IShadowMonsterProfileFileSource
{
    ShadowMonsterProfileTextReadResult Read(string relativePath);
}

/// <summary>
/// Reads only deployment-root-relative UTF-8 JSON. The loader invokes it once during startup;
/// gameplay paths consume the immutable catalog and never perform file I/O.
/// </summary>
internal sealed class DirectoryShadowMonsterProfileFileSource
    : IShadowMonsterProfileFileSource
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    private readonly string deploymentRoot;

    internal DirectoryShadowMonsterProfileFileSource(string deploymentRoot)
    {
        this.deploymentRoot = deploymentRoot;
    }

    public ShadowMonsterProfileTextReadResult Read(string relativePath)
    {
        if (
            !SanityAssetPathPolicy.TryResolveFromDeploymentRoot(
                deploymentRoot,
                relativePath,
                out var absolutePath,
                out var pathReason
            )
        )
        {
            return Failure(relativePath, pathReason);
        }

        try
        {
            if (!File.Exists(absolutePath))
                return Failure(relativePath, "shadow-profile.file-missing");

            var bytes = File.ReadAllBytes(absolutePath);
            if (
                bytes.Length >= 3
                && bytes[0] == 0xEF
                && bytes[1] == 0xBB
                && bytes[2] == 0xBF
            )
            {
                return Failure(relativePath, "shadow-profile.utf8-bom-forbidden");
            }

            return new ShadowMonsterProfileTextReadResult(
                true,
                relativePath,
                StrictUtf8.GetString(bytes),
                "shadow-profile.file-read"
            );
        }
        catch (DecoderFallbackException)
        {
            return Failure(relativePath, "shadow-profile.utf8-invalid");
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure(relativePath, "shadow-profile.file-read-failed");
        }
    }

    private static ShadowMonsterProfileTextReadResult Failure(
        string relativePath,
        string reason
    )
    {
        return new ShadowMonsterProfileTextReadResult(false, relativePath, string.Empty, reason);
    }
}

internal sealed class ShadowMonsterProfileLoader
{
    private readonly object syncRoot = new();
    private readonly IShadowMonsterProfileFileSource source;
    private bool loaded;
    private ShadowMonsterProfileLoadResult? cachedResult;

    internal ShadowMonsterProfileLoader(IShadowMonsterProfileFileSource source)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>
    /// Loads and validates all profile and dependency documents at most once. Failures are cached
    /// too, so malformed assets cannot create retry I/O in update or draw paths.
    /// </summary>
    internal ShadowMonsterProfileLoadResult LoadAtStartupOnce()
    {
        lock (syncRoot)
        {
            if (loaded)
                return cachedResult!;

            cachedResult = LoadCore();
            loaded = true;
            return cachedResult;
        }
    }

    private ShadowMonsterProfileLoadResult LoadCore()
    {
        var issues = new List<ShadowMonsterProfileIssue>();
        var profileDocuments = new List<ShadowMonsterProfileDocument>();

        var schema = ReadRequired(ShadowMonsterProfilePaths.Schema, issues);
        foreach (var profilePath in ShadowMonsterProfilePaths.ProfileFiles.Values)
        {
            var profile = ReadRequired(profilePath, issues);
            if (profile is not null)
                profileDocuments.Add(profile);
        }

        var animations = ReadRequired(ShadowMonsterProfilePaths.Animations, issues);
        var bindings = ReadRequired(ShadowMonsterProfilePaths.ResourceBindings, issues);
        var audioCues = ReadRequired(ShadowMonsterProfilePaths.AudioCues, issues);
        if (
            issues.Count > 0
            || schema is null
            || animations is null
            || bindings is null
            || audioCues is null
        )
        {
            return new ShadowMonsterProfileLoadResult(null, issues, Array.Empty<ShadowMonsterProfileDiagnostic>());
        }

        if (
            !ShadowMonsterProfileReferenceCatalog.TryCreate(
                animations.Json,
                bindings.Json,
                audioCues.Json,
                out var references,
                out var referenceIssues
            )
        )
        {
            return new ShadowMonsterProfileLoadResult(
                null,
                referenceIssues,
                Array.Empty<ShadowMonsterProfileDiagnostic>()
            );
        }

        var validation = ShadowMonsterProfileValidator.Validate(
            schema.Json,
            profileDocuments,
            references!
        );
        return ShadowMonsterProfileLoadResult.FromValidation(validation);
    }

    private ShadowMonsterProfileDocument? ReadRequired(
        string relativePath,
        List<ShadowMonsterProfileIssue> issues
    )
    {
        var result = source.Read(relativePath);
        if (result.Success)
            return new ShadowMonsterProfileDocument(relativePath, result.Text);

        issues.Add(
            new ShadowMonsterProfileIssue(
                "shadow-profile.file-unavailable",
                relativePath,
                "$",
                result.Reason
            )
        );
        return null;
    }
}
