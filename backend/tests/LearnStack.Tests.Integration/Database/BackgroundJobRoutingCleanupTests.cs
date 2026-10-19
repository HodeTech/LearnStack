using System.Diagnostics;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Actual confined routing completion and cleanup, independently observed through app-role reads.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class BackgroundJobRoutingCleanupTests(SchemaFixture schema)
{
    private static BackgroundJobRuntimeFixture Fixture => BackgroundJobRuntimeFixture.Shared;

    [Fact]
    public async Task An_owned_success_returns_the_committed_claim_after_actual_cleanup_without_spending_an_attempt()
    {
        await using var database = await DatabaseAsync();
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        await using var source = Source(database);
        var result = await Store(source).TryClaimOwnedAsync(job, 1);

        result.Failed.Should().BeFalse();
        result.CleanupConfirmed.Should().BeTrue();
        result.Claim.Should().NotBeNull();
        var claim = result.Claim!; // The preceding assertion establishes the committed claim.
        var row = await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job)
            ?? throw new InvalidOperationException("The seeded job is missing.");
        row.OwnershipToken.Should().Be(claim.Token);
        row.OwnershipKind.Should().Be("routing");
        row.OwnershipUntil.Should().Be(claim.OwnershipUntil);
        row.Phase.Should().Be("pending");
        row.NormalAttempts.Should().Be(0);
        row.CurrentAttempt.Should().BeNull();
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(0);
    }

    [Fact]
    public async Task An_unknown_definition_finishes_cleanly_without_claim_or_attempt()
    {
        await using var database = await DatabaseAsync();
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            "jobssubmissionfixture.unknown.v1");
        await using var source = Source(database);
        var result = await Store(source).TryClaimOwnedAsync(job, 1);

        result.Failed.Should().BeFalse();
        result.CleanupConfirmed.Should().BeTrue();
        result.Claim.Should().BeNull();
        var row = await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job)
            ?? throw new InvalidOperationException("The seeded job is missing.");
        row.OwnershipToken.Should().BeNull();
        row.NormalAttempts.Should().Be(0);
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(0);
    }

    [Fact]
    public async Task Actual_SQL_refusal_confirms_cleanup_preserves_the_public_exception_and_can_route_after_restoration()
    {
        await using var database = await DatabaseAsync();
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        await using var source = Source(database);
        var store = Store(source);
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await SchemaQueries.ExecuteAsync(owner, null,
            "REVOKE UPDATE (ownership_token) ON background_jobs FROM learnstack_outbox_admin");
        try
        {
            var result = await store.TryClaimOwnedAsync(job, 1);
            result.Failed.Should().BeTrue();
            result.CleanupConfirmed.Should().BeTrue("a rejected statement is distinct from failed resource cleanup");
            result.Claim.Should().BeNull();
            var original = FluentActions.Invoking(result.ThrowIfFailed).Should().Throw<PostgresException>().Which;
            original.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
            FluentActions.Invoking(result.ThrowIfFailed).Should().Throw<PostgresException>().Which.Should().BeSameAs(original);
            var publicRead = () => store.TryClaimAsync(job, 1);
            (await publicRead.Should().ThrowAsync<PostgresException>()).Which.SqlState
                .Should().Be(PostgresErrorCodes.InsufficientPrivilege);
            var row = await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job)
                ?? throw new InvalidOperationException("The seeded job is missing.");
            row.OwnershipToken.Should().BeNull();
            row.NormalAttempts.Should().Be(0);
        }
        finally
        {
            await SchemaQueries.ExecuteAsync(owner, null,
                "GRANT UPDATE (ownership_token) ON background_jobs TO learnstack_outbox_admin");
        }

        var restored = await store.TryClaimOwnedAsync(job, 1);
        restored.Failed.Should().BeFalse();
        restored.CleanupConfirmed.Should().BeTrue();
        restored.Claim.Should().NotBeNull("confirmed refusal cleanup does not permanently quarantine eligible routing");
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(0);
    }

    [Fact]
    public async Task COMMIT_failure_never_publishes_the_candidate_claim_or_spends_an_attempt()
    {
        await using var database = await DatabaseAsync();
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        await using var source = Source(database);
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        {
            // A real deferred PostgreSQL failure happens after UPDATE RETURNING
            // supplied its row. This is a commit-refusal control, not uncertainty proof.
            await SchemaQueries.ExecuteAsync(owner, null, """
                CREATE FUNCTION public.fn_routing_commit_refusal() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Routing commit refusal control';
                END $$;
                CREATE CONSTRAINT TRIGGER ct_routing_commit_refusal AFTER UPDATE ON background_jobs
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_routing_commit_refusal();
                """);
        }

        var result = await Store(source).TryClaimOwnedAsync(job, 1);
        result.Failed.Should().BeTrue();
        result.CleanupConfirmed.Should().BeTrue();
        result.Claim.Should().BeNull("UPDATE RETURNING is not acknowledged COMMIT");
        FluentActions.Invoking(result.ThrowIfFailed).Should().Throw<PostgresException>().Which.SqlState
            .Should().Be(PostgresErrorCodes.CheckViolation);
        var row = await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job)
            ?? throw new InvalidOperationException("The seeded job is missing.");
        row.OwnershipToken.Should().BeNull();
        row.NormalAttempts.Should().Be(0);
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(0);
    }

    [Fact]
    public async Task Cancellation_during_an_actual_routing_lock_wait_finishes_cleanup_and_closed_gate_refuses_fresh_SQL()
    {
        await using var database = await DatabaseAsync();
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var gate = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        await using var source = Source(database);
        var store = Store(source, gate);
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        await SchemaQueries.ExecuteAsync(owner, transaction, "LOCK TABLE background_jobs IN ACCESS EXCLUSIVE MODE");
        var blocker = ((NpgsqlConnection)owner).ProcessID;
        using var observationDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        (await HasRoutingLockAsync(observer, blocker, observationDeadline.Token)).Should().BeFalse();
        using var cancellation = new CancellationTokenSource();
        var running = store.TryClaimOwnedAsync(job, 1, cancellation.Token);
        try
        {
            await WaitForRoutingLockAsync(observer, blocker, observationDeadline.Token);
            running.IsCompleted.Should().BeFalse("the actual lock waiter must be observed before requesting caller cancellation");
            (await HasRoutingLockAsync(observer, observer.ProcessID, observationDeadline.Token)).Should().BeFalse(
                "an unrelated backend cannot satisfy the same blocker-identity barrier");
            gate.StopStarts();
            await cancellation.CancelAsync();
            var canceled = await running.WaitAsync(TimeSpan.FromSeconds(10));
            canceled.Failed.Should().BeTrue();
            canceled.CleanupConfirmed.Should().BeTrue();
            canceled.Claim.Should().BeNull();
            FluentActions.Invoking(canceled.ThrowIfFailed).Should().Throw<OperationCanceledException>();
            var publicRead = () => store.TryClaimAsync(job, 1, cancellation.Token);
            await publicRead.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            await cancellation.CancelAsync();
            await transaction.RollbackAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await using var unopened = Source(database);
        var refused = await Store(unopened, gate).TryClaimOwnedAsync(job, 1);
        refused.Failed.Should().BeFalse();
        refused.CleanupConfirmed.Should().BeTrue();
        refused.Claim.Should().BeNull();
        unopened.IsValueCreated.Should().BeFalse("normal host stop refuses actual fresh routing acquisition");
        var row = await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job)
            ?? throw new InvalidOperationException("The seeded job is missing.");
        row.OwnershipToken.Should().BeNull();
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(0);
        var restored = await Store(unopened, new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure))
            .TryClaimOwnedAsync(job, 1);
        restored.Claim.Should().NotBeNull("the same guarded source still routes while the process gate is open");
        restored.CleanupConfirmed.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_queued_routing_read_rechecks_the_process_gate_at_actual_acquisition(bool finalExit)
    {
        await using var database = await DatabaseAsync();
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var gate = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        await using var source = Source(database);
        var store = Store(source, gate);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = ReadAfterWaitAsync();
        if (finalExit) gate.BeginFinalExit();
        else gate.StopStarts();
        release.SetResult();
        var result = await queued.WaitAsync(TimeSpan.FromSeconds(10));

        result.Failed.Should().BeFalse();
        result.CleanupConfirmed.Should().BeTrue();
        result.Claim.Should().BeNull();
        source.IsValueCreated.Should().BeFalse("scheduling before gate closure supplies no acquisition permit");
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(0);

        async Task<PostgresBackgroundJobRoutingStore.OwnedRoutingRead> ReadAfterWaitAsync()
        {
            await release.Task;
            return await store.TryClaimOwnedAsync(job, 1);
        }
    }

    private async Task<DisposableSchemaDatabase> DatabaseAsync()
    {
        var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        return database;
    }

    private static PrivilegedDataSource Source(DisposableSchemaDatabase database) =>
        new(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));

    private static PostgresBackgroundJobRoutingStore Store(PrivilegedDataSource source,
        BackgroundJobRuntimeGate? gate = null) => new(source, Fixture.Registry, new(), new SystemGuidFactory(), gate);

    private static async Task WaitForRoutingLockAsync(NpgsqlConnection connection, int blocker, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (await HasRoutingLockAsync(connection, blocker, cancellationToken)) return;
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
        throw new TimeoutException("The real routing read did not reach its PostgreSQL lock wait.");
    }

    private static async Task<bool> HasRoutingLockAsync(NpgsqlConnection connection, int blocker, CancellationToken cancellationToken)
    {
        // Activity text is truncated before this routing query's UPDATE suffix.
        // Observe the exact real lock dependency instead of matching that text.
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname=current_database()
                AND usename='learnstack_outbox_admin' AND wait_event_type='Lock'
                AND @blocker=ANY(pg_blocking_pids(pid)))
            """, connection);
        command.Parameters.AddWithValue("blocker", blocker);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
