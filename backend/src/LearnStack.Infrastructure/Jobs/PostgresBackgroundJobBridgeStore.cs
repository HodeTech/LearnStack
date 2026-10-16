using System.Diagnostics.CodeAnalysis;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Npgsql;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>An opaque transport obligation, minted only by a committed bridge acquisition.</summary>
internal sealed class BackgroundJobBridgeClaim
{
    internal BackgroundJobBridgeClaim(NpgsqlDataReader row, BackgroundJobDefinitionRegistry definitions)
    {
        JobId = BackgroundJobId.From(row.GetGuid(0));
        DefinitionKey = row.GetString(1);
        Queue = row.GetString(2);
        WakeGeneration = row.GetInt64(3);
        Token = row.GetGuid(4);
        BridgeUntil = row.GetFieldValue<DateTimeOffset>(5);
        AvailableAfter = row.GetFieldValue<DateTimeOffset>(6);
        DatabaseNow = row.GetFieldValue<DateTimeOffset>(7);
        CanHandoff = definitions.TryGet(DefinitionKey, out var definition)
            && definition.Readiness == BackgroundJobDefinitionReadiness.Active
            && definition.Queue == Queue
            && (definition.Profile == BackgroundJobExecutionProfile.Transactional ? "transactional" : "external") == row.GetString(8);
        if (!JobId.IsInitialized() || JobId.Value.Version != 7 || WakeGeneration < 1 || Token == Guid.Empty)
            throw new InvalidOperationException("The bridge returned an invalid obligation.");
    }

    internal BackgroundJobId JobId { get; }
    internal string DefinitionKey { get; }
    internal string Queue { get; }
    internal long WakeGeneration { get; }
    internal Guid Token { get; }
    internal DateTimeOffset BridgeUntil { get; }
    internal DateTimeOffset AvailableAfter { get; }
    internal DateTimeOffset DatabaseNow { get; }
    internal bool CanHandoff { get; }
}

internal enum BackgroundJobBridgeDeferral { DeploymentMismatch, CreationFailed }

