using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Time;
using Npgsql;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Confined, payload-free routing acquisition. Execution and recovery ownership never qualify.</summary>
public sealed class PostgresBackgroundJobRoutingStore(
    PrivilegedDataSource source, BackgroundJobDefinitionRegistry definitions,
    BackgroundJobRuntimeOptions options, IGuidFactory ids) : BackgroundJobRoutingClaim.MintingSource
{
    private readonly PrivilegedDataSource _source = source;

    public async Task<BackgroundJobRoutingClaim?> TryClaimAsync(BackgroundJobId jobId, long wakeGeneration,
        CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        options.Validate();
        if (!jobId.IsInitialized() || jobId.Value.Version != 7 || wakeGeneration < 1)
            throw new ArgumentException("A job wakeup requires its opaque identity and positive generation.");
        var active = definitions.DefinitionKeys.Where(key => definitions.TryGet(key, out var definition)
            && definition.Readiness == BackgroundJobDefinitionReadiness.Active
            && definition.Profile == BackgroundJobExecutionProfile.Transactional && definition.CapacityGroup is null).ToArray();
        if (active.Length == 0) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.SqlTimeout);
        await using var connection = await _source.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            WITH candidate AS (
                SELECT id FROM background_jobs
                WHERE id = @id AND wake_generation = @generation AND definition_key = ANY(@active)
                    AND profile = 'transactional' AND capacity_group IS NULL AND phase = 'pending'
                    AND normal_attempts < 6 AND processed_at IS NULL AND dead_lettered_at IS NULL
                    AND terminal_reason IS NULL AND available_after <= clock_timestamp()
                    AND (ownership_token IS NULL OR (ownership_kind = 'routing' AND ownership_until <= clock_timestamp()))
                FOR UPDATE SKIP LOCKED
            )
            UPDATE background_jobs SET ownership_token = @token, ownership_kind = 'routing',
                ownership_until = clock_timestamp() + @lease
            WHERE id = (SELECT id FROM candidate)
            RETURNING id, tenant_id, organization_id, definition_key, queue, wake_generation,
                ownership_token, ownership_until, original_due_at
            """, connection, transaction) { CommandTimeout = (int)Math.Ceiling(options.SqlTimeout.TotalSeconds) };
        command.Parameters.AddWithValue("id", jobId.Value);
        command.Parameters.AddWithValue("generation", wakeGeneration);
        command.Parameters.AddWithValue("active", active);
        command.Parameters.AddWithValue("token", ids.NewUuidV4());
        command.Parameters.AddWithValue("lease", options.LeaseDuration);
        BackgroundJobRoutingClaim? claim;
        await using (var reader = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false))
            claim = await reader.ReadAsync(deadline.Token).ConfigureAwait(false) ? MintClaim(reader) : null;
        await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
        return claim;
    }
}
