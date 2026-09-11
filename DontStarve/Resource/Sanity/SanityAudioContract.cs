#nullable enable

namespace DontStarve.Resource.Sanity;

/// <summary>
/// Shared immutable facts for the shipped Sanity audio contract. Runtime resource limits
/// must remain aligned with metadata validation so a valid catalog cannot fail at playback.
/// </summary>
internal static class SanityAudioContract
{
    internal const int PhysicalClipCount = 144;

    internal const string DarknessWarningLifecyclePolicy =
        "CancelableOneShot;ReleaseOnWorldTitleDispose";
}