/// <summary>One confined SQL lane; no business payload, attempt, generation or terminal mutation.</summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The process-owned semaphore never creates a wait handle and remains live with retained operations.")]
public sealed class PostgresBackgroundJobBridgeStore(PrivilegedDataSource source,
    BackgroundJobDefinitionRegistry definitions, BackgroundJobRuntimeOptions options, IGuidFactory ids,
    BackgroundJobRuntimeGate? runtimeGate = null)
{
    private readonly SemaphoreSlim _sqlLane = new(1, 1);
    private readonly object _acquisitionGate = new();
    private bool _stopping;

    internal async Task<BackgroundJobBridgeClaim?> TryClaimAsync(IReadOnlyCollection<BackgroundJobId> retained,
        CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(retained);
        options.Validate();
        if (retained.Count > BackgroundJobRuntimeOptions.MaxOutstandingBridgeCalls)
            throw new ArgumentException("The retained bridge inventory exceeds its bound.", nameof(retained));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.SqlTimeout);
        await _sqlLane.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(acquisition: true, deadline.Token).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("""
                WITH candidate AS (
                    SELECT id FROM background_jobs
                    WHERE phase IN ('pending','finalize_pending') AND processed_at IS NULL AND dead_lettered_at IS NULL
                        AND terminal_reason IS NULL AND ownership_token IS NULL
                        AND handoff_check_at <= statement_timestamp()
                        AND (bridge_token IS NULL OR bridge_until <= statement_timestamp())
                        AND ((phase = 'pending' AND normal_attempts < 6)
                            OR (phase = 'finalize_pending' AND finalize_attempts < 6))
                        AND NOT (id = ANY(@retained))
                    ORDER BY handoff_check_at, created_at, id LIMIT 1 FOR UPDATE SKIP LOCKED
                )
                UPDATE background_jobs SET bridge_token = @token, bridge_until = clock_timestamp() + @lease,
                    handoff_check_at = clock_timestamp() + @lease
                WHERE id = (SELECT id FROM candidate)
                RETURNING id, definition_key, queue, wake_generation, bridge_token, bridge_until,
                    available_after, clock_timestamp(), profile
                """, connection, transaction) { CommandTimeout = TimeoutSeconds };
            command.Parameters.AddWithValue("retained", retained.Select(id => id.Value).ToArray());
            command.Parameters.AddWithValue("token", ids.NewUuidV4());
            command.Parameters.AddWithValue("lease", options.LeaseDuration);
            BackgroundJobBridgeClaim? claim;
            await using (var row = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false))
                claim = await row.ReadAsync(deadline.Token).ConfigureAwait(false) ? new(row, definitions) : null;
            await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
            return claim;
        }
        finally { _sqlLane.Release(); }
    }

    internal Task<bool> AcknowledgeAsync(BackgroundJobBridgeClaim claim, string vendorId,
        CancellationToken cancellationToken = default)
    {
        if (vendorId is not { Length: > 0 and <= 20 } || vendorId[0] == '0'
            || vendorId.Any(character => character is < '0' or > '9'))
            throw new ArgumentException("A vendor hint requires a bounded positive numeric identity.", nameof(vendorId));
        return UpdateAsync(claim, vendorId, null, cancellationToken);
    }

    internal Task<bool> DeferAsync(BackgroundJobBridgeClaim claim, BackgroundJobBridgeDeferral reason,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return UpdateAsync(claim, null, reason, cancellationToken);
    }

    private async Task<bool> UpdateAsync(BackgroundJobBridgeClaim claim, string? vendorId,
        BackgroundJobBridgeDeferral? reason, CancellationToken cancellationToken)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(claim);
        options.Validate();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.SqlTimeout);
        await _sqlLane.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(acquisition: false, deadline.Token).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
            // Obtain the row lock first. Time-dependent ACK/deferral fences run
            // in the separate UPDATE after any lock wait has actually ended.
            await using (var locked = new NpgsqlCommand(
                "SELECT id FROM background_jobs WHERE id = @id FOR UPDATE", connection, transaction)
            { CommandTimeout = TimeoutSeconds })
            {
                locked.Parameters.AddWithValue("id", claim.JobId.Value);
                if (await locked.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false) is null) return false;
            }
            // A callback can already own execution. ACK touches only its still-matching
            // bridge fence; requiring pending/NULL ownership here would lose that ACK.
            await using var command = new NpgsqlCommand("""
                UPDATE background_jobs SET vendor_job_id = coalesce(@vendor, vendor_job_id),
                    bridge_token = NULL, bridge_until = NULL,
                    handoff_check_at = CASE WHEN @vendor IS NULL THEN clock_timestamp() + @grace
                        ELSE greatest(clock_timestamp(), available_after) + @grace END,
                    last_error = CASE WHEN @vendor IS NULL THEN @error
                        WHEN phase IN ('pending','finalize_pending') THEN NULL ELSE last_error END
                WHERE id = @id AND wake_generation = @generation AND bridge_token = @token
                    AND bridge_until > clock_timestamp() AND processed_at IS NULL AND dead_lettered_at IS NULL
                    AND terminal_reason IS NULL AND phase <> 'quarantined'
                """, connection, transaction) { CommandTimeout = TimeoutSeconds };
            command.Parameters.AddWithValue("id", claim.JobId.Value);
            command.Parameters.AddWithValue("generation", claim.WakeGeneration);
            command.Parameters.AddWithValue("token", claim.Token);
            command.Parameters.AddWithValue("grace", options.HandoffRepairGrace);
            command.Parameters.Add("vendor", NpgsqlTypes.NpgsqlDbType.Text).Value = (object?)vendorId ?? DBNull.Value;
            command.Parameters.Add("error", NpgsqlTypes.NpgsqlDbType.Text).Value = reason switch
            {
                BackgroundJobBridgeDeferral.DeploymentMismatch => "deployment_mismatch",
                BackgroundJobBridgeDeferral.CreationFailed => "dependency_unavailable",
                _ => DBNull.Value,
            };
            var changed = await command.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false) == 1;
            await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
            return changed;
        }
        finally { _sqlLane.Release(); }
    }

    private int TimeoutSeconds => (int)Math.Ceiling(options.SqlTimeout.TotalSeconds);

    internal void StopAcquisitions()
    {
        lock (_acquisitionGate) _stopping = true;
    }

    private ValueTask<NpgsqlConnection> OpenConnectionAsync(bool acquisition, CancellationToken cancellationToken)
    {
        lock (_acquisitionGate)
        {
            if (_stopping || runtimeGate is not null
                && !(acquisition ? runtimeGate.TryAdmit() : runtimeGate.TryAdmitStorage()))
                throw new InvalidOperationException("The bridge acquisition gate is closed.");
            return source.OpenConnectionAsync(cancellationToken);
        }
    }
}
