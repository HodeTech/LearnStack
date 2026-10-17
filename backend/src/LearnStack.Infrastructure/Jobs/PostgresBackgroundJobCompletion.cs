using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>The single app-role participant for transactional job completion.</summary>
internal sealed class PostgresBackgroundJobCompletion(IUnitOfWork unit, ITenantContextAccessor accessor,
    BackgroundJobRuntimeOptions options) : BackgroundJobCompletionParticipant(unit, accessor)
{
    private const string Identity = """
        id = @id AND tenant_id = @tenant AND organization_id IS NOT DISTINCT FROM @organization
        AND definition_key = @definition AND profile = 'transactional' AND queue = @queue
        AND capacity_group IS NOT DISTINCT FROM @capacity
        AND wake_generation = @generation AND current_attempt_id = @attempt AND normal_attempts = @ordinal
        AND ownership_token = @token AND ownership_kind = 'execution' AND phase = 'executing'
        AND attempt_deadline = @deadline AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL
        AND EXISTS (SELECT 1 FROM background_job_attempts a WHERE a.id = @attempt
            AND a.tenant_id = @tenant AND a.organization_id IS NOT DISTINCT FROM @organization
            AND a.job_id = background_jobs.id AND a.stage = 'normal' AND a.ordinal = @ordinal
            AND a.admission_token = @token AND a.admitted_at = @admitted AND a.deadline = @deadline)
        AND (@capacity::text IS NULL OR EXISTS (SELECT 1 FROM background_job_reservations r
            WHERE r.tenant_id = @tenant AND r.capacity_group = @capacity
                AND r.job_id = background_jobs.id AND r.attempt_id = @attempt AND r.released_at IS NULL))
        """;
    private const string LiveTime = " AND ownership_until > clock_timestamp() AND attempt_deadline > clock_timestamp()";

    protected override async Task PreflightAsync(CancellationToken cancellationToken)
    {
        RequireCurrent();
        // No row lock spans the business handler. Its final authority is checked again after locking.
        await using var command = CreateCommand("SELECT " + BackgroundJobEnvelopeSql.Projection
            + " FROM background_jobs WHERE " + Identity + LiveTime);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || BackgroundJobEnvelopeSql.Read(reader) != Receipt.Envelope)
            throw Refused();
        RequireCurrent();
    }

    protected override async Task FenceAsync(CancellationToken cancellationToken)
    {
        RequireCurrent();
        await using (var locked = CreateCommand("SELECT TRUE FROM background_jobs WHERE " + Identity + " FOR UPDATE"))
        {
            if (await locked.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw Refused();
        }
        RequireCurrent();
        // The time predicates execute in a separate statement after the actual lock wait.
        await using var update = CreateCommand("UPDATE background_jobs SET phase = 'completed', processed_at = clock_timestamp() WHERE "
            + Identity + LiveTime + " RETURNING TRUE");
        if (await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw Refused();
        RequireCurrent();
        if (Receipt.Envelope.CapacityGroup is not null)
        {
            // Completion only decreases occupancy. The successful fenced UPDATE
            // above and this release share the same announced business transaction.
            await using var release = CreateCommand("""
                UPDATE background_job_reservations SET released_at=clock_timestamp(), release_reason='completed'
                WHERE tenant_id=@tenant AND capacity_group=@capacity AND job_id=@id AND attempt_id=@attempt
                    AND released_at IS NULL
                RETURNING tenant_id, capacity_group, job_id, attempt_id
                """);
            await using var released = await release.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var found = await released.ReadAsync(cancellationToken).ConfigureAwait(false);
            RequireCurrent();
            if (!found || released.GetGuid(0) != Receipt.Envelope.TenantId.Value
                || released.GetString(1) != Receipt.Envelope.CapacityGroup
                || released.GetGuid(2) != Receipt.Envelope.JobId.Value || released.GetGuid(3) != Receipt.AttemptId.Value)
                throw Refused();
        }
        RequireCurrent();
    }

    private NpgsqlCommand CreateCommand(string sql)
    {
        RequireCurrent();
        var command = BackgroundJobEnvelopeSql.Command(Unit, sql, Receipt.Claim, options);
        command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)Receipt.Envelope.OrganizationId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("definition", Receipt.Envelope.DefinitionKey);
        command.Parameters.AddWithValue("queue", Receipt.Envelope.Queue);
        command.Parameters.AddWithValue("capacity", NpgsqlDbType.Text, (object?)Receipt.Envelope.CapacityGroup ?? DBNull.Value);
        command.Parameters.AddWithValue("generation", Receipt.Claim.WakeGeneration);
        command.Parameters.AddWithValue("attempt", Receipt.AttemptId.Value);
        command.Parameters.AddWithValue("ordinal", Receipt.NormalAttemptOrdinal);
        command.Parameters.AddWithValue("token", Receipt.Claim.Token);
        command.Parameters.AddWithValue("deadline", Receipt.Deadline);
        command.Parameters.AddWithValue("admitted", Receipt.AdmittedAt);
        return command;
    }

    private static InvalidOperationException Refused() =>
        new("The transactional background-job fence refused current execution authority.");
}
