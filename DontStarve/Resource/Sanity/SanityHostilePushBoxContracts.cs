#nullable enable

using System;

namespace DontStarve.Resource.Sanity;

/// <summary>
/// Resource-only contract for the shadow-creature crowd-collision box. It deliberately does not
/// contain movement or map-collision behavior; later runtime stages consume the validated values.
/// </summary>
internal sealed class SanityHostilePushBoxDefinition
{
    internal const string CoordinateSpaceId = "ActorOriginRelativeSourcePx";
    internal const int MaximumGroupIdLength = 64;

    private SanityHostilePushBoxDefinition(
        string coordinateSpace,
        SanityResourceRectangle sourcePx,
        string groupId,
        double pushForce
    )
    {
        CoordinateSpace = coordinateSpace;
        SourcePx = sourcePx;
        GroupId = groupId;
        PushForce = pushForce;
    }

    internal string CoordinateSpace { get; }

    internal SanityResourceRectangle SourcePx { get; }

    internal string GroupId { get; }

    internal double PushForce { get; }

    internal bool IsValid =>
        string.Equals(CoordinateSpace, CoordinateSpaceId, StringComparison.Ordinal)
        && IsValidSourceRectangle(SourcePx)
        && IsStableGroupId(GroupId)
        && IsValidPushForce(PushForce);

    internal static bool TryCreate(
        string? coordinateSpace,
        SanityResourceRectangle sourcePx,
        string? groupId,
        double pushForce,
        out SanityHostilePushBoxDefinition? definition,
        out string reason
    )
    {
        definition = null;
        if (!string.Equals(coordinateSpace, CoordinateSpaceId, StringComparison.Ordinal))
        {
            reason = "resource.hostile-push-box.coordinate-space-invalid";
            return false;
        }

        if (!IsValidSourceRectangle(sourcePx))
        {
            reason = "resource.hostile-push-box.source-rectangle-invalid";
            return false;
        }

        if (!IsStableGroupId(groupId))
        {
            reason = "resource.hostile-push-box.group-id-invalid";
            return false;
        }

        if (!IsValidPushForce(pushForce))
        {
            reason = "resource.hostile-push-box.push-force-invalid";
            return false;
        }

        definition = new SanityHostilePushBoxDefinition(
            CoordinateSpaceId,
            sourcePx,
            groupId!,
            pushForce
        );
        reason = "resource.hostile-push-box.available";
        return true;
    }

    internal static bool IsValidSourceRectangle(SanityResourceRectangle sourcePx)
    {
        return sourcePx.Width > 0 && sourcePx.Height > 0;
    }

    internal static bool IsStableGroupId(string? value)
    {
        if (
            value is null
            || value.Length == 0
            || value.Length > MaximumGroupIdLength
            || value[0] == '.'
            || value[0] == '-'
            || value[^1] == '.'
            || value[^1] == '-'
        )
        {
            return false;
        }

        foreach (var character in value)
        {
            var isLowercaseLetter = character is >= 'a' and <= 'z';
            var isDigit = character is >= '0' and <= '9';
            if (!isLowercaseLetter && !isDigit && character != '.' && character != '-')
                return false;
        }

        return true;
    }

    internal static bool IsValidPushForce(double value)
    {
        return double.IsFinite(value) && value > 0d;
    }
}
