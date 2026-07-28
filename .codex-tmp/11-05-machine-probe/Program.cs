using Microsoft.Xna.Framework.Content;
using StardewValley.GameData.BigCraftables;
using StardewValley.GameData.Machines;

const string ContentRoot = @"F:\Games\GamePlats\Steam\steamapps\common\Stardew Valley\Content";
using var content = new ContentManager(new EmptyServices(), ContentRoot);
var bigCraftables = content.Load<Dictionary<string, BigCraftableData>>("Data/BigCraftables");
var machines = content.Load<Dictionary<string, MachineData>>("Data/Machines");
var requested = new HashSet<string>(args, StringComparer.Ordinal);

Console.WriteLine($"machine-count={machines.Count}");
foreach (var pair in machines.OrderBy(pair => pair.Key, StringComparer.Ordinal))
{
    if (requested.Count > 0 && !requested.Contains(pair.Key))
        continue;
    var id = pair.Key.StartsWith("(BC)", StringComparison.Ordinal)
        ? pair.Key[4..]
        : pair.Key;
    bigCraftables.TryGetValue(id, out var bigCraftable);
    var machine = pair.Value;
    Console.WriteLine(
        string.Join(
            '|',
            "M",
            pair.Key,
            bigCraftable?.Name ?? "<missing-big-craftable>",
            $"input={machine.HasInput}",
            $"output={machine.HasOutput}",
            $"incubator={machine.IsIncubator}",
            $"overnight={machine.OnlyCompleteOvernight}",
            $"clear={machine.ClearContentsOvernightCondition ?? "<null>"}",
            $"additional={machine.AdditionalConsumedItems?.Count ?? 0}",
            $"allowFull={machine.AllowLoadWhenFull}",
            $"interact={machine.InteractMethod ?? "<null>"}",
            $"rules={machine.OutputRules?.Count ?? 0}"
        )
    );

    foreach (var rule in machine.OutputRules ?? new List<MachineOutputRule>())
    {
        var triggers = string.Join(
            ';',
            (rule.Triggers ?? new List<MachineOutputTriggerRule>()).Select(trigger =>
                $"{trigger.Trigger}:{trigger.RequiredCount}:{trigger.RequiredItemId ?? "-"}:{string.Join(',', trigger.RequiredTags ?? new List<string>())}:{trigger.Condition ?? "-"}"
            )
        );
        var outputs = string.Join(
            ';',
            (rule.OutputItem ?? new List<MachineItemOutput>()).Select(output =>
                $"{output.ItemId ?? "-"}:{output.MinStack}-{output.MaxStack}:method={output.OutputMethod ?? "-"}:condition={output.Condition ?? "-"}"
            )
        );
        Console.WriteLine(
            string.Join(
                '|',
                "R",
                rule.Id ?? "<null>",
                $"minutes={rule.MinutesUntilReady}",
                $"days={rule.DaysUntilReady}",
                $"recalc={rule.RecalculateOnCollect}",
                $"first={rule.UseFirstValidOutput}",
                $"triggers={triggers}",
                $"outputs={outputs}"
            )
        );
    }
}

Console.WriteLine("anvil-candidates");
foreach (var pair in bigCraftables.Where(pair =>
             pair.Key.Contains("Anvil", StringComparison.OrdinalIgnoreCase)
             || pair.Value.Name.Contains("Anvil", StringComparison.OrdinalIgnoreCase)
         ).OrderBy(pair => pair.Key, StringComparer.Ordinal))
{
    Console.WriteLine($"(BC){pair.Key}|{pair.Value.Name}|sprite={pair.Value.SpriteIndex}");
}

sealed class EmptyServices : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}
