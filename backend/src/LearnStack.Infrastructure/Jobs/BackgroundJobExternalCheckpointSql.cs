using System.Diagnostics;
using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Jobs;

internal static class BackgroundJobExternalCheckpointSql
{
    internal readonly record struct CommitBudget(long ObservedAt, TimeSpan Remaining);
    private const string Header = """
        j.id=@id AND j.tenant_id=@tenant AND j.organization_id IS NOT DISTINCT FROM @organization
        AND j.definition_key=@definition AND j.profile='external' AND j.queue=@queue
        AND j.capacity_group=@capacity AND j.original_due_at=@due AND j.wake_generation=@generation
        AND j.current_attempt_id=@attempt AND j.normal_attempts=@normal_ordinal
        AND j.ownership_kind=@owner_kind AND j.ownership_token=@owner_token
        AND j.attempt_deadline=@deadline AND j.processed_at IS NULL AND j.dead_lettered_at IS NULL
        """;
    private const string NormalLedger = """
         AND EXISTS (SELECT 1 FROM background_job_attempts original WHERE original.id=@attempt AND original.tenant_id=@tenant
            AND original.organization_id IS NOT DISTINCT FROM @organization AND original.job_id=j.id
            AND original.stage='normal' AND original.ordinal=@normal_ordinal AND original.admission_token=@admission_token
            AND original.admitted_at=@admitted AND original.deadline=@deadline)
        """;
    private const string Normal = Header + " AND j.terminal_reason IS NULL" + NormalLedger;
    private const string TerminalNoStart = Header + " AND j.terminal_reason=@terminal_reason AND ("
        + BackgroundJobTerminalEligibility.TerminalNoStartBefore + ")" + NormalLedger;
    private const string Finalize = Header + """
         AND j.terminal_reason IS NULL
         AND j.finalize_attempts=@finalize_ordinal AND j.final_outcome_attempt_id=@selected_normal
         AND EXISTS (SELECT 1 FROM background_job_attempts original WHERE original.id=@attempt AND original.tenant_id=@tenant
            AND original.organization_id IS NOT DISTINCT FROM @organization AND original.job_id=j.id
            AND original.stage='finalize' AND original.ordinal=@finalize_ordinal AND original.admission_token=@admission_token
            AND original.admitted_at=@admitted AND original.deadline=@deadline
            AND original.final_outcome_attempt_id=@selected_normal)
        """;
    private const string LiveLease = " AND j.ownership_until>clock_timestamp()";

    internal static string Identity(BackgroundJobExternalCheckpointAuthority authority) =>
        authority.IsTerminalNoStart ? TerminalNoStart : authority.LedgerStage == BackgroundJobLedgerStage.Finalize ? Finalize : Normal;
    internal static string Live(BackgroundJobExternalCheckpointAuthority authority) => authority.IsRecovery
        ? LiveLease : LiveLease + " AND j.attempt_deadline>clock_timestamp()";
    internal static string Phase(BackgroundJobExternalCheckpointAuthority authority) => authority.IsTerminalNoStart
        ? " AND j.phase='terminal_pending'" : authority.IsRecovery
        ? " AND j.phase='reconciling'" : authority.LedgerStage == BackgroundJobLedgerStage.Finalize
            ? " AND j.phase='finalizing'" : " AND j.phase IN ('preparing','prepared','executing') AND j.finalization_status IS NULL";

    internal static async Task<CommitBudget> CaptureCommitBudgetAsync(IUnitOfWork unit,
        BackgroundJobExternalCheckpointAuthority authority, BackgroundJobRuntimeOptions options, CancellationToken token)
    {
        // The handler already owns the job lock. Start before the fresh SQL,
        // conservatively charging all query/reader work against the DB interval.
        await using var command = Command(unit, authority, options,
            "SELECT j.ownership_until,j.attempt_deadline,clock_timestamp() FROM background_jobs j WHERE "
                + Identity(authority) + Live(authority) + Phase(authority));
        var started = Stopwatch.GetTimestamp();
        await using var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await row.ReadAsync(token).ConfigureAwait(false) || row.FieldCount != 3) throw Refused();
        var until = row.GetFieldValue<DateTimeOffset>(0);
        var deadline = row.GetFieldValue<DateTimeOffset>(1);
        var now = row.GetFieldValue<DateTimeOffset>(2);
        if (!authority.IsRecovery && deadline < until) until = deadline;
        var remaining = until - now;
        if (remaining <= TimeSpan.Zero || remaining > TimeSpan.FromMinutes(10)) throw Refused();
        return new(started, remaining);
    }

    internal static NpgsqlCommand Command(IUnitOfWork unit, BackgroundJobExternalCheckpointAuthority authority,
        BackgroundJobRuntimeOptions options, string sql)
    {
        if (unit.Connection is not NpgsqlConnection connection || unit.Transaction is not NpgsqlTransaction transaction
            || !unit.HasActiveTransaction || unit.IsRollbackOnly
            || unit.GetTenantContextAnnouncement(transaction) is not { } announcement
            || announcement.TenantId != authority.TenantId || announcement.OrganizationId != authority.OrganizationId)
            throw Refused();
        var command = new NpgsqlCommand(sql, connection, transaction)
        { CommandTimeout = (int)Math.Ceiling(options.SqlTimeout.TotalSeconds) };
        command.Parameters.AddWithValue("id", authority.JobId.Value);
        command.Parameters.AddWithValue("tenant", authority.TenantId.Value);
        command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)authority.OrganizationId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("definition", authority.DefinitionKey);
        command.Parameters.AddWithValue("queue", authority.Queue);
        command.Parameters.AddWithValue("capacity", authority.CapacityGroup);
        command.Parameters.AddWithValue("due", authority.OriginalDueAt);
        command.Parameters.AddWithValue("generation", authority.WakeGeneration);
        command.Parameters.AddWithValue("attempt", authority.AttemptId.Value);
        command.Parameters.AddWithValue("normal_ordinal", authority.NormalAttemptOrdinal);
        command.Parameters.AddWithValue("finalize_ordinal", authority.FinalizeAttemptOrdinal);
        command.Parameters.AddWithValue("selected_normal", NpgsqlDbType.Uuid, (object?)authority.SelectedNormalAttemptId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("admission_token", authority.AdmissionToken);
        command.Parameters.AddWithValue("owner_token", authority.OwnerToken);
        command.Parameters.AddWithValue("owner_kind", authority.IsRecovery ? "recovery" : "execution");
        command.Parameters.AddWithValue("admitted", authority.AdmittedAt);
        command.Parameters.AddWithValue("deadline", authority.Deadline);
        if (authority.IsTerminalNoStart)
            command.Parameters.AddWithValue("terminal_reason", authority.TerminalReason!);
        return command;
    }

    internal static InvalidOperationException Refused() =>
        new("The external checkpoint refused its exact current owner and immutable ledger identity.");
}
