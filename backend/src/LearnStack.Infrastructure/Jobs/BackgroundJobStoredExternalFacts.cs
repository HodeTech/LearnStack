using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Canonical app-role data only; no bypass payload read or execution permission.</summary>
internal static class BackgroundJobStoredExternalFacts
{
    internal const string Projection = """
        j.plan,j.plan_fingerprint,j.operation_id,a.plan_fingerprint,a.operation_id,
        a.execute_intent_at,a.no_start_at,a.outcome,a.outcome_kind,a.failure_policy,a.outcome_at,
        a.quiescence_id,a.quiescence_kind,a.quiescent_at,r.released_at,r.release_reason,clock_timestamp(),r.attempt_id,
        j.final_outcome_attempt_id,j.finalization_status,j.finalization_reason
        """;
    internal const string From = """
         FROM background_jobs j JOIN background_job_attempts a
          ON a.tenant_id=j.tenant_id AND a.job_id=j.id AND a.id=@normal_attempt AND a.stage='normal'
            AND a.organization_id IS NOT DISTINCT FROM j.organization_id AND a.ordinal=@normal_ordinal
         LEFT JOIN background_job_reservations r
          ON r.tenant_id=a.tenant_id AND r.job_id=a.job_id AND r.attempt_id=a.id AND r.capacity_group=j.capacity_group
         WHERE
        """;

