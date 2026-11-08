#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Hunger;

internal sealed class HungerSaveData
{
    internal const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public Dictionary<string, HungerPlayerSaveData> Players { get; set; } =
        new(StringComparer.Ordinal);
}

internal sealed class HungerPlayerSaveData
{
    public double Current { get; set; }

    public double MaxAtSave { get; set; }
}

internal sealed class HungerCycleSaveData
{
    internal const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public Dictionary<string, HungerCyclePlayerSaveData> Players { get; set; } =
        new(StringComparer.Ordinal);
}

internal sealed class HungerCyclePlayerSaveData
{
    public bool IsStarving { get; set; }

    public long Wait { get; set; }
}
