#nullable enable

using System;

namespace DontStarve;

/// <summary>
/// Keeps the desktop and Android Stardew contracts separate. The Android build reports a
/// different <c>Game1.version</c> string, so accepting it on the desktop path would silently
/// widen every exact-contract patch.
/// </summary>
internal static class StardewRuntimeVersionPolicy
{
    internal const string DesktopGameVersion = "1.6.15";
    internal const string AndroidGameVersion = "1.6.15.3";

    internal static string ExpectedGameVersion(bool isAndroidPlatform) =>
        isAndroidPlatform ? AndroidGameVersion : DesktopGameVersion;

    internal static bool IsSupportedGameVersion(
        string? actualGameVersion,
        bool isAndroidPlatform
    ) => string.Equals(
        actualGameVersion,
        ExpectedGameVersion(isAndroidPlatform),
        StringComparison.Ordinal
    );
}
