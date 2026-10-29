using System.Data;
using System.Data.Common;
using System.Runtime.ExceptionServices;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Idempotency;
using Npgsql;

namespace LearnStack.Infrastructure.Idempotency;

public sealed partial class PostgresIdempotencyStore
{
    private async Task<T> OwnAsync<T>(TenantId tenantId,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        NpgsqlConnection? connection = null;
        NpgsqlTransaction? transaction = null;
        Exception? failure = null;
        var cleanupFailed = false;
        var commitStarted = false;
        T result = default!;
        try
        {
            connection = await _dataSource.Value.OpenConnectionAsync(deadline.Token);
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, deadline.Token);
            // The first authority-bearing statement is this transaction-local,
            // parameterized announcement on the existing guarded application pool.
            await ExecuteAsync(connection, transaction, """
                SELECT set_config('app.tenant_id',@tenant,true),set_config('lock_timeout','2s',true),
                    set_config('statement_timeout','10s',true)
                """, deadline.Token, ("tenant", tenantId.Value.ToString("D")));
            result = await operation(connection, transaction, deadline.Token);
            await ObserveAsync(IdempotencyStoreStage.BeforeCommit);
            commitStarted = true;
            await transaction.CommitAsync(deadline.Token);
            await ObserveAsync(IdempotencyStoreStage.AfterCommit);
        }
        catch (Exception caught) { failure = caught; }
        finally
        {
            // Await real disposal even when the operation deadline expired. A
            // cancelled wait is not evidence of release. Cleanup observations may
            // fail, but neither observation is allowed to skip actual disposal.
            if (transaction is not null)
            {
                try { await ObserveAsync(IdempotencyStoreStage.BeforeTransactionDispose); }
                catch (Exception) { cleanupFailed = true; }
                try { await transaction.DisposeAsync(); }
                catch (Exception) { cleanupFailed = true; }
                try { await ObserveAsync(IdempotencyStoreStage.AfterTransactionDispose); }
                catch (Exception) { cleanupFailed = true; }
            }
            if (connection is not null)
            {
                try { await ObserveAsync(IdempotencyStoreStage.BeforeConnectionDispose); }
                catch (Exception) { cleanupFailed = true; }
                try { await connection.DisposeAsync(); }
                catch (Exception) { cleanupFailed = true; }
                try { await ObserveAsync(IdempotencyStoreStage.AfterConnectionDispose); }
                catch (Exception) { cleanupFailed = true; }
            }
        }
        // No acquired result can escape unknown COMMIT or incomplete cleanup.
        // Provider details never become an InnerException or a diagnostic value.
        if (cleanupFailed || (failure is not null && (commitStarted || IsStorageFailure(failure)
                || failure is OperationCanceledException && !cancellationToken.IsCancellationRequested)))
            throw new IdempotencyStoreException();
        if (failure is OperationCanceledException)
            throw new OperationCanceledException("Idempotency storage was cancelled.", cancellationToken);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    private Task ObserveAsync(IdempotencyStoreStage stage) => _observe?.Invoke(stage) ?? Task.CompletedTask;

    private static bool IsStorageFailure(Exception failure) => failure is DbException or TimeoutException
        or IdempotencyStoreException || failure is InvalidOperationException { InnerException: DbException or TimeoutException };

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql) =>
        new(sql, connection, transaction) { CommandTimeout = 10 };

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object Value)[] values)
    {
        await using var command = Command(connection, transaction, sql);
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, sql);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? default! : (T)value;
    }
}

internal enum IdempotencyStoreStage
{
    CapacityHeld,
    BeforeCommit,
    AfterCommit,
    BeforeTransactionDispose,
    AfterTransactionDispose,
    BeforeConnectionDispose,
    AfterConnectionDispose,
}
