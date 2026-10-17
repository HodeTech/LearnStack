using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Get-only metadata captured from the actual prepared terminal acquisition.</summary>
internal sealed class BackgroundJobTerminalPreparedFacts
{
    internal const string Projection = """
        j.finalize_attempts,j.final_outcome_attempt_id,j.finalization_status,j.finalization_reason,
        j.plan_fingerprint,j.operation_id,j.prepared_at,COALESCE(j.final_outcome_attempt_id,j.current_attempt_id),
        (SELECT a.outcome_kind FROM background_job_attempts a WHERE a.tenant_id=j.tenant_id AND a.job_id=j.id
            AND a.id=COALESCE(j.final_outcome_attempt_id,j.current_attempt_id) AND a.stage='normal'),
        (SELECT a.failure_policy FROM background_job_attempts a WHERE a.tenant_id=j.tenant_id AND a.job_id=j.id
            AND a.id=COALESCE(j.final_outcome_attempt_id,j.current_attempt_id) AND a.stage='normal')
        """;
    internal const string Fence = """
        j.finalize_attempts=@terminal_finalize_count
        AND j.final_outcome_attempt_id IS NOT DISTINCT FROM @terminal_selected
        AND j.finalization_status IS NOT DISTINCT FROM @terminal_status
        AND j.finalization_reason IS NOT DISTINCT FROM @terminal_module_reason
        AND j.plan_fingerprint=@terminal_fingerprint AND j.operation_id=@terminal_operation
        AND j.prepared_at=@terminal_prepared_at
        AND COALESCE(j.final_outcome_attempt_id,j.current_attempt_id)=@terminal_normal
        AND EXISTS (SELECT 1 FROM background_job_attempts n WHERE n.tenant_id=j.tenant_id AND n.job_id=j.id
            AND n.id=@terminal_normal AND n.stage='normal' AND n.ordinal=j.normal_attempts
            AND n.organization_id IS NOT DISTINCT FROM j.organization_id
            AND n.outcome_kind IS NOT DISTINCT FROM @terminal_state AND n.failure_policy IS NOT DISTINCT FROM @terminal_policy)
        """;

    private BackgroundJobTerminalPreparedFacts(NpgsqlDataReader row, int normalAttempts, Guid? currentAttempt,
        string reason, DateTimeOffset observedAt)
    {
        if (row.FieldCount != 24) throw Refused();
        FinalizeAttempts = row.GetInt32(14);
        SelectedNormalAttemptId = row.IsDBNull(15) ? null : row.GetGuid(15);
        FinalizationStatus = Text(row, 16);
        FinalizationReason = Text(row, 17);
        PlanFingerprint = row.GetString(18);
        OperationId = row.GetGuid(19);
        PreparedAt = row.GetFieldValue<DateTimeOffset>(20);
        NormalAttemptId = row.GetGuid(21);
        State = Text(row, 22);
        Policy = Text(row, 23);
        if (normalAttempts is < 1 or > 6 || currentAttempt is null || FinalizeAttempts is < 0 or > 6
            || NormalAttemptId.Version != 7 || SelectedNormalAttemptId is { Version: not 7 }
            || PreparedAt == DateTimeOffset.MinValue || PreparedAt == DateTimeOffset.MaxValue || PreparedAt > observedAt
            || PlanFingerprint.Length != 64 || PlanFingerprint.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            || OperationId == Guid.Empty || (FinalizeAttempts == 0) != (currentAttempt == NormalAttemptId)
            || SelectedNormalAttemptId is { } selected && selected != NormalAttemptId
            || reason is not ("retired_definition" or "invalid_envelope" or "finalize_refused" or "finalize_exhausted")
            || reason is "finalize_refused" or "finalize_exhausted" && (FinalizeAttempts == 0 || SelectedNormalAttemptId is null)
            || reason == "finalize_exhausted" && FinalizeAttempts != 6
            || !ValidState(State, Policy)) throw Refused();
        if (SelectedNormalAttemptId is null)
        {
            if (FinalizeAttempts != 0 || FinalizationStatus is not null || FinalizationReason is not null
                || reason is not ("retired_definition" or "invalid_envelope")) throw Refused();
        }
        else if (!ValidSelection(State, Policy, FinalizationStatus, FinalizationReason, normalAttempts)) throw Refused();
    }

    internal int FinalizeAttempts { get; }
    internal Guid? SelectedNormalAttemptId { get; }
    internal string? FinalizationStatus { get; }
    internal string? FinalizationReason { get; }
    internal string PlanFingerprint { get; }
    internal Guid OperationId { get; }
    internal DateTimeOffset PreparedAt { get; }
    internal Guid NormalAttemptId { get; }
    internal string? State { get; }
    internal string? Policy { get; }

    internal static BackgroundJobTerminalPreparedFacts FromReturnedRow(NpgsqlDataReader row,
        int normalAttempts, Guid? currentAttempt, string reason, DateTimeOffset observedAt) =>
        new(row, normalAttempts, currentAttempt, reason, observedAt);

    internal void AddParameters(NpgsqlCommand command)
    {
        command.Parameters.AddWithValue("terminal_finalize_count", FinalizeAttempts);
        command.Parameters.AddWithValue("terminal_selected", NpgsqlDbType.Uuid, (object?)SelectedNormalAttemptId ?? DBNull.Value);
        command.Parameters.AddWithValue("terminal_status", NpgsqlDbType.Text, (object?)FinalizationStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("terminal_module_reason", NpgsqlDbType.Text, (object?)FinalizationReason ?? DBNull.Value);
        command.Parameters.AddWithValue("terminal_fingerprint", PlanFingerprint);
        command.Parameters.AddWithValue("terminal_operation", OperationId);
        command.Parameters.AddWithValue("terminal_prepared_at", PreparedAt);
        command.Parameters.AddWithValue("terminal_normal", NormalAttemptId);
        command.Parameters.AddWithValue("terminal_state", NpgsqlDbType.Text, (object?)State ?? DBNull.Value);
        command.Parameters.AddWithValue("terminal_policy", NpgsqlDbType.Text, (object?)Policy ?? DBNull.Value);
    }

    private static bool ValidState(string? state, string? policy) => (state, policy) switch
    {
        (null or "succeeded", null) => true,
        ("failed", "permanent") => true,
        ("failed" or "cancelled", "transient_repeatable" or "transient_not_repeatable") => true,
        _ => false,
    };

    private static bool ValidSelection(string? state, string? policy, string? status, string? reason, int normalAttempts) =>
        (state, policy, status, reason) switch
        {
            ("succeeded", null, "succeeded", "none") => true,
            ("failed", "permanent" or "transient_not_repeatable", "failed", "external_failed") => true,
            ("cancelled", "transient_not_repeatable", "failed", "external_cancelled") => true,
            ("failed" or "cancelled", "transient_repeatable", "failed", "normal_attempts_exhausted") => normalAttempts == 6,
            (null, null, "failed", "permanent_failure") => true,
            (null, null, "failed", "normal_attempts_exhausted") => normalAttempts == 6,
            _ => false,
        };

    private static string? Text(NpgsqlDataReader row, int index) => row.IsDBNull(index) ? null : row.GetString(index);
    private static InvalidOperationException Refused() => new("Prepared terminal acquisition returned inconsistent immutable metadata.");
}
