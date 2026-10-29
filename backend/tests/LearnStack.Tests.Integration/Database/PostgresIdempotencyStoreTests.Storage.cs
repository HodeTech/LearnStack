using System.Text;
using LearnStack.Infrastructure.Idempotency;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Idempotency;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class PostgresIdempotencyStoreTests
{
    [Theory]
    [InlineData("unknown-kind")]
    [InlineData("empty-target")]
    [InlineData("non-success")]
    [InlineData("protected-too-large")]
    [InlineData("generic-too-large")]
    [InlineData("headers-too-large")]
    public async Task Invalid_capture_is_rejected_without_changing_the_existing_claim(string defect)
    {
        var tenant = Tenant();
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        var store = Store(source);
        var claim = await store.TryClaimAsync(tenant, "invalid-capture", "fingerprint", CancellationToken.None);
        var target = new IdempotencyReplayTarget(IdempotencyReplayTargetKind.Organization, Guid.CreateVersion7());
        var response = defect switch
        {
            "unknown-kind" => Receipt(target with { Kind = (IdempotencyReplayTargetKind)99 }),
            "empty-target" => Receipt(target with { Id = Guid.Empty }),
            "non-success" => Receipt(target) with { StatusCode = 403 },
            "protected-too-large" => Receipt(target) with { Body = new byte[1025] },
            "generic-too-large" => Receipt() with { Body = new byte[PostgresIdempotencyStore.MaxStoredResponseBytes + 1] },
            _ => Receipt(target) with
            {
                Headers = new Dictionary<string, IReadOnlyList<string>> { ["X-Value"] = [new string('é', 512)] },
            },
        };
        var before = await SnapshotAsync(tenant);
        var refusal = await Assert.ThrowsAsync<ArgumentException>(() =>
            store.CompleteAsync(tenant, "invalid-capture", claim.Token, response, CancellationToken.None));
        Assert.Equal("Idempotency response is invalid.", refusal.Message);
        Assert.Equal(before, await SnapshotAsync(tenant));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Headers_inclusive_caps_accept_the_exact_boundary(bool protect)
    {
        var tenant = Tenant();
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        var store = Store(source);
        var claim = await store.TryClaimAsync(tenant, "inclusive-capture", "fingerprint", CancellationToken.None);
        var cap = protect ? PostgresIdempotencyStore.MaxProtectedResponseBytes : PostgresIdempotencyStore.MaxStoredResponseBytes;
        var response = new IdempotentResponse(200, null,
            new Dictionary<string, IReadOnlyList<string>> { ["X"] = ["é", "a"] }, new byte[cap - 4],
            protect ? new(IdempotencyReplayTargetKind.Membership, Guid.CreateVersion7()) : null);
        Assert.True(await store.CompleteAsync(tenant, "inclusive-capture", claim.Token, response, CancellationToken.None));
        var replay = await store.TryClaimAsync(tenant, "inclusive-capture", "fingerprint", CancellationToken.None);
        Assert.Equal(IdempotencyClaim.Completed, replay.Outcome);
        Assert.Equal(cap - 4, replay.Stored!.Body.Length);
        Assert.Equal(BoundaryHeaderValues, replay.Stored.Headers["X"]);
    }

    [Theory]
    [InlineData("kind-only")]
    [InlineData("id-only")]
    [InlineData("unknown-kind")]
    [InlineData("empty-target")]
    [InlineData("non-success")]
    [InlineData("in-flight")]
    [InlineData("too-large")]
    public async Task Actual_app_role_constraint_rejects_unpaired_or_invalid_protected_metadata(string defect)
    {
        var tenant = Tenant();
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        var store = Store(source);
        await store.TryClaimAsync(tenant, "constraint-target", "fingerprint", CancellationToken.None);
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(app, transaction, tenant.Value);
        await using var command = new NpgsqlCommand("""
            UPDATE public.idempotency_keys SET state=@state,status_code=@status,body=@body,
                replay_target_kind=@kind,replay_target_id=@target WHERE key='constraint-target'
            """, app, transaction);
        command.Parameters.AddWithValue("state", defect == "in-flight" ? "in_flight" : "completed");
        command.Parameters.AddWithValue("status", NpgsqlDbType.Integer, defect == "in-flight" ? DBNull.Value : defect == "non-success" ? 403 : 200);
        command.Parameters.AddWithValue("body", NpgsqlDbType.Bytea, defect == "in-flight" ? DBNull.Value : new byte[defect == "too-large" ? 1025 : 1]);
        command.Parameters.AddWithValue("kind", NpgsqlDbType.Text,
            defect == "id-only" ? DBNull.Value : defect == "unknown-kind" ? "TenantSetting" : "Organization");
        command.Parameters.AddWithValue("target", NpgsqlDbType.Uuid,
            defect == "kind-only" ? DBNull.Value : defect == "empty-target" ? Guid.Empty : Guid.CreateVersion7());
        var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("23514", failure.SqlState);
        Assert.Equal("ck_idempotency_keys_replay_target", failure.ConstraintName);
    }

    [Fact]
    public async Task A_real_PostgreSQL_completion_fault_has_fixed_exception_text_and_preserves_the_claim()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var tenant = TenantId.From(Guid.CreateVersion7());
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        var store = Store(source);
        var claim = await store.TryClaimAsync(tenant, "private-key-sentinel", "private-fingerprint-sentinel", CancellationToken.None);
        await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        await using (var command = new NpgsqlCommand("""
            CREATE FUNCTION idempotency_fault_proof() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'private-key-sentinel private-fingerprint-sentinel private-body-sentinel' USING ERRCODE='P0399'; END;
            $$;
            CREATE TRIGGER tg_idempotency_fault_proof BEFORE UPDATE ON public.idempotency_keys
                FOR EACH ROW EXECUTE FUNCTION idempotency_fault_proof();
            """, owner))
            await command.ExecuteNonQueryAsync();
        var failure = await Assert.ThrowsAsync<IdempotencyStoreException>(() => store.CompleteAsync(tenant,
            "private-key-sentinel", claim.Token, Receipt() with { Body = Encoding.UTF8.GetBytes("private-body-sentinel") }, CancellationToken.None));
        Assert.Null(failure.InnerException);
        Assert.Equal("Idempotency storage is unavailable.", failure.Message);
        Assert.DoesNotContain("private-", failure.ToString(), StringComparison.Ordinal);
        var stillClaimed = await store.TryClaimAsync(tenant, "private-key-sentinel", "private-fingerprint-sentinel", CancellationToken.None);
        Assert.Equal(IdempotencyClaim.InFlight, stillClaimed.Outcome);
        Assert.Null(stillClaimed.Stored);
    }

    [Fact]
    public async Task Forward_metadata_migration_preserves_generic_outcomes_and_existing_owner_rls_and_privileges()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres, applyMigrations: false);
        await MigrationChains.ApplyTenancyAsync(database.MigrationConnectionString);
        await using var context = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(database.MigrationConnectionString,
                options => options.MigrationsHistoryTable(PlatformDbContextFactory.HistoryTable)).Options);
        await context.GetService<IMigrator>().MigrateAsync("20261017231821_index_background_job_control_quarantines");
        var tenant = Guid.CreateVersion7();
        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SchemaQueries.SetTenantAsync(app, transaction, tenant);
            await using var command = new NpgsqlCommand("""
                INSERT INTO public.idempotency_keys(tenant_id,key,fingerprint,claim_token,state,status_code,content_type,headers,body,expires_at)
                VALUES(@tenant,'legacy-outcome','fingerprint',uuidv7(),'completed',201,'application/json',
                    '{"Location":["/original/location"]}'::jsonb,convert_to('{"legacy":true}','UTF8'),clock_timestamp()+interval '24 hours')
                """, app, transaction);
            command.Parameters.AddWithValue("tenant", tenant);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        var before = await TableProtectionAsync(database);
        await context.Database.MigrateAsync();
        Assert.Equal(before, await TableProtectionAsync(database));
        Assert.False(context.Database.HasPendingModelChanges());
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        var replay = await Store(source).TryClaimAsync(TenantId.From(tenant), "legacy-outcome", "fingerprint", CancellationToken.None);
        Assert.Equal(IdempotencyClaim.Completed, replay.Outcome);
        Assert.Equal(201, replay.Stored!.StatusCode);
        Assert.Equal("{\"legacy\":true}", Encoding.UTF8.GetString(replay.Stored.Body));
        Assert.Equal(LegacyLocationValues, replay.Stored.Headers["Location"]);
        Assert.Null(replay.Stored.ReplayTarget);
        await MigrationChains.ApplyAllAsync(database.MigrationConnectionString);
        Assert.Equal(before, await TableProtectionAsync(database));
    }

    private static async Task<string> TableProtectionAsync(DisposableSchemaDatabase database)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT jsonb_build_object('owner',pg_get_userbyid(relowner),'enabled',relrowsecurity,'forced',relforcerowsecurity,
                'acl',relacl::text,'policy',(SELECT jsonb_agg(to_jsonb(p) ORDER BY policyname)
                    FROM pg_catalog.pg_policies AS p WHERE schemaname='public' AND tablename='idempotency_keys'))::text
            FROM pg_catalog.pg_class WHERE oid='public.idempotency_keys'::regclass
            """, owner);
        var value = (string)(await command.ExecuteScalarAsync())!;
        Assert.Contains("learnstack_migration", value, StringComparison.Ordinal);
        Assert.Contains("\"enabled\": true", value, StringComparison.Ordinal);
        Assert.Contains("\"forced\": true", value, StringComparison.Ordinal);
        return value;
    }
}
