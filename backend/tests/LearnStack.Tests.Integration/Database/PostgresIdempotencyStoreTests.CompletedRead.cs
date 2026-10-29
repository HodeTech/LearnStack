using LearnStack.Infrastructure.Idempotency;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Idempotency;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class PostgresIdempotencyStoreTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("in_flight")]
    [InlineData("unreplayable")]
    [InlineData("mismatched")]
    [InlineData("expired")]
    [InlineData("other-tenant")]
    public async Task Completed_lookup_discloses_nothing_and_does_not_change_nonmatching_state(string state)
    {
        var tenant = Tenant();
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        var store = Store(source);
        if (state != "missing")
        {
            var claim = await store.TryClaimAsync(tenant, "completed-read", "fingerprint", CancellationToken.None);
            if (state != "in_flight")
                Assert.True(await store.CompleteAsync(tenant, "completed-read", claim.Token,
                    state == "unreplayable" ? null : Receipt(), CancellationToken.None));
            if (state == "expired") await ExpireAsync(tenant, "completed-read");
        }
        var before = await SnapshotAsync(tenant);
        Assert.Null(await store.ReadCompletedAsync(state == "other-tenant" ? Tenant() : tenant,
            "completed-read", state == "mismatched" ? "different" : "fingerprint", CancellationToken.None));
        Assert.Equal(before, await SnapshotAsync(tenant));
    }

    [Fact]
    public async Task Completed_lookup_ignores_capacity_and_row_locks_and_returns_a_detached_original_snapshot()
    {
        var tenant = Tenant();
        await SeedSlotsAsync(tenant, 1000);
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        var store = Store(source);
        var before = await SnapshotAsync(tenant);
        await using var holder = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(holder);
        await using var transaction = await holder.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(holder, transaction, tenant.Value);
        await using (var command = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(@capacity);
            SELECT key FROM public.idempotency_keys WHERE key='slot-0001' FOR UPDATE
            """, holder, transaction))
        {
            command.Parameters.AddWithValue("capacity", IdempotencyTenantCapacityKey.ForTenant(tenant));
            await command.ExecuteNonQueryAsync();
        }
        var response = await store.ReadCompletedAsync(tenant, "slot-0001", "seed", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(response);
        Assert.Equal(IdempotencyReplayTargetKind.Organization, response.ReplayTarget!.Kind);
        response.Body[0] = 0;
        var again = await store.ReadCompletedAsync(tenant, "slot-0001", "seed", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(again);
        Assert.NotEqual(response.Body[0], again.Body[0]);
        Assert.Equal(before, await SnapshotAsync(tenant));
    }

    [Fact]
    public async Task Completed_lookup_awaits_actual_owner_cleanup_and_does_not_relabel_a_postcommit_fault()
    {
        var tenant = Tenant();
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        var normal = Store(source);
        var claim = await normal.TryClaimAsync(tenant, "read-owner-cleanup", "fingerprint", CancellationToken.None);
        Assert.True(await normal.CompleteAsync(tenant, "read-owner-cleanup", claim.Token, Receipt(), CancellationToken.None));
        var before = await SnapshotAsync(tenant);
        var boundary = new Boundary(IdempotencyStoreStage.BeforeConnectionDispose);
        var read = Store(source, boundary.ObserveAsync).ReadCompletedAsync(tenant, "read-owner-cleanup", "fingerprint", CancellationToken.None);
        try
        {
            await boundary.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(read.IsCompleted);
        }
        finally { boundary.Release.TrySetResult(); }
        Assert.NotNull(await read);
        Assert.Equal(1, boundary.ConnectionDisposals);
        await Assert.ThrowsAsync<IdempotencyStoreException>(() => Store(source, stage =>
            stage == IdempotencyStoreStage.AfterCommit ? Task.FromException(new NpgsqlException("private read fault")) : Task.CompletedTask)
            .ReadCompletedAsync(tenant, "read-owner-cleanup", "fingerprint", CancellationToken.None));
        Assert.Equal(before, await SnapshotAsync(tenant));
    }
}
