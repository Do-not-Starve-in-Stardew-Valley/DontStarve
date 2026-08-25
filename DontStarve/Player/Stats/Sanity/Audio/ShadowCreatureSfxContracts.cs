#nullable enable

using System;

namespace DontStarve.Player.Stats.Sanity.Audio;

internal enum ShadowCreatureSpecies
{
    CreeperFear,
    Terrorbeak,
}

internal enum ShadowCreatureSfxCue
{
    Idle,
    Chase,
    Taunt,
    AttackDull,
    AttackSharp,
    Attack,
    Hurt,
    Death,
}

internal enum ShadowCreatureSfxPlaybackState
{
    Playing,
    Stopped,
}

internal enum ShadowCreatureSfxRequestStatus
{
    Started,
    DroppedPriority,
    DroppedDuplicate,
    SkippedSilent,
    MissingPool,
    Failed,
}

internal readonly record struct ShadowCreatureSfxRequestResult(
    ShadowCreatureSfxRequestStatus Status,
    ShadowCreatureSfxCue Cue,
    string Reason
)
{
    internal bool Accepted => Status is
        ShadowCreatureSfxRequestStatus.Started
        or ShadowCreatureSfxRequestStatus.SkippedSilent;
}

internal readonly record struct ShadowCreatureSfxOwnerKey
{
    internal ShadowCreatureSfxOwnerKey(string sessionId, string entityId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("A session ID is required.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(entityId))
            throw new ArgumentException("An entity ID is required.", nameof(entityId));
        SessionId = sessionId;
        EntityId = entityId;
    }

    internal string SessionId { get; }

    internal string EntityId { get; }

    public override string ToString() => string.Concat(SessionId, ":", EntityId);
}

internal readonly record struct ShadowCreatureSfxSpatial(
    bool IsAudible,
    float Volume,
    float Pan,
    float DistanceFactor = 1f,
    bool IsInAudibleRadius = true
)
{
    internal const double MaximumDistanceTiles = 30d;

    internal static ShadowCreatureSfxSpatial AtOrigin(float soundVolume)
    {
        var volume = Clamp(soundVolume);
        return new ShadowCreatureSfxSpatial(
            volume > 0f,
            volume,
            0f,
            1f,
            true
        );
    }

    internal static ShadowCreatureSfxSpatial FromDelta(
        double dx,
        double dy,
        float soundVolume
    )
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy))
            return new ShadowCreatureSfxSpatial(false, 0f, 0f, 0f, false);

        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        var baseVolume = Clamp(soundVolume);
        if (distance > MaximumDistanceTiles)
            return new ShadowCreatureSfxSpatial(false, 0f, 0f, 0f, false);

        var attenuation = Math.Max(0d, 1d - (distance / MaximumDistanceTiles));
        var pan = distance <= double.Epsilon
            ? 0d
            : Math.Clamp(dx / distance, -1d, 1d);
        return new ShadowCreatureSfxSpatial(
            true,
            Clamp((float)(baseVolume * attenuation)),
            Clamp((float)pan, -1f, 1f),
            Clamp((float)attenuation),
            true
        );
    }

    private static float Clamp(float value, float minimum = 0f, float maximum = 1f)
    {
        if (!float.IsFinite(value))
            return 0f;
        return Math.Clamp(value, minimum, maximum);
    }
}

/// <summary>Borrowed SoundEffect facade. The owner lane owns only instances it creates.</summary>
internal interface IShadowCreatureSfxEffect
{
    string ResourceId { get; }

    IShadowCreatureSfxInstance CreateInstance();
}

internal interface IShadowCreatureSfxInstance : IDisposable
{
    ShadowCreatureSfxPlaybackState State { get; }

    float Volume { get; set; }

    float Pan { get; set; }

    bool IsLooped { get; set; }

    void Play();

    void Stop();
}

/// <summary>Receives bounded diagnostics without coupling the pure SFX layer to SMAPI logging.</summary>
internal interface IShadowCreatureSfxDiagnostics
{
    void Report(string code, string reason);
}

internal interface IShadowCreatureSfxRandom
{
    int NextIndex(int exclusiveUpperBound);

    double NextDouble(double inclusiveMinimum, double exclusiveMaximum);
}

internal sealed class SystemShadowCreatureSfxRandom : IShadowCreatureSfxRandom
{
    private readonly Random random = new();

    public int NextIndex(int exclusiveUpperBound)
    {
        if (exclusiveUpperBound <= 0)
            throw new ArgumentOutOfRangeException(nameof(exclusiveUpperBound));
        return random.Next(exclusiveUpperBound);
    }

    public double NextDouble(double inclusiveMinimum, double exclusiveMaximum)
    {
        if (!double.IsFinite(inclusiveMinimum)
            || !double.IsFinite(exclusiveMaximum)
            || exclusiveMaximum < inclusiveMinimum)
        {
            throw new ArgumentOutOfRangeException(nameof(exclusiveMaximum));
        }
        return inclusiveMinimum + ((exclusiveMaximum - inclusiveMinimum) * random.NextDouble());
    }
}
