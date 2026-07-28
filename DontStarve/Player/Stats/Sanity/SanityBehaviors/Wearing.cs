using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;

namespace DontStarve.Player.Stats.Sanity.SanityBehaviors;

/// <summary>
/// 根据当前装备表每分钟调整理智，正值恢复、负值扣减。
/// </summary>
internal class Wearing : ITimeRelatedBehavior
{
    private const string SAVE_KEY = "DontStarve.Sanity.Wearing";
    private static Dictionary<string, double> hatSanity = new();
    private static Dictionary<string, double> shirtSanity = new();
    private static Dictionary<string, double> pantsSanity = new();
    private static Dictionary<string, double> bootsSanity = new();
    private static Dictionary<string, double> ringSanity = new();
    private static Dictionary<string, double> trinketSanity = new();
    private long wait;

    public void Init(IModHelper helper)
    {
        // 装备表全部用 ItemId 匹配；新增装备优先扩 JSON，不要把单件装备写死到这里。
        hatSanity = helper.ModContent.Load<Dictionary<string, double>>("Asset/Sanity/hat.json");
        bootsSanity = helper.ModContent.Load<Dictionary<string, double>>("Asset/Sanity/boots.json");
        ringSanity = helper.ModContent.Load<Dictionary<string, double>>("Asset/Sanity/ring.json");
        shirtSanity = helper.ModContent.Load<Dictionary<string, double>>("Asset/Sanity/shirt.json");
        pantsSanity = helper.ModContent.Load<Dictionary<string, double>>("Asset/Sanity/pants.json");
        trinketSanity = helper.ModContent.Load<Dictionary<string, double>>(
            "Asset/Sanity/trinket.json"
        );
    }

    internal static bool TryGetPerMinuteSanity(Item item, out double value)
    {
        value = 0d;
        if (item is null)
            return false;

        var qualifiedId = item.QualifiedItemId;
        Dictionary<string, double> values = qualifiedId switch
        {
            var id when id.StartsWith("(H)", System.StringComparison.Ordinal) =>
                hatSanity,
            var id when id.StartsWith("(S)", System.StringComparison.Ordinal) =>
                shirtSanity,
            var id when id.StartsWith("(P)", System.StringComparison.Ordinal) =>
                pantsSanity,
            var id when id.StartsWith("(B)", System.StringComparison.Ordinal) =>
                bootsSanity,
            // Stardew 1.6.15 Ring derives from Object and reports "(O)", not a private ring
            // qualifier. The ring table still filters by exact ItemId, so ordinary objects do not
            // gain equipment text.
            var id when id.StartsWith("(O)", System.StringComparison.Ordinal) =>
                ringSanity,
            var id when id.StartsWith("(TR)", System.StringComparison.Ordinal) =>
                trinketSanity,
            _ => null,
        };
        return values is not null && values.TryGetValue(item.ItemId, out value);
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

        // 每分钟重新读取当前穿戴状态，不缓存装备对象，避免换装后继续沿用旧效果。
        var hat = player.hat.Value;
        var shirt = player.shirtItem.Value;
        var pants = player.pantsItem.Value;
        var boots = player.boots.Value;
        var leftRing = player.leftRing.Value;
        var rightRing = player.rightRing.Value;

        // 当前 Stardew 1.6 inventory 把 MaximumTrinkets 固定为 1；只读取 slot 0，
        // 不把 NetList 的可扩展形状误写成已经支持多个同时生效的 trinket 槽。
        var trinket = player.trinketItems.Count > 0 ? player.trinketItems[0] : null;
        var sanity = SanityBehaviorRules.CalculateEquipmentDelta(
            new EquipmentSanityLoadout(
                hat?.ItemId,
                shirt?.ItemId,
                pants?.ItemId,
                boots?.ItemId,
                leftRing?.ItemId,
                rightRing?.ItemId,
                trinket?.ItemId
            ),
            hatSanity,
            shirtSanity,
            pantsSanity,
            bootsSanity,
            ringSanity,
            trinketSanity
        );

        player.ChangeSanity(sanity, SanityChangeSource.Equipment);
    }

    public void Sync(long _, long delta)
    {
        if (delta < 0)
            wait += -delta;
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
