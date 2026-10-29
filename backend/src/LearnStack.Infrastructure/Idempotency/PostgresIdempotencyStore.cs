using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Idempotency;
using Npgsql;

namespace LearnStack.Infrastructure.Idempotency;

/// <summary>Durable app-role claims, isolated from business ownership and bounded to 1,000 physical tenant slots.</summary>
public sealed partial class PostgresIdempotencyStore : IIdempotencyStore
{
    public const int MaxEntriesPerTenant = 1_000;
    public const int MaxStoredResponseBytes = 256 * 1024;
    public const int MaxProtectedResponseBytes = 1024;
    private readonly Lazy<NpgsqlDataSource> _dataSource;
    private readonly Func<IdempotencyStoreStage, Task>? _observe;

    public PostgresIdempotencyStore(Lazy<NpgsqlDataSource> dataSource) : this(dataSource, null) { }

    // Internal observation only: tests can hold or fault completion boundaries,
    // but cannot substitute SQL, connections, tenant context or ownership tokens.
    internal PostgresIdempotencyStore(Lazy<NpgsqlDataSource> dataSource, Func<IdempotencyStoreStage, Task>? observe)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _observe = observe;
    }

    public Task<IdempotentResponse?> ReadCompletedAsync(TenantId tenantId, string key, string fingerprint,
        CancellationToken cancellationToken)
    {
        Guard(tenantId, key);
        ArgumentNullException.ThrowIfNull(fingerprint);
        if (!JsonValue.IsStorableText(fingerprint)) throw new ArgumentException("Idempotency fingerprint is invalid.", nameof(fingerprint));
        return OwnAsync<IdempotentResponse?>(tenantId, async (connection, transaction, token) =>
        {
            var existing = await ReadAsync(connection, transaction, key, fingerprint, locked: false, token);
            return existing is { Outcome: IdempotencyClaim.Completed } ? existing.Stored : null;
        }, cancellationToken);
    }

    public Task<IdempotencyClaimResult> TryClaimAsync(TenantId tenantId, string key, string fingerprint,
        CancellationToken cancellationToken)
    {
        Guard(tenantId, key);
        ArgumentNullException.ThrowIfNull(fingerprint);
        if (!JsonValue.IsStorableText(fingerprint)) throw new ArgumentException("Idempotency fingerprint is invalid.", nameof(fingerprint));
        return OwnAsync(tenantId, async (connection, transaction, token) =>
        {
            // Never take a key row lock before capacity: reclaimers take capacity
            // then a recyclable row, so the reverse order would form a deadlock.
            var existing = await ReadAsync(connection, transaction, key, fingerprint, locked: false, token);
            if (existing is not null) return existing;
            await ExecuteAsync(connection, transaction, "SELECT pg_catalog.pg_advisory_xact_lock(@capacity)", token,
                ("capacity", IdempotencyTenantCapacityKey.ForTenant(tenantId)));
            await ObserveAsync(IdempotencyStoreStage.CapacityHeld);

            // A fresh READ COMMITTED statement after the wait owns the decision.
            existing = await ReadAsync(connection, transaction, key, fingerprint, locked: true, token);
            if (existing is not null) return existing;
            var claim = Guid.NewGuid(); // Fencing nonce, not an aggregate identifier.
            var renewed = await ResetAsync(connection, transaction, key, key, fingerprint, claim, token);
            if (!renewed)
            {
                var expired = await ScalarAsync<string?>(connection, transaction, """
                    SELECT key FROM public.idempotency_keys
                    WHERE expires_at <= clock_timestamp()
                    ORDER BY expires_at, key LIMIT 1 FOR UPDATE
                    """, token);
                if (expired is not null)
                {
                    if (!await ResetAsync(connection, transaction, expired, key, fingerprint, claim, token))
                        throw new IdempotencyStoreException();
                }
                else
                {
                    var count = await ScalarAsync<long>(connection, transaction,
                        "SELECT count(*) FROM public.idempotency_keys", token);
                    if (count >= MaxEntriesPerTenant) return Result(IdempotencyClaim.CapacityExhausted);
                    await ExecuteAsync(connection, transaction, """
                        WITH instant AS (SELECT clock_timestamp() AS at)
                        INSERT INTO public.idempotency_keys(tenant_id,key,fingerprint,claim_token,state,claimed_at,expires_at)
                        SELECT @tenant,@key,@fingerprint,@claim,'in_flight',at,at + interval '5 minutes' FROM instant
                        """, token, ("tenant", tenantId.Value), ("key", key), ("fingerprint", fingerprint), ("claim", claim));
                }
            }
            return new IdempotencyClaimResult(IdempotencyClaim.Acquired, claim, null);
        }, cancellationToken);
    }

    public Task<bool> CompleteAsync(TenantId tenantId, string key, Guid token, IdempotentResponse? response,
        CancellationToken cancellationToken)
    {
        Guard(tenantId, key);
        if (token == Guid.Empty) throw new ArgumentException("Idempotency claim token is invalid.", nameof(token));
        var stored = PostgresIdempotencyResponse.Capture(response);
        return OwnAsync(tenantId, async (connection, transaction, cancellation) =>
        {
            await using var command = Command(connection, transaction, """
                UPDATE public.idempotency_keys
                SET state=@state,status_code=@status,content_type=@content,headers=@headers,body=@body,
                    replay_target_kind=@kind,replay_target_id=@target,
                    expires_at=clock_timestamp() + interval '24 hours'
                WHERE key=@key AND claim_token=@claim AND state='in_flight'
                """);
            command.Parameters.AddWithValue("key", key);
            command.Parameters.AddWithValue("claim", token);
            PostgresIdempotencyResponse.AddParameters(command, stored);
            return await command.ExecuteNonQueryAsync(cancellation) == 1;
        }, cancellationToken);
    }

    public Task<bool> AbandonAsync(TenantId tenantId, string key, Guid token, CancellationToken cancellationToken)
    {
        Guard(tenantId, key);
        if (token == Guid.Empty) throw new ArgumentException("Idempotency claim token is invalid.", nameof(token));
        return OwnAsync(tenantId, async (connection, transaction, cancellation) =>
            await ExecuteAsync(connection, transaction, """
                UPDATE public.idempotency_keys SET expires_at=clock_timestamp() - interval '1 microsecond',
                    status_code=NULL,content_type=NULL,headers=NULL,body=NULL,replay_target_kind=NULL,replay_target_id=NULL
                WHERE key=@key AND claim_token=@claim AND state='in_flight'
                """, cancellation, ("key", key), ("claim", token)) == 1, cancellationToken);
    }

    private static async Task<IdempotencyClaimResult?> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string key, string fingerprint, bool locked, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT fingerprint,expires_at <= clock_timestamp(),state,status_code,content_type,headers::text,body,
                replay_target_kind,replay_target_id
            FROM public.idempotency_keys WHERE key=@key
            """ + (locked ? " FOR UPDATE" : string.Empty));
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        // Mismatch wins even for an expired row. No state or response is decoded
        // on this path, and nothing about the incumbent outcome is disclosed.
        if (!string.Equals(reader.GetString(0), fingerprint, StringComparison.Ordinal)) return Result(IdempotencyClaim.Mismatched);
        if (reader.GetBoolean(1)) return null;
        return reader.GetString(2) switch
        {
            "in_flight" => Result(IdempotencyClaim.InFlight),
            "unreplayable" => Result(IdempotencyClaim.Unreplayable),
            "completed" => new IdempotencyClaimResult(IdempotencyClaim.Completed, Guid.Empty, PostgresIdempotencyResponse.Read(reader)),
            _ => throw new IdempotencyStoreException(),
        };
    }

    private static async Task<bool> ResetAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string oldKey, string key, string fingerprint, Guid claim, CancellationToken cancellationToken) =>
        await ExecuteAsync(connection, transaction, """
            WITH instant AS (SELECT clock_timestamp() AS at)
            UPDATE public.idempotency_keys SET key=@key,fingerprint=@fingerprint,claim_token=@claim,state='in_flight',
                status_code=NULL,content_type=NULL,headers=NULL,body=NULL,replay_target_kind=NULL,replay_target_id=NULL,
                claimed_at=instant.at,expires_at=instant.at + interval '5 minutes'
            FROM instant WHERE key=@old AND expires_at <= instant.at
            """, cancellationToken, ("old", oldKey), ("key", key), ("fingerprint", fingerprint), ("claim", claim)) == 1;

    private static IdempotencyClaimResult Result(IdempotencyClaim outcome) => new(outcome, Guid.Empty, null);

    private static void Guard(TenantId tenantId, string key)
    {
        if (!tenantId.IsInitialized() || tenantId.Value == Guid.Empty || tenantId == TenantId.PlatformSentinel)
            throw new ArgumentException("Idempotency tenant is invalid.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(key) || key.Length is < 8 or > 128 || !JsonValue.IsStorableText(key))
            throw new ArgumentException("Idempotency key is invalid.", nameof(key));
    }
}
