using System.IO;
using LearnStack.SharedKernel.Jobs;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Separate provider storage/login; no LearnStack migrations or application credentials.</summary>
internal sealed class BackgroundJobProviderJournalDatabase : IAsyncDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly string _name = "provider_proof_" + Guid.NewGuid().ToString("N");
    private readonly string _login = "provider_client_" + Guid.NewGuid().ToString("N");
    private bool _roleCreated, _databaseCreated;

    private BackgroundJobProviderJournalDatabase(PostgresFixture postgres) => _postgres = postgres;
    internal BackgroundJobProviderSettings Settings { get; private set; } = null!;

    internal static async Task<BackgroundJobProviderJournalDatabase> CreateAsync(PostgresFixture postgres)
    {
        var owner = new BackgroundJobProviderJournalDatabase(postgres);
        // All SQL identifiers/password characters are generated closed alphabets.
        var password = Guid.NewGuid().ToString("N");
        try
        {
            await postgres.ExecuteAsSuperuserAsync($"CREATE ROLE {owner._login} LOGIN PASSWORD '{password}' "
                + "NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT CONNECTION LIMIT 8");
            owner._roleCreated = true;
            await postgres.ExecuteAsSuperuserAsync($"CREATE DATABASE {owner._name} OWNER {owner._login}");
            owner._databaseCreated = true;
            owner.Settings = new(new NpgsqlConnectionStringBuilder(postgres.AppConnectionString)
            {
                Database = owner._name,
                Username = owner._login,
                Password = password,
                Pooling = false,
            }.ConnectionString);
            await using var connection = new NpgsqlConnection(owner.Settings.ConnectionString);
            await connection.OpenAsync();
            await using var schema = new NpgsqlCommand("""
                CREATE TABLE provider_controls(resource_id uuid PRIMARY KEY, mode text NOT NULL);
                CREATE TABLE provider_operations(
                    operation_id uuid PRIMARY KEY,job_id uuid NOT NULL,resource_id uuid NOT NULL,finalized_id uuid NOT NULL);
                CREATE TABLE provider_attempts(
                    operation_id uuid NOT NULL REFERENCES provider_operations,attempt_id uuid NOT NULL,
                    deadline timestamptz NOT NULL,state text NOT NULL CHECK(state IN ('succeeded','failed','cancelled','running','unknown')),
                    policy text NULL CHECK(policy IN ('permanent','transient_repeatable','transient_not_repeatable')),
                    start_fence_closed boolean NOT NULL,evidence_id uuid NULL,reply_fault_spent boolean NOT NULL,
                    completion_origin text NULL CHECK(completion_origin IN ('runtime_token','independent_failure')),
                    runtime_token_observed boolean NOT NULL DEFAULT false,
                    PRIMARY KEY(operation_id,attempt_id),
                    CHECK((state IN ('succeeded','failed','cancelled'))=start_fence_closed),
                    CHECK(start_fence_closed=(evidence_id IS NOT NULL)),
                    CHECK((state IN ('failed','cancelled'))=(policy IS NOT NULL)),
                    CHECK(state<>'cancelled' OR policy<>'permanent'));
                CREATE TABLE provider_effects(operation_id uuid PRIMARY KEY REFERENCES provider_operations,resource_id uuid NOT NULL);
                CREATE TABLE provider_calls(
                    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,operation_id uuid NOT NULL,attempt_id uuid NOT NULL,
                    method text NOT NULL CHECK(method IN ('execute','reconcile')));
                CREATE TABLE provider_cancellation_requests(
                    operation_id uuid NOT NULL,attempt_id uuid NOT NULL,deadline timestamptz NOT NULL,
                    requested_at timestamptz NOT NULL DEFAULT clock_timestamp(),PRIMARY KEY(operation_id,attempt_id));
                """, connection) { CommandTimeout = 5 };
            await schema.ExecuteNonQueryAsync();
            return owner;
        }
        catch { await owner.DisposeAsync(); throw; }
    }

    internal async Task SetModeAsync(Guid resource, string mode)
    {
        if (mode is not ("succeeded" or "permanent" or "repeatable" or "not_repeatable"
            or "cancelled_repeatable" or "cancelled_not_repeatable" or "running" or "unknown" or "lost_reply"
            or "cancel_on_runtime_stop" or "independent_failure_before_stop"))
            throw new ArgumentException("Unknown provider fixture mode.", nameof(mode));
        await using var connection = new NpgsqlConnection(Settings.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO provider_controls(resource_id,mode) VALUES(@id,@mode)
            ON CONFLICT(resource_id) DO UPDATE SET mode=excluded.mode
            """, connection) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", resource);
        command.Parameters.AddWithValue("mode", mode);
        await command.ExecuteNonQueryAsync();
    }

    internal async Task<(long Executes, long Reconciles, long Effects)> CountsAsync(Guid operation)
    {
        await using var connection = new NpgsqlConnection(Settings.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM provider_calls WHERE operation_id=@id AND method='execute'),
                (SELECT count(*) FROM provider_calls WHERE operation_id=@id AND method='reconcile'),
                (SELECT count(*) FROM provider_effects WHERE operation_id=@id)
            """, connection) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", operation);
        await using var row = await command.ExecuteReaderAsync();
        if (!await row.ReadAsync()) throw new InvalidOperationException("Provider observation is missing.");
        return (row.GetInt64(0), row.GetInt64(1), row.GetInt64(2));
    }

    internal async Task<long> CancellationRequestsAsync(Guid operation)
    {
        await using var connection = new NpgsqlConnection(Settings.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM provider_cancellation_requests WHERE operation_id=@id AND requested_at>=deadline", connection)
        { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", operation);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    internal async Task<BackgroundJobProviderCausalFacts> CausalFactsAsync(BackgroundJobExternalContext context)
    {
        await using var connection = new NpgsqlConnection(Settings.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT a.state,a.policy,a.start_fence_closed,a.evidence_id,a.completion_origin,
                a.runtime_token_observed,a.deadline,
                (SELECT count(*) FROM provider_effects WHERE operation_id=a.operation_id)
            FROM provider_attempts a WHERE a.operation_id=@operation AND a.attempt_id=@attempt
            """, connection) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("operation", context.OperationId);
        command.Parameters.AddWithValue("attempt", context.AttemptId.Value);
        await using var row = await command.ExecuteReaderAsync();
        if (!await row.ReadAsync()) throw new InvalidOperationException("The actual provider attempt is missing.");
        return new(row.GetString(0), row.IsDBNull(1) ? null : row.GetString(1), row.GetBoolean(2),
            row.IsDBNull(3) ? null : row.GetGuid(3), row.IsDBNull(4) ? null : row.GetString(4),
            row.GetBoolean(5), row.GetFieldValue<DateTimeOffset>(6), row.GetInt64(7));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_databaseCreated) await _postgres.ExecuteAsSuperuserAsync($"DROP DATABASE {_name} WITH (FORCE)");
        }
        finally
        {
            if (_roleCreated) await _postgres.ExecuteAsSuperuserAsync($"DROP ROLE {_login}");
        }
    }
}

/// <summary>Typed test-provider settings consumed only by the separate compiled Infrastructure adapter.</summary>
public sealed record BackgroundJobProviderSettings(string ConnectionString);

/// <summary>Provider SDK response; not a runtime observation/checkpoint or release capability.</summary>
public sealed record BackgroundJobProviderReport(BackgroundJobExternalState State,
    BackgroundJobExternalFailurePolicy? Policy, Guid? OutcomeId, Guid? EvidenceId);

/// <summary>
/// Independent provider SDK used by the genuine test adapter. Each call owns its
/// provider transaction and durable attempt fence, never a business UoW or sender.
/// </summary>
public sealed class BackgroundJobPositiveProviderClient(BackgroundJobProviderSettings settings,
    BackgroundJobPositiveReconcileProbe probe, BackgroundJobPositiveCausalProviderProbe? causal = null)
{
    public Task<BackgroundJobProviderReport> ExecuteAsync(BackgroundJobExternalContext context,
        Guid resource, Guid finalized, CancellationToken token) => CallAsync(context, resource, finalized, false, token);

    public async Task<BackgroundJobProviderReport> ReconcileAsync(BackgroundJobExternalContext context,
        Guid resource, Guid finalized, CancellationToken token)
    {
        var actual = await CallAsync(context, resource, finalized, true, token);
        await probe.AfterActualCallAsync(context, token);
        return actual;
    }

    private async Task<BackgroundJobProviderReport> CallAsync(BackgroundJobExternalContext context,
        Guid resource, Guid finalized, bool reconcile, CancellationToken token)
    {
        // The inner task has left all actual provider transaction/connection
        // cleanup before accepted work begins waiting for its causal completion.
        var accepted = await CallProviderTransactionAsync(context, resource, finalized, reconcile, token);
        return accepted.CausalMode is { } mode
            ? await CompleteCausalAttemptAsync(context, resource, finalized, mode, token)
            : accepted.Report;
    }

    private async Task<(BackgroundJobProviderReport Report, string? CausalMode)> CallProviderTransactionAsync(
        BackgroundJobExternalContext context, Guid resource, Guid finalized, bool reconcile, CancellationToken token)
    {
        if (!JobApplicationSourceOwner.IsExternal || JobApplicationSourceOwner.Current is not null)
            throw new InvalidOperationException("Provider calls require external denial, without an application owner.");
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using (var called = Command("""
            INSERT INTO provider_calls(operation_id,attempt_id,method) VALUES(@operation,@attempt,@method)
            """))
        {
            called.Parameters.AddWithValue("method", reconcile ? "reconcile" : "execute");
            await called.ExecuteNonQueryAsync(token);
        }
        if (!reconcile)
        {
            await using var ensure = Command("""
                INSERT INTO provider_operations(operation_id,job_id,resource_id,finalized_id)
                VALUES(@operation,@job,@resource,@finalized) ON CONFLICT DO NOTHING
                """);
            await ensure.ExecuteNonQueryAsync(token);
        }
        bool exists;
        await using (var identity = Command("""
            SELECT job_id,resource_id,finalized_id FROM provider_operations WHERE operation_id=@operation FOR UPDATE
            """))
        await using (var row = await identity.ExecuteReaderAsync(token))
        {
            exists = await row.ReadAsync(token);
            if (exists && (row.GetGuid(0) != context.JobId.Value || row.GetGuid(1) != resource || row.GetGuid(2) != finalized))
                throw new InvalidOperationException("Provider operation identity was substituted.");
        }
        if (!exists)
        {
            await transaction.CommitAsync(token);
            return (new(BackgroundJobExternalState.Unknown, null, null, null), null);
        }
        if (reconcile)
        {
            // This real provider request is deliberately not a quiescence
            // receipt: the fixture can truthfully remain Running afterwards.
            await using var cancel = Command("""
                INSERT INTO provider_cancellation_requests(operation_id,attempt_id,deadline)
                SELECT @operation,@attempt,@deadline WHERE clock_timestamp()>=@deadline ON CONFLICT DO NOTHING
                """);
            await cancel.ExecuteNonQueryAsync(token);
        }
        var retained = await ReadAttemptAsync();
        if (retained is not null || reconcile)
        {
            await transaction.CommitAsync(token);
            return (retained ?? new(BackgroundJobExternalState.Unknown, null, null, null), null);
        }
        // Reconciliation never creates acceptance or infers NeverStarted from
        // absence. A duplicate closed attempt returns above without another effect.
        string mode;
        await using (var control = Command("SELECT mode FROM provider_controls WHERE resource_id=@resource"))
            mode = await control.ExecuteScalarAsync(token) as string ?? "succeeded";
        var state = mode switch
        {
            "permanent" or "repeatable" or "not_repeatable" => "failed",
            "cancelled_repeatable" or "cancelled_not_repeatable" => "cancelled",
            "running" or "cancel_on_runtime_stop" or "independent_failure_before_stop" => "running",
            "unknown" => "unknown",
            _ => "succeeded",
        };
        var policy = mode switch
        {
            "permanent" => "permanent",
            "repeatable" or "cancelled_repeatable" => "transient_repeatable",
            "not_repeatable" or "cancelled_not_repeatable" => "transient_not_repeatable",
            _ => null,
        };
        var completed = state is "succeeded" or "failed" or "cancelled";
        await using (var accept = Command("""
            INSERT INTO provider_attempts(operation_id,attempt_id,deadline,state,policy,start_fence_closed,evidence_id,reply_fault_spent)
            SELECT @operation,@attempt,@deadline,@state,@policy,@closed,@evidence,@spent
            WHERE clock_timestamp()<@deadline RETURNING TRUE
            """))
        {
            accept.Parameters.AddWithValue("state", state);
            accept.Parameters.AddWithValue("policy", NpgsqlDbType.Text, (object?)policy ?? DBNull.Value);
            accept.Parameters.AddWithValue("closed", completed);
            accept.Parameters.AddWithValue("evidence", NpgsqlDbType.Uuid, completed ? Guid.NewGuid() : DBNull.Value);
            accept.Parameters.AddWithValue("spent", mode == "lost_reply");
            if (await accept.ExecuteScalarAsync(token) is not true)
            {
                await transaction.CommitAsync(token);
                return (new(BackgroundJobExternalState.Unknown, null, null, null), null);
            }
        }
        if (state == "succeeded")
        {
            await using var effect = Command("""
                INSERT INTO provider_effects(operation_id,resource_id) VALUES(@operation,@resource) ON CONFLICT DO NOTHING
                """);
            await effect.ExecuteNonQueryAsync(token);
        }
        var result = await ReadAttemptAsync() ?? throw new InvalidOperationException("Provider acceptance is missing.");
        await transaction.CommitAsync(token);
        if (mode == "lost_reply") throw new IOException("Provider fixture lost its reply after durable acceptance.");
        return (result, mode is "cancel_on_runtime_stop" or "independent_failure_before_stop" ? mode : null);

        NpgsqlCommand Command(string sql)
        {
            var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 5 };
            command.Parameters.AddWithValue("operation", context.OperationId);
            command.Parameters.AddWithValue("attempt", context.AttemptId.Value);
            command.Parameters.AddWithValue("job", context.JobId.Value);
            command.Parameters.AddWithValue("resource", resource);
            command.Parameters.AddWithValue("finalized", finalized);
            command.Parameters.AddWithValue("deadline", context.Deadline);
            return command;
        }

        async Task<BackgroundJobProviderReport?> ReadAttemptAsync()
        {
            await using var read = Command("""
                SELECT deadline,state,policy,start_fence_closed,evidence_id FROM provider_attempts
                WHERE operation_id=@operation AND attempt_id=@attempt
                """);
            await using var row = await read.ExecuteReaderAsync(token);
            if (!await row.ReadAsync(token)) return null;
            if (row.GetFieldValue<DateTimeOffset>(0) != context.Deadline)
                throw new InvalidOperationException("Provider attempt deadline was substituted.");
            var kind = row.GetString(1) switch
            {
                "succeeded" => BackgroundJobExternalState.Succeeded,
                "failed" => BackgroundJobExternalState.Failed,
                "cancelled" => BackgroundJobExternalState.Cancelled,
                "running" => BackgroundJobExternalState.Running,
                "unknown" => BackgroundJobExternalState.Unknown,
                _ => throw new InvalidOperationException("Unknown provider state."),
            };
            var failurePolicy = row.IsDBNull(2) ? (BackgroundJobExternalFailurePolicy?)null : row.GetString(2) switch
            {
                "permanent" => BackgroundJobExternalFailurePolicy.Permanent,
                "transient_repeatable" => BackgroundJobExternalFailurePolicy.TransientRepeatable,
                "transient_not_repeatable" => BackgroundJobExternalFailurePolicy.TransientNotRepeatable,
                _ => throw new InvalidOperationException("Unknown provider policy."),
            };
            var closed = row.GetBoolean(3);
            if (closed != (kind is BackgroundJobExternalState.Succeeded or BackgroundJobExternalState.Failed or BackgroundJobExternalState.Cancelled)
                || closed == row.IsDBNull(4)) throw new InvalidOperationException("Provider fencing evidence is incomplete.");
            return new(kind, failurePolicy, closed ? resource : null, closed ? row.GetGuid(4) : null);
        }
    }

    private async Task<BackgroundJobProviderReport> CompleteCausalAttemptAsync(BackgroundJobExternalContext context,
        Guid resource, Guid finalized, string mode, CancellationToken token)
    {
        var operation = causal ?? throw new InvalidOperationException("The accepted causal operation has no owner.");
        // This registration signals the ACTUAL supplied private token. Its
        // cancellation is merely an input; it supplies no quiescence itself.
        using var registration = token.Register(static state =>
            ((BackgroundJobPositiveCausalProviderProbe)state!).ObserveRuntimeToken(), operation);
        operation.PublishAccepted(context, token.CanBeCanceled);
        if (mode == "cancel_on_runtime_stop")
            await operation.RuntimeTokenObserved.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        else
            await operation.IndependentFailureReleased.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        // This fixture's real operation consists of this one awaited work item.
        // It has now finished and has no worker/child/callback left capable of
        // an effect. Close the provider's exact attempt fence durably before a
        // completed report is returned; duplicate Execute returns retained facts.
        var cancelled = mode == "cancel_on_runtime_stop";
        if (cancelled && !token.IsCancellationRequested)
            throw new InvalidOperationException("Cancellation completion requires the observed runtime token.");
        var completed = await PersistCausalCompletionAsync(context, resource, finalized, cancelled, token.IsCancellationRequested);
        operation.PublishCompletionStored();
        // This response barrier owns no live connector/transaction and can no
        // longer cause an effect. It lets an independent failure finish before
        // stop while its actual provider method returns only after stop.
        await operation.ReportReleased.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        return completed;
    }

    private async Task<BackgroundJobProviderReport> PersistCausalCompletionAsync(BackgroundJobExternalContext context,
        Guid resource, Guid finalized, bool cancelled, bool runtimeTokenObserved)
    {
        var evidence = Guid.NewGuid();
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        // The canceled runtime token cannot prevent the provider from recording
        // its own completed cleanup. This separate finite provider budget is
        // not permission to issue any runtime application/control checkpoint.
        await connection.OpenAsync(completion.Token);
        await using var transaction = await connection.BeginTransactionAsync(completion.Token);
        await using var complete = new NpgsqlCommand("""
            UPDATE provider_attempts a SET state=@state,policy=@policy,start_fence_closed=true,evidence_id=@evidence,
                completion_origin=@origin,runtime_token_observed=@observed
            FROM provider_operations o WHERE o.operation_id=a.operation_id AND o.operation_id=@operation
                AND o.job_id=@job AND o.resource_id=@resource AND o.finalized_id=@finalized
                AND a.attempt_id=@attempt AND a.deadline=@deadline AND a.state='running'
                AND NOT a.start_fence_closed AND a.evidence_id IS NULL AND a.policy IS NULL
            RETURNING a.evidence_id
            """, connection, transaction) { CommandTimeout = 5 };
        complete.Parameters.AddWithValue("operation", context.OperationId);
        complete.Parameters.AddWithValue("job", context.JobId.Value);
        complete.Parameters.AddWithValue("resource", resource);
        complete.Parameters.AddWithValue("finalized", finalized);
        complete.Parameters.AddWithValue("attempt", context.AttemptId.Value);
        complete.Parameters.AddWithValue("deadline", context.Deadline);
        complete.Parameters.AddWithValue("state", cancelled ? "cancelled" : "failed");
        complete.Parameters.AddWithValue("policy", cancelled ? "transient_not_repeatable" : "permanent");
        complete.Parameters.AddWithValue("origin", cancelled ? "runtime_token" : "independent_failure");
        complete.Parameters.AddWithValue("observed", runtimeTokenObserved);
        complete.Parameters.AddWithValue("evidence", evidence);
        if (await complete.ExecuteScalarAsync(completion.Token) is not Guid stored || stored != evidence)
            throw new InvalidOperationException("The causal completion did not close its exact accepted attempt.");
        await transaction.CommitAsync(completion.Token);
        return new(cancelled ? BackgroundJobExternalState.Cancelled : BackgroundJobExternalState.Failed,
            cancelled ? BackgroundJobExternalFailurePolicy.TransientNotRepeatable : BackgroundJobExternalFailurePolicy.Permanent,
            resource, evidence);
    }
}

internal sealed record BackgroundJobProviderCausalFacts(string State, string? Policy, bool Closed,
    Guid? Evidence, string? Origin, bool RuntimeTokenObserved, DateTimeOffset Deadline, long Effects);

/// <summary>Original-provider lifecycle input/observation only; never reused by the restarted host.</summary>
public sealed class BackgroundJobPositiveCausalProviderProbe
{
    private readonly TaskCompletionSource<BackgroundJobExternalContext> _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _tokenObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _independentFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completionStored = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _reportReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<BackgroundJobExternalContext> Accepted => _accepted.Task;
    public Task RuntimeTokenObserved => _tokenObserved.Task;
    public Task IndependentFailureReleased => _independentFailure.Task;
    public Task CompletionStored => _completionStored.Task;
    public Task ReportReleased => _reportReleased.Task;
    public bool RuntimeTokenCanBeCanceled { get; private set; }
    public void ReleaseIndependentFailure() => _independentFailure.TrySetResult();
    public void ReleaseReport() => _reportReleased.TrySetResult();
    internal void PublishCompletionStored() => _completionStored.TrySetResult();
    internal void ObserveRuntimeToken() => _tokenObserved.TrySetResult();
    internal void PublishAccepted(BackgroundJobExternalContext context, bool tokenCanBeCanceled)
    {
        RuntimeTokenCanBeCanceled = tokenCanBeCanceled;
        if (!_accepted.TrySetResult(context)) throw new InvalidOperationException("Only one actual causal Execute may be accepted.");
    }
}

/// <summary>Explicit adapter-qualification mutant; no runtime behavior is expected to certify external truth.</summary>
public sealed class BackgroundJobPositiveCausalReportMutation(bool lieOnReconcile = false)
{
    private int _applied;
    public bool LieOnReconcile { get; } = lieOnReconcile;
    public int Applied => Volatile.Read(ref _applied);
    public void RecordApplied() => Interlocked.Increment(ref _applied);
}

/// <summary>Finite test barrier after the real provider transaction; no runtime observation or cleanup capability.</summary>
public sealed class BackgroundJobPositiveReconcileProbe
{
    private readonly string _mode;
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public BackgroundJobPositiveReconcileProbe(string mode = "none")
    {
        if (mode is not ("none" or "fault" or "timeout" or "late_success" or "late_running" or "host_stop"))
            throw new ArgumentException("Unknown reconcile fixture mode.", nameof(mode));
        _mode = mode;
        if (mode is "none" or "fault") _release.SetResult();
    }
    public Task Entered => _entered.Task;
    public BackgroundJobExternalContext? Context { get; private set; }
    public bool TokenWasCancelled { get; private set; }
    public void Release() => _release.TrySetResult();
    public async Task AfterActualCallAsync(BackgroundJobExternalContext context, CancellationToken token)
    {
        Context = context;
        _entered.TrySetResult();
        await _release.Task;
        TokenWasCancelled = token.IsCancellationRequested;
        if (_mode == "fault") throw new IOException("Explicit provider bridge fixture fault.");
        if (_mode is not ("late_success" or "late_running")) token.ThrowIfCancellationRequested();
    }
}