    internal static async Task<BackgroundJobExternalSnapshot> ReadTerminalNoStartAsync(IUnitOfWork unit,
        BackgroundJobExternalCheckpointAuthority authority, BackgroundJobRuntimeOptions options, CancellationToken token)
    {
        if (!authority.IsTerminalNoStart) throw BackgroundJobExternalCheckpointSql.Refused();
        var predicate = BackgroundJobExternalCheckpointSql.Identity(authority)
            + BackgroundJobExternalCheckpointSql.Live(authority) + BackgroundJobExternalCheckpointSql.Phase(authority);
        BackgroundJobEnvelope envelope;
        await using (var command = BackgroundJobExternalCheckpointSql.Command(unit, authority, options,
            "SELECT " + BackgroundJobEnvelopeSql.Projection + " FROM background_jobs j WHERE " + predicate))
        await using (var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await row.ReadAsync(token).ConfigureAwait(false)) throw BackgroundJobExternalCheckpointSql.Refused();
            envelope = BackgroundJobEnvelopeSql.Read(row);
            if (!authority.MatchesEnvelope(envelope) || !BackgroundJobEnvelopeSql.HasValidCausalContext(envelope))
                throw BackgroundJobExternalCheckpointSql.Refused();
        }
        await using var facts = BackgroundJobExternalCheckpointSql.Command(unit, authority, options,
            "SELECT " + Projection + From + " " + predicate);
        facts.Parameters.AddWithValue("normal_attempt", authority.AttemptId.Value);
        await using var stored = await facts.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await stored.ReadAsync(token).ConfigureAwait(false)) throw BackgroundJobExternalCheckpointSql.Refused();
        return ReadTerminalNoStart(stored, envelope, authority.AttemptId, authority.TerminalReason);
    }

    internal static BackgroundJobExternalSnapshot ReadTerminalNoStart(DbDataReader row,
        BackgroundJobEnvelope envelope, BackgroundJobAttemptId normalAttempt, string? terminalReason)
    {
        if (row.FieldCount != 21 || row.IsDBNull(17) || row.GetGuid(17) != normalAttempt.Value)
            throw BackgroundJobExternalCheckpointSql.Refused();
        string? plan = null, fingerprint = null;
        Guid? operation = null;
        if (!row.IsDBNull(0))
        {
            if (terminalReason is not ("retired_definition" or "invalid_envelope"))
                throw BackgroundJobExternalCheckpointSql.Refused();
            plan = ReadUninvokablePlan(row.GetString(0));
            fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plan)));
            operation = BackgroundJobExternalIdentity.OperationId(envelope.JobId, fingerprint);
            if (Text(row, 1) != fingerprint || row.IsDBNull(2) || row.GetGuid(2) != operation)
                throw BackgroundJobExternalCheckpointSql.Refused();
        }
        else if (!row.IsDBNull(1) || !row.IsDBNull(2)) throw BackgroundJobExternalCheckpointSql.Refused();
        if ((!row.IsDBNull(3) || !row.IsDBNull(4)) && (plan is null || Text(row, 3) != fingerprint
            || row.IsDBNull(4) || row.GetGuid(4) != operation)) throw BackgroundJobExternalCheckpointSql.Refused();
        for (var index = 5; index < 21; index++)
            if (index is not (6 or 14 or 15 or 16 or 17) && !row.IsDBNull(index))
                throw BackgroundJobExternalCheckpointSql.Refused();
        var noStart = Time(row, 6);
        var released = Time(row, 14);
        var now = row.GetFieldValue<DateTimeOffset>(16);
        if (noStart is not null ? noStart > now || released is null || released < noStart || released > now
                || Text(row, 15) != "never_started"
            : released is not null || !row.IsDBNull(15)) throw BackgroundJobExternalCheckpointSql.Refused();
        return new(plan, fingerprint, operation, null, null, null) { Envelope = envelope, NoStartRecorded = noStart is not null };
    }

    // The immutable plan already passed its typed Prepare contract. Uninvokable
    // cleanup reads only its stored wire/hash; it cannot resolve module types.
    private static string ReadUninvokablePlan(string plan)
    {
        BackgroundJobPayloadContract.EnsureWithinBudget(plan, BackgroundJobPayloadKind.Plan);
        using var document = JsonDocument.Parse(plan);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw BackgroundJobExternalCheckpointSql.Refused();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!names.Add(property.Name) || property.Value.ValueKind != JsonValueKind.Null
                && (property.Value.ValueKind != JsonValueKind.String || !property.Value.TryGetGuid(out var identifier)
                    || identifier == Guid.Empty || property.Value.GetString() != identifier.ToString("D")))
                throw BackgroundJobExternalCheckpointSql.Refused();
        }
        return plan;
    }

    internal static BackgroundJobExternalSnapshot Read(DbDataReader row, BackgroundJobDefinitionDescriptor definition,
        BackgroundJobEnvelope envelope, int ordinal, BackgroundJobAttemptId normalAttempt)
    {
        if (row.FieldCount != 21 || row.IsDBNull(17) || row.GetGuid(17) != normalAttempt.Value)
            throw BackgroundJobExternalCheckpointSql.Refused();
        string? plan = null, fingerprint = null;
        Guid? operation = null;
        if (!row.IsDBNull(0))
        {
            plan = Canonical(row.GetString(0), definition.ProfileRegistration.PlanType!, BackgroundJobPayloadKind.Plan);
            fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plan)));
            operation = BackgroundJobExternalIdentity.OperationId(envelope.JobId, fingerprint);
            if (row.IsDBNull(1) || row.GetString(1) != fingerprint || row.IsDBNull(2) || row.GetGuid(2) != operation)
                throw BackgroundJobExternalCheckpointSql.Refused();
        }
        else if (!row.IsDBNull(1) || !row.IsDBNull(2)) throw BackgroundJobExternalCheckpointSql.Refused();
        var stamp = !row.IsDBNull(3) || !row.IsDBNull(4);
        if (stamp && (plan is null || row.IsDBNull(3) || row.GetString(3) != fingerprint
            || row.IsDBNull(4) || row.GetGuid(4) != operation)) throw BackgroundJobExternalCheckpointSql.Refused();
        var intent = Time(row, 5);
        var noStart = Time(row, 6);
        var released = Time(row, 14);
        var releaseReason = Text(row, 15);
        var now = row.GetFieldValue<DateTimeOffset>(16);
        if (intent is not null && (!stamp || intent > now || noStart is not null)
            || noStart > now) throw BackgroundJobExternalCheckpointSql.Refused();
        string? outcome = null;
        BackgroundJobExternalState? state = null;
        BackgroundJobExternalFailurePolicy? policy = null;
        if (!row.IsDBNull(7))
        {
            outcome = Canonical(row.GetString(7), definition.ProfileRegistration.OutcomeType!, BackgroundJobPayloadKind.Outcome);
            state = Text(row, 8) switch
            {
                "succeeded" => BackgroundJobExternalState.Succeeded,
                "failed" => BackgroundJobExternalState.Failed,
                "cancelled" => BackgroundJobExternalState.Cancelled,
                _ => throw BackgroundJobExternalCheckpointSql.Refused(),
            };
            policy = Text(row, 9) switch
            {
                null => null,
                "permanent" => BackgroundJobExternalFailurePolicy.Permanent,
                "transient_repeatable" => BackgroundJobExternalFailurePolicy.TransientRepeatable,
                "transient_not_repeatable" => BackgroundJobExternalFailurePolicy.TransientNotRepeatable,
                _ => throw BackgroundJobExternalCheckpointSql.Refused(),
            };
            _ = BackgroundJobExternalPolicy.Select(state.Value, policy, ordinal);
            var at = Time(row, 10);
            var quiescent = Time(row, 13);
            if (intent is null || at is null || at < intent || at > now || noStart is not null
                || row.IsDBNull(11) || row.GetGuid(11) == Guid.Empty
                || Text(row, 12) is not ("provider_fenced" or "execution_terminated")
                || quiescent is null || quiescent < intent || quiescent > now
                || released is null || released < quiescent || released > now || releaseReason != "quiescent")
                throw BackgroundJobExternalCheckpointSql.Refused();
        }
        else
        {
            for (var index = 8; index <= 13; index++)
                if (!row.IsDBNull(index)) throw BackgroundJobExternalCheckpointSql.Refused();
            if (noStart is not null
                ? intent is not null || released is null || released < noStart || released > now || releaseReason != "never_started"
                : released is not null || releaseReason is not null)
                throw BackgroundJobExternalCheckpointSql.Refused();
        }
        var selection = ReadSelection(row, normalAttempt, ordinal, plan is not null && stamp, noStart is not null, state, policy);
        return new(plan, fingerprint, operation, outcome, state, policy)
        {
            Envelope = envelope,
            ExecuteIntentRecorded = intent is not null,
            NoStartRecorded = noStart is not null,
            SelectedNormalAttemptId = selection is null ? null : normalAttempt,
            FinalizationStatus = selection?.FinalizationStatus,
            FinalizationReason = selection?.FinalizationReason,
        };
    }

    private static BackgroundJobKnownOutcomeDecision? ReadSelection(DbDataReader row, BackgroundJobAttemptId normalAttempt,
        int ordinal, bool stampedPlan, bool noStart, BackgroundJobExternalState? state, BackgroundJobExternalFailurePolicy? policy)
    {
        // A normal/recovery read precedes selection. In particular, retained
        // no-start history may still need its original attempt stamp written.
        if (row.IsDBNull(18) && row.IsDBNull(19) && row.IsDBNull(20)) return null;
        if (row.IsDBNull(18) || row.IsDBNull(19) || row.IsDBNull(20)
            || row.GetGuid(18) != normalAttempt.Value || !stampedPlan || ordinal is < 1 or > 6)
            throw BackgroundJobExternalCheckpointSql.Refused();
        var decision = state is { } completed ? BackgroundJobExternalPolicy.Select(completed, policy, ordinal)
            : noStart && row.GetString(19) == "failed" && row.GetString(20) == "permanent_failure"
                ? new(BackgroundJobKnownOutcomeDisposition.Finalize, BackgroundJobFinalizationStatus.Failed,
                    BackgroundJobFinalizationReason.PermanentFailure, "permanent_failure")
                : noStart && ordinal == 6 && row.GetString(19) == "failed" && row.GetString(20) == "normal_attempts_exhausted"
                    ? new(BackgroundJobKnownOutcomeDisposition.Finalize, BackgroundJobFinalizationStatus.Failed,
                        BackgroundJobFinalizationReason.NormalAttemptsExhausted, "retry_exhausted")
                    : throw BackgroundJobExternalCheckpointSql.Refused();
        var reason = decision.FinalizationReason switch
        {
            BackgroundJobFinalizationReason.None => "none",
            BackgroundJobFinalizationReason.ExternalFailed => "external_failed",
            BackgroundJobFinalizationReason.ExternalCancelled => "external_cancelled",
            BackgroundJobFinalizationReason.NormalAttemptsExhausted => "normal_attempts_exhausted",
            BackgroundJobFinalizationReason.PermanentFailure => "permanent_failure",
            _ => throw BackgroundJobExternalCheckpointSql.Refused(),
        };
        if (decision.Disposition != BackgroundJobKnownOutcomeDisposition.Finalize
            || row.GetString(19) != (decision.FinalizationStatus == BackgroundJobFinalizationStatus.Succeeded ? "succeeded" : "failed")
            || row.GetString(20) != reason) throw BackgroundJobExternalCheckpointSql.Refused();
        return decision;
    }

    private static string Canonical(string stored, Type type, BackgroundJobPayloadKind kind)
    {
        var canonical = BackgroundJobPayloadContract.Serialize(BackgroundJobPayloadContract.Read(stored, type, kind), type, kind);
        return canonical == stored ? canonical : throw BackgroundJobExternalCheckpointSql.Refused();
    }
    private static string? Text(DbDataReader row, int ordinal) => row.IsDBNull(ordinal) ? null : row.GetString(ordinal);
    private static DateTimeOffset? Time(DbDataReader row, int ordinal) => row.IsDBNull(ordinal) ? null : row.GetFieldValue<DateTimeOffset>(ordinal);
}
