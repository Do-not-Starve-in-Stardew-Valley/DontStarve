using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

/// <summary>
/// Adjusts sanity based on equipped clothing and accessories.
/// </summary>
internal class Wearing : ITimeRelatedBehavior
{
    private const string SAVE_KEY = "DontStarve.Sanity.Wearing";
    private Dictionary<string, double> hatSanity = null!;
    private Dictionary<string, double> shirtSanity = null!;
    private Dictionary<string, double> pantsSanity = null!;
    private Dictionary<string, double> bootsSanity = null!;
    private Dictionary<string, double> ringSanity = null!;
    private Dictionary<string, double> trinketSanity = null!;
    private long wait;

    public void Init(IModHelper helper)
    {
        hatSanity = helper.ModContent.Load<Dictionary<string, double>>("Asset/Sanity/hat.json");
        bootsSanity = helper.ModContent.Load<Dictionary<string, double>>("Asset/Sanity/boots.json");
        ringSanity = helper.ModContent.Load<Dictionary<string, double>>("Asset/Sanity/ring.json");
        shirtSanity = helper.ModContent.Load<Dictionary<string, double>>("Asset/Sanity/shirt.json");
        pantsSanity = helper.ModContent.Load<Dictionary<string, double>>("Asset/Sanity/pants.json");
        trinketSanity = helper.ModContent.Load<Dictionary<string, double>>(
            "Asset/Sanity/trinket.json"
        );
    }

    public void Update(long _)
    {
        if (wait > 0)
        {
            wait--;
            return;
        }

        var player = Game1.player;
        if (player == null)
            return;

        var sanity = 0.0;

        var hat = player.hat.Value;
        if (hat != null)
            if (hatSanity.TryGetValue(hat.ItemId, out var value))
                sanity += value;

        var shirt = player.shirtItem.Value;
        if (shirt != null)
            if (shirtSanity.TryGetValue(shirt.ItemId, out var value))
                sanity += value;

        var pants = player.pantsItem.Value;
        if (pants != null)
            if (pantsSanity.TryGetValue(pants.ItemId, out var value))
                sanity += value;

        var boots = player.boots.Value;
        if (boots != null)
            if (bootsSanity.TryGetValue(boots.ItemId, out var value))
                sanity += value;

        var leftRing = player.leftRing.Value;
        if (leftRing != null)
            if (ringSanity.TryGetValue(leftRing.ItemId, out var value))
                sanity += value;

        var rightRing = player.rightRing.Value;
        if (rightRing != null)
            if (ringSanity.TryGetValue(rightRing.ItemId, out var value))
                sanity += value;

        var trinket = player.trinketItems.FirstOrDefault();
        if (trinket != null)
            if (trinketSanity.TryGetValue(trinket.ItemId, out var value))
                sanity += value;

        player.SetSanity(player.GetSanity() + sanity);
    }

    public void Sync(long time, long delta)
    {
        if (delta < 0)
            wait += -delta;
        else
            for (var i = 0; i <= delta; i++)
                Update(time);
    }

    public void Load(IModHelper helper)
    {
        var data = helper.Data.ReadSaveData<WearingData>(SAVE_KEY);
        wait = data?.Wait ?? 0;
    }

    public void Save(IModHelper helper)
    {
        helper.Data.WriteSaveData(SAVE_KEY, new WearingData { Wait = wait });
    }
}

internal class WearingData
{
    public long Wait { get; init; }
}
