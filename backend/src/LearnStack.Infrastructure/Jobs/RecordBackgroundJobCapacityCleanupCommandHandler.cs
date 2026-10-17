using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using MediatR;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Records positively witnessed transactional cleanup and its exact reservation release together.</summary>
public sealed class RecordBackgroundJobCapacityCleanupCommandHandler(IUnitOfWork unit, BackgroundJobRuntimeOptions options)
    : IRequestHandler<RecordBackgroundJobCapacityCleanupCommand, Result<None>>
{
    private const string HeaderIdentity = """
        j.id=@job AND j.tenant_id=@tenant AND j.organization_id IS NOT DISTINCT FROM @organization
        AND j.definition_key=@definition AND j.profile='transactional' AND j.queue=@queue AND j.capacity_group=@group
        AND j.original_due_at=@due AND j.wake_generation=@generation AND j.current_attempt_id=@attempt
        AND j.normal_attempts=@ordinal AND j.attempt_deadline=@deadline AND j.phase='reconciling'
        AND j.ownership_kind='recovery' AND j.ownership_token=@recovery AND j.ownership_token<>@admission
        AND j.ownership_until>clock_timestamp() AND j.processed_at IS NULL
        AND j.dead_lettered_at IS NULL AND j.terminal_reason IS NULL
        """;
    private const string AttemptIdentity = """
        a.id=@attempt AND a.tenant_id=j.tenant_id AND a.job_id=j.id
        AND a.organization_id IS NOT DISTINCT FROM @organization AND a.stage='normal' AND a.ordinal=@ordinal
        AND a.admission_token=@admission AND a.admitted_at=@admitted AND a.deadline=@deadline
        AND a.plan_fingerprint IS NULL AND a.operation_id IS NULL AND a.execute_intent_at IS NULL
        AND a.outcome IS NULL AND a.quiescent_at IS NULL
        """;
    private const string ReservationIdentity = """
        r.tenant_id=@tenant AND r.capacity_group=@group AND r.job_id=@job AND r.attempt_id=@attempt
        """;

    public async Task<Result<None>> Handle(RecordBackgroundJobCapacityCleanupCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        options.Validate();
        request.RequireOwner(unit);
        // Never join the attempt into this first locking statement: after a wait,
        // its statement snapshot can predate the header's refreshed row.
        await using (var locked = Command(request, "SELECT TRUE FROM background_jobs WHERE id=@job AND tenant_id=@tenant FOR UPDATE"))
        {
            var found = await locked.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            request.RequireOwner(unit);
            if (found is not true) return Refused();
        }
        request.RequireOwner(unit);
        DateTimeOffset? recorded;
        DateTimeOffset? other;
        DateTimeOffset? released;
        string? releaseReason;
        var rollback = request.Evidence == BackgroundJobCapacityCleanupEvidence.TransactionalRollback;
        var evidenceColumn = rollback ? "transactional_cleanup_at" : "no_start_at";
        var reason = rollback ? "transactional_rollback" : "never_started";
        await using (var read = Command(request, """
            SELECT a.transactional_cleanup_at, a.no_start_at, r.released_at, r.release_reason
            FROM background_jobs j JOIN background_job_attempts a ON a.tenant_id=j.tenant_id AND a.job_id=j.id
            JOIN background_job_reservations r ON r.tenant_id=a.tenant_id AND r.job_id=a.job_id AND r.attempt_id=a.id
            WHERE
            """ + " " + HeaderIdentity + " AND " + AttemptIdentity + " AND " + ReservationIdentity + " FOR UPDATE OF a, r"))
        await using (var row = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            request.RequireOwner(unit);
            var found = await row.ReadAsync(cancellationToken).ConfigureAwait(false);
            request.RequireOwner(unit);
            if (!found) return Refused();
            recorded = row.IsDBNull(rollback ? 0 : 1) ? null : row.GetFieldValue<DateTimeOffset>(rollback ? 0 : 1);
            other = row.IsDBNull(rollback ? 1 : 0) ? null : row.GetFieldValue<DateTimeOffset>(rollback ? 1 : 0);
            released = row.IsDBNull(2) ? null : row.GetFieldValue<DateTimeOffset>(2);
            releaseReason = row.IsDBNull(3) ? null : row.GetString(3);
        }
        request.RequireOwner(unit);
        if (other is not null) return Refused();
        if (recorded is not null || released is not null || releaseReason is not null)
        {
            // An idempotent acknowledgement comes from the exact locked history,
            // never a zero-row UPDATE or a phase/token that could survive rollback.
            if (recorded is null || released is null || released < recorded || releaseReason != reason)
                return Refused();
            request.RecordCleanup(unit);
            return Result<None>.Ok(None.Value);
        }

        await using (var evidence = Command(request, "UPDATE background_job_attempts a SET " + evidenceColumn
            + "=clock_timestamp() FROM background_jobs j WHERE " + HeaderIdentity + " AND " + AttemptIdentity
            + " AND a.transactional_cleanup_at IS NULL AND a.no_start_at IS NULL RETURNING a.id"))
        {
            var written = await evidence.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            request.RequireOwner(unit);
            if (written is not Guid id || id != request.Recovery.AttemptId.Value)
                return Refused();
        }
        request.RequireOwner(unit);
        await using (var release = Command(request, """
            UPDATE background_job_reservations r SET released_at=clock_timestamp(), release_reason=@reason
            FROM background_jobs j JOIN background_job_attempts a ON a.tenant_id=j.tenant_id AND a.job_id=j.id
            WHERE
            """ + " " + HeaderIdentity + " AND " + AttemptIdentity + " AND " + ReservationIdentity
            + " AND a." + evidenceColumn + " IS NOT NULL AND r.released_at IS NULL"
            + " RETURNING r.tenant_id, r.capacity_group, r.job_id, r.attempt_id"))
        {
            release.Parameters.AddWithValue("reason", reason);
            await using var row = await release.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            request.RequireOwner(unit);
            var found = await row.ReadAsync(cancellationToken).ConfigureAwait(false);
            request.RequireOwner(unit);
            if (!found || row.GetGuid(0) != request.Recovery.TenantId.Value || row.GetString(1) != request.Recovery.CapacityGroup
                || row.GetGuid(2) != request.Recovery.JobId.Value || row.GetGuid(3) != request.Recovery.AttemptId.Value)
                return Refused();
        }
        request.RequireOwner(unit);
        request.RecordCleanup(unit);
        return Result<None>.Ok(None.Value);
    }

    private NpgsqlCommand Command(RecordBackgroundJobCapacityCleanupCommand request, string sql)
    {
        request.RequireOwner(unit);
        var recovery = request.Recovery;
        var command = new NpgsqlCommand(sql, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!)
        { CommandTimeout = (int)Math.Ceiling(options.SqlTimeout.TotalSeconds) };
        command.Parameters.AddWithValue("job", recovery.JobId.Value);
        command.Parameters.AddWithValue("tenant", recovery.TenantId.Value);
        command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)recovery.OrganizationId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("definition", recovery.DefinitionKey);
        command.Parameters.AddWithValue("queue", recovery.Queue);
        command.Parameters.AddWithValue("group", recovery.CapacityGroup);
        command.Parameters.AddWithValue("due", recovery.OriginalDueAt);
        command.Parameters.AddWithValue("generation", recovery.WakeGeneration);
        command.Parameters.AddWithValue("attempt", recovery.AttemptId.Value);
        command.Parameters.AddWithValue("ordinal", recovery.NormalAttemptOrdinal);
        command.Parameters.AddWithValue("deadline", recovery.Deadline);
        command.Parameters.AddWithValue("admitted", recovery.AdmittedAt);
        command.Parameters.AddWithValue("admission", recovery.AdmissionToken);
        command.Parameters.AddWithValue("recovery", recovery.RecoveryToken);
        return command;
    }

    private static Result<None> Refused() => Result<None>.Fail(BackgroundJobExecutionAdmission.Refused);
}
