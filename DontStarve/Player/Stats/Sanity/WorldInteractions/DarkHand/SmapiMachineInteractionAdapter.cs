#nullable enable

using System;
using StardewValley;
using StardewValley.GameData.Machines;

namespace DontStarve.Player.Stats.Sanity.WorldInteractions.DarkHand;

/// <summary>
/// Read-only Stardew 1.6 public-surface adapter for the task-11 machine fact contract. This type
/// never writes machine NetFields and never manufactures an authority revision from their values.
/// </summary>
internal sealed class SmapiMachineInteractionAdapter
{
    private readonly MachineTargetCatalog catalog;
    private readonly MachineRuntimeEvidence evidence;

    internal SmapiMachineInteractionAdapter(
        MachineTargetCatalog catalog,
        MachineRuntimeEvidence evidence
    )
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.evidence = evidence;
    }

    internal MachineInteractionSnapshot Capture(
        StardewValley.Object machine,
        string targetId,
        string locationId,
        long authorityRevision
    )
    {
        ArgumentNullException.ThrowIfNull(machine);
        var lastRuleId = machine.lastOutputRuleId.Value ?? string.Empty;
        var data = machine.GetMachineData();
        var observation = new MachineReadObservation(
            targetId,
            locationId,
            machine.QualifiedItemId,
            authorityRevision,
            lastRuleId,
            machine.MinutesUntilReady,
            machine.readyForHarvest.Value,
            machine.showNextIndex.Value,
            ReadItem(machine.heldObject.Value),
            ReadItem(machine.lastInputItem.Value),
            ReadData(data, lastRuleId)
        );
        return MachineInteractionClassifier.Capture(observation, catalog, evidence);
    }

    private static MachineItemFacts? ReadItem(Item? item)
    {
        if (item is null)
            return null;
        return new MachineItemFacts(
            item.QualifiedItemId,
            item.Stack,
            item.Quality,
            item is StardewValley.Object objectItem && objectItem.IsRecipe,
            IsOrdinaryMachineContent(item)
                ? MachineContentProtection.Ordinary
                : MachineContentProtection.ProtectedOrQuest
        );
    }

    private static bool IsOrdinaryMachineContent(Item item)
    {
        return item is StardewValley.Object objectItem
            && !objectItem.IsRecipe
            && !objectItem.questItem.Value
            && objectItem.canBeTrashed()
            && objectItem.QualifiedItemId.StartsWith("(O)", StringComparison.Ordinal);
    }

    private static MachineDataFacts? ReadData(MachineData? data, string lastRuleId)
    {
        if (data is null)
            return null;

        MachineOutputRule? activeRule = null;
        if (data.OutputRules is not null)
        {
            foreach (var rule in data.OutputRules)
            {
                if (string.Equals(rule.Id, lastRuleId, StringComparison.Ordinal))
                {
                    activeRule = rule;
                    break;
                }
            }
        }

        return new MachineDataFacts(
            data.IsIncubator,
            data.OnlyCompleteOvernight,
            data.AdditionalConsumedItems is { Count: > 0 },
            !string.IsNullOrWhiteSpace(data.InteractMethod),
            !string.IsNullOrWhiteSpace(data.ClearContentsOvernightCondition),
            ReadRule(activeRule)
        );
    }

    private static MachineRuleFacts? ReadRule(MachineOutputRule? rule)
    {
        if (rule is null)
            return null;

        var triggers = MachineTriggerKinds.None;
        var maximumRequiredInput = 0;
        if (rule.Triggers is not null)
        {
            foreach (var trigger in rule.Triggers)
            {
                triggers |= trigger.Trigger switch
                {
                    MachineOutputTrigger.ItemPlacedInMachine =>
                        MachineTriggerKinds.ItemPlacedInMachine,
                    MachineOutputTrigger.OutputCollected => MachineTriggerKinds.OutputCollected,
                    MachineOutputTrigger.MachinePutDown => MachineTriggerKinds.MachinePutDown,
                    MachineOutputTrigger.DayUpdate => MachineTriggerKinds.DayUpdate,
                    _ => MachineTriggerKinds.None,
                };
                if (
                    trigger.Trigger == MachineOutputTrigger.ItemPlacedInMachine
                    && trigger.RequiredCount > maximumRequiredInput
                )
                {
                    maximumRequiredInput = trigger.RequiredCount;
                }
            }
        }

        var hasCustomOutputMethod = false;
        if (rule.OutputItem is not null)
        {
            foreach (var output in rule.OutputItem)
            {
                if (!string.IsNullOrWhiteSpace(output.OutputMethod))
                {
                    hasCustomOutputMethod = true;
                    break;
                }
            }
        }

        return new MachineRuleFacts(
            rule.Id ?? string.Empty,
            triggers,
            maximumRequiredInput,
            rule.MinutesUntilReady,
            rule.DaysUntilReady,
            rule.RecalculateOnCollect,
            hasCustomOutputMethod
        );
    }
}
