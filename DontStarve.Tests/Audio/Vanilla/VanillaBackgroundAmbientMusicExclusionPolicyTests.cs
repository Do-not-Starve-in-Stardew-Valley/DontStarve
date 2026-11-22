using DontStarve.Audio.Vanilla;
using Xunit;

namespace DontStarve.Tests.Audio.Vanilla;

public sealed class VanillaBackgroundAmbientMusicExclusionPolicyTests
{
    [Theory]
    [InlineData("rain", false, false)]
    [InlineData("RAIN", false, false)]
    [InlineData("winter_day_ambient", true, false)]
    [InlineData("spring_night_ambient", false, true)]
    [InlineData("modded-day-ambience", true, false)]
    public void Clearly_vanilla_background_ambient_tracks_are_preserved(
        string cueName,
        bool outdoorsAmbience,
        bool nightAmbience
    )
    {
        Assert.True(
            VanillaBackgroundAmbientMusicExclusionPolicy.ShouldPreserve(
                cueName,
                outdoorsAmbience,
                nightAmbience
            )
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("none")]
    [InlineData("silence")]
    [InlineData("forest_theme")]
    [InlineData("some_ambient_song")]
    public void Ordinary_or_unclassified_tracks_are_not_preserved(string? cueName)
    {
        Assert.False(
            VanillaBackgroundAmbientMusicExclusionPolicy.ShouldPreserve(
                cueName,
                isPlayingOutdoorsAmbience: false,
                isPlayingNightAmbience: false
            )
        );
    }

    [Theory]
    [InlineData("none", true, false)]
    [InlineData("silence", false, true)]
    public void Cleared_requests_are_not_preserved_even_if_old_ambient_flags_linger(
        string cueName,
        bool outdoorsAmbience,
        bool nightAmbience
    )
    {
        Assert.False(
            VanillaBackgroundAmbientMusicExclusionPolicy.ShouldPreserve(
                cueName,
                outdoorsAmbience,
                nightAmbience
            )
        );
    }

    [Fact]
    public void Suppressed_update_is_allowed_when_requested_track_is_protected_and_current_is_missing()
    {
        Assert.True(
            VanillaBackgroundAmbientMusicExclusionPolicy.ShouldAllowSuppressedUpdate(
                currentCueName: null,
                requestedCueName: "rain",
                isPlayingOutdoorsAmbience: false,
                isPlayingNightAmbience: false
            )
        );
    }

    [Fact]
    public void Suppressed_update_is_allowed_for_a_protected_current_and_requested_track()
    {
        Assert.True(
            VanillaBackgroundAmbientMusicExclusionPolicy.ShouldAllowSuppressedUpdate(
                currentCueName: "winter_day_ambient",
                requestedCueName: "winter_day_ambient",
                isPlayingOutdoorsAmbience: true,
                isPlayingNightAmbience: false
            )
        );
    }

    [Fact]
    public void Suppressed_update_remains_blocked_when_an_ambient_current_track_is_replaced_by_ordinary_music()
    {
        Assert.False(
            VanillaBackgroundAmbientMusicExclusionPolicy.ShouldAllowSuppressedUpdate(
                currentCueName: "winter_day_ambient",
                requestedCueName: "forest_theme",
                isPlayingOutdoorsAmbience: false,
                isPlayingNightAmbience: false
            )
        );
    }

    [Fact]
    public void Suppressed_update_allows_switching_between_explicit_day_ambient_tracks()
    {
        Assert.True(
            VanillaBackgroundAmbientMusicExclusionPolicy.ShouldAllowSuppressedUpdate(
                currentCueName: "spring_day_ambient",
                requestedCueName: "winter_day_ambient",
                isPlayingOutdoorsAmbience: true,
                isPlayingNightAmbience: false
            )
        );
    }

    [Theory]
    [InlineData("forest_theme", "forest_theme", true, false, false)]
    [InlineData("forest_theme", "winter_day_ambient", true, false, false)]
    [InlineData("winter_day_ambient", "spring_day_ambient", true, false, true)]
    [InlineData(null, "custom_day_ambient", true, false, false)]
    [InlineData("custom_day_ambient", "custom_day_ambient", true, false, false)]
    public void Suppressed_update_does_not_treat_stale_ambient_flags_as_music_permission(
        string? currentCueName,
        string requestedCueName,
        bool outdoorsAmbience,
        bool nightAmbience,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            VanillaBackgroundAmbientMusicExclusionPolicy.ShouldAllowSuppressedUpdate(
                currentCueName,
                requestedCueName,
                outdoorsAmbience,
                nightAmbience
            )
        );
    }

    [Theory]
    [InlineData("rain", "forest_theme")]
    [InlineData("rain", "none")]
    [InlineData(null, "forest_theme")]
    public void Suppressed_update_remains_blocked_for_an_ordinary_or_none_request(
        string? currentCueName,
        string requestedCueName
    )
    {
        Assert.False(
            VanillaBackgroundAmbientMusicExclusionPolicy.ShouldAllowSuppressedUpdate(
                currentCueName,
                requestedCueName,
                isPlayingOutdoorsAmbience: false,
                isPlayingNightAmbience: false
            )
        );
    }
}
