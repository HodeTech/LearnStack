using System.Runtime.ExceptionServices;
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
    BackgroundJobRuntimeOptions options, IGuidFactory ids,
    BackgroundJobRuntimeGate? runtimeGate = null) : BackgroundJobRoutingClaim.MintingSource
{
    private readonly PrivilegedDataSource _source = source;

    public async Task<BackgroundJobRoutingClaim?> TryClaimAsync(BackgroundJobId jobId, long wakeGeneration,
        CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        var result = await TryClaimOwnedAsync(jobId, wakeGeneration, cancellationToken).ConfigureAwait(false);
        result.ThrowIfFailed();
        return result.Claim;
    }

    internal async Task<OwnedRoutingRead> TryClaimOwnedAsync(BackgroundJobId jobId, long wakeGeneration,
        CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        options.Validate();
        if (!jobId.IsInitialized() || jobId.Value.Version != 7 || wakeGeneration < 1)
            throw new ArgumentException("A job wakeup requires its opaque identity and positive generation.");
        var active = definitions.DefinitionKeys.Where(key => definitions.TryGet(key, out var definition)
            && definition.Readiness == BackgroundJobDefinitionReadiness.Active
            && definition.Profile == BackgroundJobExecutionProfile.Transactional && definition.CapacityGroup is null).ToArray();
        if (active.Length == 0) return new(null, null, null);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.SqlTimeout);
        NpgsqlConnection? connection = null;
        NpgsqlTransaction? transaction = null;
        NpgsqlCommand? command = null;
        NpgsqlDataReader? reader = null;
        BackgroundJobRoutingClaim? claim = null;
        ExceptionDispatchInfo? failure = null;
        var readerDisposalAttempted = false;
        var cleanupFailed = false;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            // The gate read is the acquisition linearization point, including
            // ordinary host stop. Queuing this owned operation admits no SQL.
            if (runtimeGate?.TryAdmit() == false) return new(null, null, null);
            connection = await _source.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
            transaction = await connection.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
            command = new NpgsqlCommand("""
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
            reader = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
            var candidate = await reader.ReadAsync(deadline.Token).ConfigureAwait(false) ? MintClaim(reader) : null;
            // COMMIT requires the actual reader to finish. A disposal failure
            // closes this operation and leaves every acquired object retained.
            readerDisposalAttempted = true;
            try { await reader.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) when (Catchable(exception))
            {
                cleanupFailed = true;
                failure = ExceptionDispatchInfo.Capture(exception);
            }
            if (failure is null)
            {
                await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
                // An uncertain COMMIT never grants an executable routing claim.
                claim = candidate;
            }
        }
        catch (Exception exception) when (Catchable(exception))
        {
            failure ??= ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            // These are actual joins, without a caller timeout or cancellation
            // shortcut. A fault in one disposal cannot skip the remaining owners.
            if (reader is not null && !readerDisposalAttempted)
            {
                try { await reader.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) when (Catchable(exception))
                {
                    cleanupFailed = true;
                    failure ??= ExceptionDispatchInfo.Capture(exception);
                }
            }
            if (command is not null)
            {
                try { await command.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) when (Catchable(exception))
                {
                    cleanupFailed = true;
                    failure ??= ExceptionDispatchInfo.Capture(exception);
                }
            }
            if (transaction is not null)
            {
                try { await transaction.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) when (Catchable(exception))
                {
                    cleanupFailed = true;
                    failure ??= ExceptionDispatchInfo.Capture(exception);
                }
            }
            if (connection is not null)
            {
                try { await connection.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) when (Catchable(exception))
                {
                    cleanupFailed = true;
                    failure ??= ExceptionDispatchInfo.Capture(exception);
                }
            }
        }
        return new(claim, failure, cleanupFailed ? (reader, command, transaction, connection) : null);
    }

    private static bool Catchable(Exception exception) => exception.GetType() != typeof(OutOfMemoryException)
        && exception.GetType() != typeof(StackOverflowException);

    /// <summary>A completed read distinguishes SQL failure from unresolved actual resource cleanup.</summary>
    internal sealed class OwnedRoutingRead
    {
        private readonly ExceptionDispatchInfo? _failure;
        // Retaining this result retains the actual owners, including objects whose
        // disposal threw. It deliberately supplies no retry/disposal authority.
        private readonly (NpgsqlDataReader? Reader, NpgsqlCommand? Command,
            NpgsqlTransaction? Transaction, NpgsqlConnection? Connection)? _unresolvedResources;

        internal OwnedRoutingRead(BackgroundJobRoutingClaim? claim, ExceptionDispatchInfo? failure,
            (NpgsqlDataReader? Reader, NpgsqlCommand? Command,
                NpgsqlTransaction? Transaction, NpgsqlConnection? Connection)? unresolvedResources)
        {
            Claim = claim;
            _failure = failure;
            _unresolvedResources = unresolvedResources;
        }

        internal BackgroundJobRoutingClaim? Claim { get; }
        internal bool CleanupConfirmed => _unresolvedResources is null;
        internal bool Failed => _failure is not null;

        internal void ThrowIfFailed() => _failure?.Throw();
    }
}
