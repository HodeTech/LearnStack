using LearnStack.Infrastructure.Idempotency;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Identity;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class PostgresIdempotencyStoreTests
{
    [Fact]
    public async Task Claims_progress_while_the_business_tenant_fence_is_held_and_return_after_capacity_release()
    {
        var tenant = Tenant();
        await using var business = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(business);
        await using var businessTransaction = await business.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(business, businessTransaction, tenant.Value);
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", business, businessTransaction))
        {
            command.Parameters.AddWithValue("key", IdentityAdvisoryLockKey.ForTenant(tenant));
            await command.ExecuteNonQueryAsync();
        }
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        var claimed = await Store(source).TryClaimAsync(tenant, "separate-owner", "fingerprint", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(IdempotencyClaim.Acquired, claimed.Outcome);
        await using var capacity = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@key)", business, businessTransaction);
        capacity.Parameters.AddWithValue("key", IdempotencyTenantCapacityKey.ForTenant(tenant));
        Assert.Equal(true, await capacity.ExecuteScalarAsync());
    }

    [Fact]
    public async Task A_fresh_read_after_the_capacity_wait_observes_the_other_owners_committed_claim()
    {
        var tenant = Tenant();
        var gate = new Boundary(IdempotencyStoreStage.CapacityHeld);
        await using var firstSource = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        var application = "idempotency-wait-" + Guid.NewGuid().ToString("N");
        await using var secondSource = ApplicationDataSource.Build(new NpgsqlConnectionStringBuilder(schema.Postgres.AppConnectionString)
        { ApplicationName = application }.ConnectionString);
        var first = Store(firstSource, gate.ObserveAsync).TryClaimAsync(tenant, "shared-contender", "same", CancellationToken.None);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = Store(secondSource).TryClaimAsync(tenant, "shared-contender", "same", CancellationToken.None);
        try
        {
            await ObserveAdvisoryWaitAsync(application, second);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
        }
        finally { gate.Release.TrySetResult(); }
        Assert.Equal(IdempotencyClaim.Acquired, (await first).Outcome);
        Assert.Equal(IdempotencyClaim.InFlight, (await second).Outcome);
        Assert.Equal(1L, await ScalarAsync<long>(tenant, "SELECT count(*) FROM public.idempotency_keys"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Commit_and_actual_connection_cleanup_finish_before_an_acquired_result_can_escape(bool holdConnectionCleanup)
    {
        var tenant = Tenant();
        var boundary = new Boundary(holdConnectionCleanup ? IdempotencyStoreStage.BeforeConnectionDispose : IdempotencyStoreStage.BeforeCommit);
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        var claim = Store(source, boundary.ObserveAsync).TryClaimAsync(tenant, "owned-completion", "fingerprint", CancellationToken.None);
        try
        {
            await boundary.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(claim.IsCompleted);
            Assert.Equal(holdConnectionCleanup ? 1L : 0L,
                await ScalarAsync<long>(tenant, "SELECT count(*) FROM public.idempotency_keys"));
        }
        finally { boundary.Release.TrySetResult(); }
        Assert.Equal(IdempotencyClaim.Acquired, (await claim).Outcome);
        Assert.Equal(1, boundary.ConnectionDisposals);
    }

    [Theory]
    [InlineData("AfterCommit")]
    [InlineData("AfterTransactionDispose")]
    [InlineData("AfterConnectionDispose")]
    public async Task Postcommit_or_cleanup_observation_failure_withholds_acquisition_without_retry_or_asserted_rollback(string fault)
    {
        var faultAt = Enum.Parse<IdempotencyStoreStage>(fault);
        var tenant = Tenant();
        var visits = new List<IdempotencyStoreStage>();
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        var store = Store(source, stage =>
        {
            visits.Add(stage);
            if (stage == faultAt) throw new NpgsqlException("private-key private-fingerprint private-outcome");
            return Task.CompletedTask;
        });
        var failure = await Assert.ThrowsAsync<IdempotencyStoreException>(() =>
            store.TryClaimAsync(tenant, "uncertain-commit", "fingerprint", CancellationToken.None));
        Assert.Null(failure.InnerException);
        Assert.Equal("Idempotency storage is unavailable.", failure.Message);
        Assert.DoesNotContain("private-", failure.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, visits.Count(stage => stage == IdempotencyStoreStage.CapacityHeld));
        Assert.Equal(1, visits.Count(stage => stage == IdempotencyStoreStage.AfterConnectionDispose));
        Assert.Equal(1L, await ScalarAsync<long>(tenant, "SELECT count(*) FROM public.idempotency_keys"));
        Assert.Equal(IdempotencyClaim.InFlight,
            (await Store(source).TryClaimAsync(tenant, "uncertain-commit", "fingerprint", CancellationToken.None)).Outcome);
        // This injects failure after the real COMMIT/Dispose calls. It proves the
        // caller cannot infer rollback from boundary failure, not network loss.
    }

    [Fact]
    public async Task Completion_and_release_use_no_capacity_fence_and_completed_retention_cannot_be_extended()
    {
        var tenant = Tenant();
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        var store = Store(source);
        var complete = await store.TryClaimAsync(tenant, "complete-no-capacity", "fingerprint", CancellationToken.None);
        var release = await store.TryClaimAsync(tenant, "release-no-capacity", "fingerprint", CancellationToken.None);
        await using var holder = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        await using var transaction = await holder.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", holder, transaction))
        {
            command.Parameters.AddWithValue("key", IdempotencyTenantCapacityKey.ForTenant(tenant));
            await command.ExecuteNonQueryAsync();
        }
        Assert.True(await store.CompleteAsync(tenant, "complete-no-capacity", complete.Token, Receipt(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await store.AbandonAsync(tenant, "release-no-capacity", release.Token, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5)));
        var before = await SnapshotAsync(tenant);
        Assert.False(await store.CompleteAsync(tenant, "complete-no-capacity", complete.Token, Receipt(), CancellationToken.None));
        Assert.Equal(before, await SnapshotAsync(tenant));
        Assert.InRange(await ScalarAsync<double>(tenant, """
            SELECT extract(epoch FROM (expires_at-clock_timestamp()))::double precision
            FROM public.idempotency_keys WHERE key='complete-no-capacity'
            """), 86_390, 86_400);
    }

    [Fact]
    public async Task A_failed_completion_observation_does_not_abandon_or_overwrite_the_actual_committed_outcome()
    {
        var tenant = Tenant();
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        var normal = Store(source);
        var claim = await normal.TryClaimAsync(tenant, "completion-uncertainty", "fingerprint", CancellationToken.None);
        Assert.Equal(300d, await ScalarAsync<double>(tenant, """
            SELECT extract(epoch FROM (expires_at-claimed_at))::double precision
            FROM public.idempotency_keys WHERE key='completion-uncertainty'
            """));
        var faulty = Store(source, stage => stage == IdempotencyStoreStage.AfterCommit
            ? Task.FromException(new NpgsqlException("private-completed-payload")) : Task.CompletedTask);
        var response = Receipt(new(IdempotencyReplayTargetKind.Organization, Guid.CreateVersion7()));
        await Assert.ThrowsAsync<IdempotencyStoreException>(() =>
            faulty.CompleteAsync(tenant, "completion-uncertainty", claim.Token, response, CancellationToken.None));
        var before = await SnapshotAsync(tenant);
        Assert.False(await normal.AbandonAsync(tenant, "completion-uncertainty", claim.Token, CancellationToken.None));
        var replay = await normal.TryClaimAsync(tenant, "completion-uncertainty", "fingerprint", CancellationToken.None);
        Assert.Equal(IdempotencyClaim.Completed, replay.Outcome);
        Assert.Equal(response.ReplayTarget, replay.Stored!.ReplayTarget);
        Assert.Equal(response.Body, replay.Stored.Body);
        Assert.Equal(before, await SnapshotAsync(tenant));
    }

    [Fact]
    public async Task Capacity_lock_timeout_is_fixed_and_existing_keys_do_not_wait_for_that_fence()
    {
        var tenant = Tenant();
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        var store = Store(source);
        await store.TryClaimAsync(tenant, "existing-before-lock", "fingerprint", CancellationToken.None);
        await using var holder = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        await using var transaction = await holder.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", holder, transaction))
        {
            command.Parameters.AddWithValue("key", IdempotencyTenantCapacityKey.ForTenant(tenant));
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(IdempotencyClaim.InFlight, (await store.TryClaimAsync(tenant, "existing-before-lock", "fingerprint", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1))).Outcome);
        var failure = await Assert.ThrowsAsync<IdempotencyStoreException>(() =>
            store.TryClaimAsync(tenant, "blocked-new-key", "fingerprint", CancellationToken.None));
        Assert.Null(failure.InnerException);
        Assert.Equal(1L, await ScalarAsync<long>(tenant, "SELECT count(*) FROM public.idempotency_keys"));
    }

    [Fact]
    public async Task Caller_cancellation_during_capacity_wait_still_awaits_actual_cleanup()
    {
        var tenant = Tenant();
        var application = "idempotency-cancel-" + Guid.NewGuid().ToString("N");
        var cleanup = new Boundary(IdempotencyStoreStage.BeforeConnectionDispose);
        await using var source = ApplicationDataSource.Build(new NpgsqlConnectionStringBuilder(schema.Postgres.AppConnectionString)
        { ApplicationName = application }.ConnectionString);
        await using var holder = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        await using var transaction = await holder.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", holder, transaction))
        {
            command.Parameters.AddWithValue("key", IdempotencyTenantCapacityKey.ForTenant(tenant));
            await command.ExecuteNonQueryAsync();
        }
        using var cancellation = new CancellationTokenSource();
        var pending = Store(source, cleanup.ObserveAsync).TryClaimAsync(tenant, "cancelled-capacity", "fingerprint", cancellation.Token);
        try
        {
            await ObserveAdvisoryWaitAsync(application, pending);
            cancellation.Cancel();
            await cleanup.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
        }
        finally { cleanup.Release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, cleanup.ConnectionDisposals);
        Assert.Equal(0L, await ScalarAsync<long>(tenant, "SELECT count(*) FROM public.idempotency_keys"));
    }

    private async Task ObserveAdvisoryWaitAsync(string application, Task pending)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(observer);
        while (true)
        {
            Assert.False(pending.IsCompleted);
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity
                    WHERE application_name=@application AND wait_event_type='Lock' AND wait_event='advisory')
                """, observer) { CommandTimeout = 5 };
            command.Parameters.AddWithValue("application", application);
            if (await command.ExecuteScalarAsync(deadline.Token) is true) return;
            await Task.Delay(10, deadline.Token);
        }
    }

    private sealed class Boundary(IdempotencyStoreStage hold)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ConnectionDisposals { get; private set; }
        public async Task ObserveAsync(IdempotencyStoreStage stage)
        {
            if (stage == IdempotencyStoreStage.AfterConnectionDispose) ConnectionDisposals++;
            if (stage != hold) return;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
