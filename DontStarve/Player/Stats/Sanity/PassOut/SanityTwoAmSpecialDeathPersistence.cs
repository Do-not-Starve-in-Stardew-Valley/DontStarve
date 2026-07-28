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
        {
            return new SanityTwoAmSpecialDeathPersistenceResult(
                SanityTwoAmSpecialDeathPersistenceStatus.Missing,
                "passout.two-am.save-missing",
                null
            );
        }
        if (data.SchemaVersion != SanityTwoAmSpecialDeathSaveData.CurrentSchemaVersion)
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

        return new SanityTwoAmSpecialDeathPersistenceResult(
            SanityTwoAmSpecialDeathPersistenceStatus.Available,
            "passout.two-am.save-valid",
            data
        );
    }

    internal static SanityTwoAmSpecialDeathSaveData Capture(
        IReadOnlyList<SanityTwoAmSpecialDeathSnapshot> snapshots
    )
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var data = new SanityTwoAmSpecialDeathSaveData();
        foreach (var snapshot in snapshots)
        {
            if (
                snapshot.Kind != SanityTwoAmSpecialDeathFlowKind.DefaultClinic
                || snapshot.Phase
                    is not (
                        SanityTwoAmSpecialDeathPhase.AwaitingRecovery
                        or SanityTwoAmSpecialDeathPhase.Recovered
                    )
            )
            {
                continue;
            }
            data.Flows.Add(
                new SanityTwoAmSpecialDeathPersistedFlow
                {
                    SessionId = snapshot.SessionId,
                    CorrelationId = snapshot.CorrelationId,
                    PlayerKey = snapshot.PlayerKey,
                    ScreenId = snapshot.ScreenId,
                    AuthorityRevision = snapshot.AuthorityRevision,
                    Kind = snapshot.Kind.ToString(),
                    Phase = snapshot.Phase.ToString(),
                    MailQueued = snapshot.MailQueued,
                    MailId = snapshot.MailId,
                    Revision = snapshot.Revision,
                }
            );
        }
        return data;
    }

    internal static string? ValidateFlow(
        SanityTwoAmSpecialDeathPersistedFlow? flow
    )
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
        if (!string.Equals(
                flow.Kind,
                SanityTwoAmSpecialDeathFlowKind.DefaultClinic.ToString(),
                StringComparison.Ordinal
            ))
        {
            return "passout.two-am.save-kind-invalid";
        }
        if (
            !Enum.TryParse<SanityTwoAmSpecialDeathPhase>(
                flow.Phase,
                ignoreCase: false,
                out var phase
            )
            || phase
                is not (
                    SanityTwoAmSpecialDeathPhase.AwaitingRecovery
                    or SanityTwoAmSpecialDeathPhase.Recovered
                )
        )
        {
            return "passout.two-am.save-phase-invalid";
        }
        if (!string.Equals(
                flow.MailId,
                SanityTwoAmSpecialDeathContract.MailId,
                StringComparison.Ordinal
            ))
        {
            return "passout.two-am.save-mail-id-invalid";
        }
        return null;
    }

    private static SanityTwoAmSpecialDeathPersistenceResult Invalid(string reason)
    {
        return new SanityTwoAmSpecialDeathPersistenceResult(
            SanityTwoAmSpecialDeathPersistenceStatus.Invalid,
            reason,
            null
        );
    }
}
