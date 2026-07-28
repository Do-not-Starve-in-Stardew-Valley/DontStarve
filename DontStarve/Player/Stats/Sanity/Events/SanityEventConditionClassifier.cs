#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace DontStarve.Player.Stats.Sanity.Events;

internal enum SanityEventClassification
{
    NonFriendship,
    Friendship,
    Unknown,
}

internal readonly record struct SanityEventClassificationResult(
    SanityEventClassification Classification,
    string Reason
);

/// <summary>
/// Classifies only event-key conditions whose relationship semantics are stable in the
/// Stardew 1.6 event API. Unrelated conditions are intentionally ignored because the
/// original game has already accepted them before the gate runs.
/// </summary>
internal static class SanityEventConditionClassifier
{
    private static readonly HashSet<string> RelationshipQueries = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "PLAYER_FRIENDSHIP_POINTS",
        "PLAYER_HEARTS",
        "PLAYER_NPC_RELATIONSHIP",
    };

    private static readonly HashSet<string> RelationshipTypes = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "friendly",
        "roommate",
        "dating",
        "engaged",
        "married",
        "divorced",
    };

    internal static SanityEventClassificationResult Classify(
        IReadOnlyList<string>? splitPreconditions,
        SanityEventClassification? configuredOverride = null
    )
    {
        if (configuredOverride.HasValue)
        {
            return configuredOverride.Value is SanityEventClassification.Friendship
                or SanityEventClassification.NonFriendship
                ? new SanityEventClassificationResult(
                    configuredOverride.Value,
                    "event-classification-override"
                )
                : Unknown("event-classification-override-is-invalid");
        }

        if (splitPreconditions is null || splitPreconditions.Count == 0)
            return Unknown("event-preconditions-are-missing");
        if (string.IsNullOrWhiteSpace(splitPreconditions[0]))
            return Unknown("event-id-is-missing");

        var foundRelationship = false;
        var relationshipReason = "known-relationship-condition";
        for (var index = 1; index < splitPreconditions.Count; index++)
        {
            var condition = splitPreconditions[index];
            if (!TryTokenize(condition, out var tokens) || tokens.Count == 0)
                return Unknown("event-condition-tokenization-failed");

            var command = tokens[0];
            if (command.StartsWith("!", StringComparison.Ordinal))
                command = command[1..];

            if (
                string.Equals(command, "f", StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    command,
                    "Friendship",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                if (!IsValidFriendshipCondition(tokens))
                    return Unknown("friendship-condition-is-malformed");
                foundRelationship = true;
                relationshipReason = "known-relationship-condition";
                continue;
            }

            if (
                !string.Equals(command, "G", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    command,
                    "GameStateQuery",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                continue;
            }

            var query = ClassifyGameStateQuery(tokens);
            if (query.Classification == SanityEventClassification.Unknown)
                return query;
            if (query.Classification == SanityEventClassification.Friendship)
            {
                foundRelationship = true;
                relationshipReason = query.Reason;
            }
        }

        return foundRelationship
            ? new SanityEventClassificationResult(
                SanityEventClassification.Friendship,
                relationshipReason
            )
            : new SanityEventClassificationResult(
                SanityEventClassification.NonFriendship,
                "no-relationship-condition"
            );
    }

    private static bool IsValidFriendshipCondition(IReadOnlyList<string> tokens)
    {
        // Stardew's f/Friendship condition accepts one or more NPC/minimum-point pairs.
        var argumentCount = tokens.Count - 1;
        if (argumentCount < 2 || argumentCount % 2 != 0)
            return false;

        for (var index = 1; index < tokens.Count; index += 2)
        {
            if (string.IsNullOrWhiteSpace(tokens[index]))
                return false;
            if (
                !int.TryParse(
                    tokens[index + 1],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out _
                )
            )
            {
                return false;
            }
        }

        return true;
    }

    private static SanityEventClassificationResult ClassifyGameStateQuery(
        IReadOnlyList<string> conditionTokens
    )
    {
        if (conditionTokens.Count < 2)
            return Unknown("game-state-query-is-missing");

        IReadOnlyList<string> queryTokens;
        if (conditionTokens.Count == 2)
        {
            if (!TryTokenize(conditionTokens[1], out var nestedTokens))
                return Unknown("game-state-query-tokenization-failed");
            queryTokens = nestedTokens;
        }
        else
        {
            var directTokens = new string[conditionTokens.Count - 1];
            for (var index = 1; index < conditionTokens.Count; index++)
                directTokens[index - 1] = conditionTokens[index];
            queryTokens = directTokens;
        }

        if (queryTokens.Count == 0 || string.IsNullOrWhiteSpace(queryTokens[0]))
            return Unknown("game-state-query-is-missing");

        // Nested/compound/custom GSQ expressions are deliberately outside this narrow gate.
        foreach (var token in queryTokens)
        {
            if (token.Contains(','))
                return Unknown("game-state-query-is-compound");
        }

        var queryName = queryTokens[0];
        if (
            string.Equals(queryName, "ANY", StringComparison.OrdinalIgnoreCase)
            || string.Equals(queryName, "ALL", StringComparison.OrdinalIgnoreCase)
        )
        {
            return Unknown("game-state-query-is-nested");
        }

        if (!RelationshipQueries.Contains(queryName))
            return Unknown("game-state-query-is-outside-known-relationship-rules");

        if (
            string.IsNullOrWhiteSpace(queryTokens[1])
            || string.IsNullOrWhiteSpace(queryTokens[2])
        )
        {
            return Unknown("relationship-game-state-query-owner-or-npc-is-missing");
        }

        if (
            string.Equals(
                queryName,
                "PLAYER_NPC_RELATIONSHIP",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            if (queryTokens.Count is < 4 or > 9)
            {
                return Unknown(
                    "relationship-game-state-query-argument-count-is-invalid"
                );
            }
            for (var index = 3; index < queryTokens.Count; index++)
            {
                if (!RelationshipTypes.Contains(queryTokens[index]))
                    return Unknown("relationship-game-state-query-type-is-unknown");
            }
        }
        else
        {
            if (queryTokens.Count is < 4 or > 5)
            {
                return Unknown(
                    "relationship-game-state-query-argument-count-is-invalid"
                );
            }
            for (var index = 3; index < queryTokens.Count; index++)
            {
                if (
                    !int.TryParse(
                        queryTokens[index],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out _
                    )
                )
                {
                    return Unknown("relationship-game-state-query-range-is-invalid");
                }
            }
        }

        return new SanityEventClassificationResult(
            SanityEventClassification.Friendship,
            "known-relationship-game-state-query"
        );
    }

    private static bool TryTokenize(string? value, out List<string> tokens)
    {
        tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var current = new System.Text.StringBuilder();
        var quote = '\0';
        var escaped = false;
        foreach (var character in value)
        {
            if (escaped)
            {
                current.Append(character);
                escaped = false;
                continue;
            }

            if (character == '\\')
            {
                escaped = true;
                continue;
            }

            if (quote != '\0')
            {
                if (character == quote)
                    quote = '\0';
                else
                    current.Append(character);
                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                AddCurrentToken(tokens, current);
                continue;
            }

            current.Append(character);
        }

        if (escaped)
            current.Append('\\');
        if (quote != '\0')
            return false;

        AddCurrentToken(tokens, current);
        return tokens.Count > 0;
    }

    private static void AddCurrentToken(
        ICollection<string> tokens,
        System.Text.StringBuilder current
    )
    {
        if (current.Length == 0)
            return;
        tokens.Add(current.ToString());
        current.Clear();
    }

    private static SanityEventClassificationResult Unknown(string reason)
    {
        return new SanityEventClassificationResult(
            SanityEventClassification.Unknown,
            reason
        );
    }
}
