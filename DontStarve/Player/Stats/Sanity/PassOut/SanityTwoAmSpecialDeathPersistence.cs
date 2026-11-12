#nullable enable

using System;
using System.Collections.Generic;

namespace DontStarve.Player.Stats.Sanity.PassOut;

internal enum SanityTwoAmSpecialDeathPersistenceStatus
{
    Missing,
    Available,
    Invalid,
}

internal sealed record SanityTwoAmSpecialDeathPersistenceResult(
    SanityTwoAmSpecialDeathPersistenceStatus Status,
    string Reason,
    SanityTwoAmSpecialDeathSaveData? Data
)
{
    internal bool CanWrite => Status != SanityTwoAmSpecialDeathPersistenceStatus.Invalid;
}

internal static class SanityTwoAmSpecialDeathPersistence
{
    internal static SanityTwoAmSpecialDeathPersistenceResult Validate(
        SanityTwoAmSpecialDeathSaveData? data
    )
    {
        if (data is null)
            return new(
                SanityTwoAmSpecialDeathPersistenceStatus.Missing,
                "passout.two-am.save-missing",
                null
            );

        // Schema 1 was the old clinic/mail flow. It is safe to read and discard: no new code
        // resumes its recovery, queues its mail, or warps the player. This lets the next save
        // replace it with the empty schema-2 payload.
        if (
            data.SchemaVersion != 1
            && data.SchemaVersion != SanityTwoAmSpecialDeathSaveData.CurrentSchemaVersion
        )
            return Invalid("passout.two-am.save-schema-unsupported");
        if (data.Flows is null || data.Flows.Count > SanityTwoAmSpecialDeathContract.MaximumFlows)
            return Invalid("passout.two-am.save-flow-count-invalid");

        var players = new HashSet<string>(StringComparer.Ordinal);
        var correlations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var flow in data.Flows)
        {
            var reason = ValidateFlow(flow);
            if (reason is not null)
                return Invalid(reason);
            if (!players.Add(flow.PlayerKey))
                return Invalid("passout.two-am.save-player-duplicated");
            if (!correlations.Add(flow.CorrelationId))
                return Invalid("passout.two-am.save-correlation-duplicated");
        }

        return new(
            SanityTwoAmSpecialDeathPersistenceStatus.Available,
            data.SchemaVersion == 1
                ? "passout.two-am.legacy-save-valid-discardable"
                : "passout.two-am.save-valid",
            data
        );
    }

    internal static SanityTwoAmSpecialDeathSaveData Capture(
        IReadOnlyList<SanityTwoAmSpecialDeathSnapshot> snapshots
    )
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        // The new chain completes through vanilla death or PassOutNewDay and has no custom
        // recovery/mail phase. Always write an empty current payload to retire schema-1 data.
        return new SanityTwoAmSpecialDeathSaveData();
    }

    internal static string? ValidateFlow(SanityTwoAmSpecialDeathPersistedFlow? flow)
    {
        if (flow is null)
            return "passout.two-am.save-flow-null";
        if (!SanityProtocol.IsValidSessionId(flow.SessionId))
            return "passout.two-am.save-session-invalid";
        if (!Guid.TryParseExact(flow.CorrelationId, "N", out var parsed) || parsed == Guid.Empty)
            return "passout.two-am.save-correlation-invalid";
        if (!SanityPlayerKey.IsCanonical(flow.PlayerKey))
            return "passout.two-am.save-player-invalid";
        if (flow.ScreenId < 0 || flow.AuthorityRevision < 0 || flow.Revision <= 0)
            return "passout.two-am.save-revision-invalid";
        if (
            !string.Equals(flow.Kind, "DefaultClinic", StringComparison.Ordinal)
        )
            return "passout.two-am.save-kind-invalid";
        if (
            !string.Equals(flow.Phase, "AwaitingRecovery", StringComparison.Ordinal)
            && !string.Equals(flow.Phase, "Recovered", StringComparison.Ordinal)
        )
            return "passout.two-am.save-phase-invalid";
        if (!string.Equals(flow.MailId, "Yurin.DontStarve_SanityDarknessSpecialDeath", StringComparison.Ordinal))
            return "passout.two-am.save-mail-id-invalid";
        return null;
    }

    private static SanityTwoAmSpecialDeathPersistenceResult Invalid(string reason) =>
        new(SanityTwoAmSpecialDeathPersistenceStatus.Invalid, reason, null);
}
