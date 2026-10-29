using System.Text;
using System.Text.Json;
using LearnStack.Infrastructure.Idempotency;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Identity;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class PostgresIdempotencyStoreTests(SchemaFixture schema) : IAsyncLifetime
{
    private static readonly string[] OriginalLocationValues = ["/api/v1/example/unchanged"];
    private static readonly string[] WholeOutcomeValues = ["one", "two", "Türkçe"];
    private static readonly string[] BoundaryHeaderValues = ["é", "a"];
    private static readonly string[] LegacyLocationValues = ["/original/location"];
    private readonly List<TenantId> _tenants = [];
    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var cleanup = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.PlatformConnectionString);
        foreach (var tenant in _tenants)
        {
            await using var command = new NpgsqlCommand("DELETE FROM public.idempotency_keys WHERE tenant_id=@tenant", cleanup);
            command.Parameters.AddWithValue("tenant", tenant.Value);
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Independent_stores_share_claims_and_preserve_whole_generic_response_bytes_and_headers()
    {
        var tenant = Tenant();
        await using var firstSource = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        await using var secondSource = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        var first = Store(firstSource);
        var second = Store(secondSource);
        var claim = await first.TryClaimAsync(tenant, "whole-response", "fingerprint", CancellationToken.None);
        Assert.Equal(IdempotencyClaim.Acquired, claim.Outcome);
        Assert.NotEqual(Guid.Empty, claim.Token);
        Assert.Equal(IdempotencyClaim.InFlight, (await second.TryClaimAsync(tenant, "whole-response", "fingerprint", CancellationToken.None)).Outcome);
        var body = Enumerable.Range(0, 250_000).Select(value => (byte)(value % 256)).ToArray();
        var headers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Location"] = ["/api/v1/example/unchanged"],
            ["X-Outcome"] = ["one", "two", "Türkçe"],
        };
        var response = new IdempotentResponse(201, "application/octet-stream", headers, body);
        var completion = first.CompleteAsync(tenant, "whole-response", claim.Token, response, CancellationToken.None);
        body[0] = 99;
        headers["Location"] = ["/must-not-replace-frozen-value"];
        Assert.True(await completion);
        var replay = await second.TryClaimAsync(tenant, "whole-response", "fingerprint", CancellationToken.None);
        Assert.Equal(IdempotencyClaim.Completed, replay.Outcome);
        Assert.Equal(Guid.Empty, replay.Token);
        Assert.NotNull(replay.Stored);
        Assert.Equal(201, replay.Stored.StatusCode);
        Assert.Equal("application/octet-stream", replay.Stored.ContentType);
        Assert.Equal((byte)0, replay.Stored.Body[0]);
        Assert.Equal(body.AsSpan(1).ToArray(), replay.Stored.Body.AsSpan(1).ToArray());
        Assert.Equal(OriginalLocationValues, replay.Stored.Headers["Location"]);
        Assert.Equal(WholeOutcomeValues, replay.Stored.Headers["X-Outcome"]);
        Assert.Null(replay.Stored.ReplayTarget);
        var before = await SnapshotAsync(tenant);
        Assert.False(await first.CompleteAsync(tenant, "whole-response", claim.Token, null, CancellationToken.None));
        Assert.False(await first.AbandonAsync(tenant, "whole-response", claim.Token, CancellationToken.None));
        Assert.Equal(before, await SnapshotAsync(tenant));
    }

    [Theory]
    [InlineData("in_flight", false)]
    [InlineData("in_flight", true)]
    [InlineData("completed", false)]
    [InlineData("completed", true)]
    [InlineData("unreplayable", false)]
    [InlineData("unreplayable", true)]
    public async Task Fingerprint_mismatch_precedes_every_state_and_expiry_without_disclosing_or_changing_it(string state, bool expired)
    {
        var tenant = Tenant();
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        var store = Store(source);
        var claim = await store.TryClaimAsync(tenant, "opaque-mismatch", "original", CancellationToken.None);
        if (state != "in_flight")
            Assert.True(await store.CompleteAsync(tenant, "opaque-mismatch", claim.Token, state == "completed" ? Receipt() : null, CancellationToken.None));
        if (expired) await ExpireAsync(tenant, "opaque-mismatch");
        var before = await SnapshotAsync(tenant);
        var mismatch = await store.TryClaimAsync(tenant, "opaque-mismatch", "different", CancellationToken.None);
        Assert.Equal(IdempotencyClaim.Mismatched, mismatch.Outcome);
        Assert.Equal(Guid.Empty, mismatch.Token);
        Assert.Null(mismatch.Stored);
        Assert.Equal(before, await SnapshotAsync(tenant));
    }

    [Fact]
    public async Task Expired_same_key_and_released_key_receive_new_tokens_and_old_owners_cannot_change_them()
    {
        var tenant = Tenant();
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        var store = Store(source);
        var first = await store.TryClaimAsync(tenant, "expired-claim", "same", CancellationToken.None);
        await ExpireAsync(tenant, "expired-claim");
        var next = await store.TryClaimAsync(tenant, "expired-claim", "same", CancellationToken.None);
        Assert.Equal(IdempotencyClaim.Acquired, next.Outcome);
        Assert.NotEqual(first.Token, next.Token);
        var before = await SnapshotAsync(tenant);
        Assert.False(await store.CompleteAsync(tenant, "expired-claim", first.Token, Receipt(), CancellationToken.None));
        Assert.False(await store.AbandonAsync(tenant, "expired-claim", first.Token, CancellationToken.None));
        Assert.Equal(before, await SnapshotAsync(tenant));
        Assert.True(await store.AbandonAsync(tenant, "expired-claim", next.Token, CancellationToken.None));
        var released = await store.TryClaimAsync(tenant, "expired-claim", "same", CancellationToken.None);
        Assert.Equal(IdempotencyClaim.Acquired, released.Outcome);
        Assert.NotEqual(next.Token, released.Token);
        Assert.True(await store.CompleteAsync(tenant, "expired-claim", released.Token, null, CancellationToken.None));
        Assert.Equal(IdempotencyClaim.Unreplayable, (await store.TryClaimAsync(tenant, "expired-claim", "same", CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Full_physical_capacity_preserves_existing_service_and_recycles_only_database_expired_slots()
    {
        var tenant = Tenant();
        await SeedSlotsAsync(tenant, 1000);
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        var store = Store(source);
        Assert.Equal(IdempotencyClaim.CapacityExhausted, (await store.TryClaimAsync(tenant, "new-at-capacity", "new", CancellationToken.None)).Outcome);
        Assert.Equal(IdempotencyClaim.InFlight, (await store.TryClaimAsync(tenant, "slot-0000", "seed", CancellationToken.None)).Outcome);
        Assert.Equal(IdempotencyClaim.Completed, (await store.TryClaimAsync(tenant, "slot-0001", "seed", CancellationToken.None)).Outcome);
        Assert.Equal(IdempotencyClaim.Unreplayable, (await store.TryClaimAsync(tenant, "slot-0002", "seed", CancellationToken.None)).Outcome);
        var oldToken = await ScalarAsync<Guid>(tenant, "SELECT claim_token FROM public.idempotency_keys WHERE key='slot-0001'");
        await ExpireAsync(tenant, "slot-0001");
        var acquired = await store.TryClaimAsync(tenant, "new-at-capacity", "new", CancellationToken.None);
        Assert.Equal(IdempotencyClaim.Acquired, acquired.Outcome);
        Assert.NotEqual(oldToken, acquired.Token);
        Assert.Equal(1000L, await ScalarAsync<long>(tenant, "SELECT count(*) FROM public.idempotency_keys"));
        Assert.Equal(0L, await ScalarAsync<long>(tenant, "SELECT count(*) FROM public.idempotency_keys WHERE key='slot-0001'"));
        Assert.Equal(1L, await ScalarAsync<long>(tenant, """
            SELECT count(*) FROM public.idempotency_keys WHERE key='new-at-capacity' AND fingerprint='new' AND state='in_flight'
                AND status_code IS NULL AND content_type IS NULL AND headers IS NULL AND body IS NULL
                AND replay_target_kind IS NULL AND replay_target_id IS NULL
            """));
        var before = await SnapshotAsync(tenant);
        Assert.False(await store.CompleteAsync(tenant, "slot-0001", oldToken, Receipt(), CancellationToken.None));
        Assert.False(await store.AbandonAsync(tenant, "slot-0001", oldToken, CancellationToken.None));
        Assert.False(await store.CompleteAsync(tenant, "new-at-capacity", oldToken, Receipt(), CancellationToken.None));
        Assert.Equal(before, await SnapshotAsync(tenant));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Independent_capacity_competitors_cannot_both_consume_the_last_physical_slot(bool recycle)
    {
        var tenant = Tenant();
        await SeedSlotsAsync(tenant, recycle ? 1000 : 999);
        if (recycle) await ExpireAsync(tenant, "slot-0000");
        await using var firstSource = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        await using var secondSource = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        var outcomes = await Task.WhenAll(
            Store(firstSource).TryClaimAsync(tenant, "last-slot-first", "first", CancellationToken.None),
            Store(secondSource).TryClaimAsync(tenant, "last-slot-second", "second", CancellationToken.None));
        Assert.Single(outcomes, outcome => outcome.Outcome == IdempotencyClaim.Acquired);
        Assert.Single(outcomes, outcome => outcome.Outcome == IdempotencyClaim.CapacityExhausted);
        Assert.Equal(1000L, await ScalarAsync<long>(tenant, "SELECT count(*) FROM public.idempotency_keys"));
    }

    [Fact]
    public async Task Populated_tenant_keyspaces_are_isolated_and_missing_scope_reads_nothing()
    {
        var first = Tenant();
        var second = Tenant();
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        var store = Store(source);
        Assert.Equal(IdempotencyClaim.Acquired, (await store.TryClaimAsync(first, "same-client-key", "first", CancellationToken.None)).Outcome);
        Assert.Equal(IdempotencyClaim.Acquired, (await store.TryClaimAsync(second, "same-client-key", "second", CancellationToken.None)).Outcome);
        Assert.Equal(1L, await ScalarAsync<long>(first, "SELECT count(*) FROM public.idempotency_keys"));
        Assert.Equal(1L, await ScalarAsync<long>(second, "SELECT count(*) FROM public.idempotency_keys"));
        Assert.Equal(0L, await ScalarAsync<long>(first, "SELECT count(*) FROM public.idempotency_keys WHERE tenant_id=@other", ("other", second.Value)));
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var query = new NpgsqlCommand("SELECT count(*) FROM public.idempotency_keys", app);
        Assert.Equal(0L, await query.ExecuteScalarAsync());
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(app, transaction, first.Value);
        await using var write = new NpgsqlCommand("UPDATE public.idempotency_keys SET fingerprint='forbidden' WHERE tenant_id=@other", app, transaction);
        write.Parameters.AddWithValue("other", second.Value);
        Assert.Equal(0, await write.ExecuteNonQueryAsync());
        await using var delete = new NpgsqlCommand("DELETE FROM public.idempotency_keys WHERE tenant_id=@tenant", app, transaction);
        delete.Parameters.AddWithValue("tenant", first.Value);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync())).SqlState);
    }

    [Theory]
    [InlineData("11111111-1111-7111-8111-111111111111")]
    [InlineData("22222222-2222-7222-8222-222222222222")]
    [InlineData("ffffffff-ffff-7fff-bfff-ffffffffffff")]
    public async Task Capacity_keys_match_independent_SQL_RFC_length_prefixed_vectors_and_are_distinct_from_business_keys(string value)
    {
        var tenant = TenantId.From(Guid.Parse(value));
        var actual = await ScalarAsync<long>(tenant, """
            SELECT ('x'||substr(encode(sha256(
                int4send(octet_length(convert_to('learnstack.idempotency.capacity.v1','UTF8')))
                ||convert_to('learnstack.idempotency.capacity.v1','UTF8')||int4send(16)||uuid_send(@tenant)),
                'hex'),1,16))::bit(64)::bigint
            """, ("tenant", tenant.Value));
        Assert.Equal(IdempotencyTenantCapacityKey.ForTenant(tenant), actual);
        Assert.NotEqual(IdentityAdvisoryLockKey.ForTenant(tenant), actual);
    }

    [Theory]
    [InlineData(IdempotencyReplayTargetKind.Membership)]
    [InlineData(IdempotencyReplayTargetKind.Role)]
    [InlineData(IdempotencyReplayTargetKind.Organization)]
    [InlineData(IdempotencyReplayTargetKind.Tenant)]
    public async Task Closed_protected_targets_round_trip_without_becoming_wire_fields(IdempotencyReplayTargetKind kind)
    {
        var tenant = Tenant();
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        var store = Store(source);
        var claim = await store.TryClaimAsync(tenant, "protected-kind", "fingerprint", CancellationToken.None);
        var response = Receipt(new(kind, Guid.CreateVersion7()));
        Assert.True(await store.CompleteAsync(tenant, "protected-kind", claim.Token, response, CancellationToken.None));
        var replay = await store.TryClaimAsync(tenant, "protected-kind", "fingerprint", CancellationToken.None);
        Assert.Equal(response.ReplayTarget, replay.Stored!.ReplayTarget);
        Assert.Equal(response.Body, replay.Stored.Body);
        using var json = JsonDocument.Parse(replay.Stored.Body);
        Assert.Single(json.RootElement.EnumerateObject());
    }

    private TenantId Tenant()
    {
        var tenant = TenantId.From(Guid.CreateVersion7());
        _tenants.Add(tenant);
        return tenant;
    }

    private static PostgresIdempotencyStore Store(NpgsqlDataSource source, Func<IdempotencyStoreStage, Task>? observer = null) =>
        new(new Lazy<NpgsqlDataSource>(() => source), observer);

    private static IdempotentResponse Receipt(IdempotencyReplayTarget? target = null) =>
        new(200, "application/json", new Dictionary<string, IReadOnlyList<string>> { ["ETag"] = ["\"7\""] },
            Encoding.UTF8.GetBytes("{\"version\":\"7\"}"), target);

    private Task ExpireAsync(TenantId tenant, string key) => MutateAsync(tenant,
        "UPDATE public.idempotency_keys SET expires_at=clock_timestamp()-interval '1 second' WHERE key=@key", ("key", key));

    private Task SeedSlotsAsync(TenantId tenant, int count) => MutateAsync(tenant, """
        INSERT INTO public.idempotency_keys(tenant_id,key,fingerprint,claim_token,state,status_code,content_type,headers,body,
            claimed_at,expires_at,replay_target_kind,replay_target_id)
        SELECT @tenant,'slot-'||lpad(i::text,4,'0'),'seed',uuidv7(),
            CASE i%3 WHEN 0 THEN 'in_flight' WHEN 1 THEN 'completed' ELSE 'unreplayable' END,
            CASE WHEN i%3=1 THEN 200 ELSE NULL END,
            CASE WHEN i%3=1 THEN 'application/json' ELSE NULL END,
            CASE WHEN i%3=1 THEN '{"ETag":["\"7\""]}'::jsonb ELSE NULL END,
            CASE WHEN i%3=1 THEN convert_to('{"version":"7"}','UTF8') ELSE NULL END,
            clock_timestamp(),clock_timestamp()+interval '24 hours',
            CASE WHEN i%3=1 THEN 'Organization' ELSE NULL END,
            CASE WHEN i%3=1 THEN @target ELSE NULL END
        FROM generate_series(0,@count-1) AS i
        """, ("tenant", tenant.Value), ("count", count), ("target", Guid.CreateVersion7()));

    private Task<string> SnapshotAsync(TenantId tenant) => ScalarAsync<string>(tenant,
        "SELECT COALESCE(jsonb_agg(to_jsonb(row) ORDER BY key),'[]'::jsonb)::text FROM public.idempotency_keys AS row");

    private async Task MutateAsync(TenantId tenant, string sql, params (string Name, object Value)[] values)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(app, transaction, tenant.Value);
        await using var command = new NpgsqlCommand(sql, app, transaction) { CommandTimeout = 10 };
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private async Task<T> ScalarAsync<T>(TenantId tenant, string sql, params (string Name, object Value)[] values)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(app, transaction, tenant.Value);
        await using var command = new NpgsqlCommand(sql, app, transaction) { CommandTimeout = 10 };
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
