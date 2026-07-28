#nullable enable

using System;
using System.IO;

namespace DontStarve.Resource.Sanity;

internal static class SanityAssetPathPolicy
{
    private const string RequiredPrefix = "Asset/Sanity/";

    internal static bool TryNormalize(
        string path,
        out string normalizedPath,
        out string reason
    )
    {
        normalizedPath = string.Empty;
        reason = "path.invalid";
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "path.empty";
            return false;
        }

        foreach (var character in path)
        {
            if (character > 0x7f)
            {
                reason = "path.non-ascii";
                return false;
            }

            var isAsciiLetterOrDigit = (character >= 'A' && character <= 'Z')
                || (character >= 'a' && character <= 'z')
                || (character >= '0' && character <= '9');
            if (
                !isAsciiLetterOrDigit
                && character != '/'
                && character != '-'
                && character != '_'
                && character != '.'
            )
            {
                reason = character == '\\'
                    ? "path.backslash"
                    : "path.invalid-character";
                return false;
            }
        }

        if (!path.StartsWith(RequiredPrefix, StringComparison.Ordinal))
        {
            reason = path.StartsWith("DontStarve/", StringComparison.OrdinalIgnoreCase)
                ? "path.source-prefix"
                : "path.outside-sanity-root";
            return false;
        }

        var segments = path.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                reason = "path.traversal";
                return false;
            }

            if (
                string.Equals(segment, "references", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "DontStarve", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "TestPackage", StringComparison.OrdinalIgnoreCase)
            )
            {
                reason = "path.forbidden-segment";
                return false;
            }
        }

        normalizedPath = path;
        reason = "path.valid";
        return true;
    }

    internal static bool TryResolveFromDeploymentRoot(
        string deploymentRoot,
        string path,
        out string absolutePath,
        out string reason
    )
    {
        absolutePath = string.Empty;
        if (!TryNormalize(path, out var normalizedPath, out reason))
            return false;

        if (string.IsNullOrWhiteSpace(deploymentRoot))
        {
            reason = "path.root-empty";
            return false;
        }

        try
        {
            // 运行时传 helper.DirectoryPath；仓库验证传 repoRoot/DontStarve。两端只共享这一种部署根相对语义。
            var fullRoot = Path.GetFullPath(deploymentRoot);
            var relativePlatformPath = normalizedPath.Replace('/', Path.DirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePlatformPath));
            var rootWithSeparator = fullRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            ) + Path.DirectorySeparatorChar;

            if (!candidate.StartsWith(rootWithSeparator, PathComparison))
            {
                reason = "path.outside-deployment-root";
                return false;
            }

            absolutePath = candidate;
            reason = "path.resolved";
            return true;
        }
        catch (ArgumentException)
        {
            reason = "path.resolve-failed";
            return false;
        }
        catch (NotSupportedException)
        {
            reason = "path.resolve-failed";
            return false;
        }
        catch (PathTooLongException)
        {
            reason = "path.resolve-failed";
            return false;
        }
    }

    internal static bool TryResolveFromRepositoryRoot(
        string repositoryRoot,
        string path,
        out string absolutePath,
        out string reason
    )
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            absolutePath = string.Empty;
            reason = "path.root-empty";
            return false;
        }

        return TryResolveFromDeploymentRoot(
            Path.Combine(repositoryRoot, "DontStarve"),
            path,
            out absolutePath,
            out reason
        );
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
