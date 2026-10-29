using System.Data;
using System.Data.Common;
using System.Reflection;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Physical COMMIT acknowledgement is distinct from owned transaction cleanup.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class UnitOfWorkCommitCleanupTests(SchemaFixture schema)
{
    private const string Subscription = "proof.commit-cleanup.v1";
    private static readonly CleanupContext Context = new();

    [Fact]
    public async Task Acknowledged_commit_precedes_a_disposal_fault_and_remains_durable()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(Context);
        var marker = await InsertMarkerAsync(unit);
        var cleanupFailure = new IOException("cleanup proof fault");
        var transaction = Interpose(unit, cleanupFailure: cleanupFailure);

        await frame.CompleteAsync();

        transaction.Commits.Should().Be(1);
        transaction.Disposals.Should().Be(0, "physical acknowledgement must reach the owner before cleanup");
        unit.HasActiveTransaction.Should().BeFalse();
        (await CountMarkerAsync(source, marker)).Should().Be(1);
        await FluentActions.Awaiting(() => frame.DisposeAsync().AsTask())
            .Should().ThrowAsync<IOException>().Where(failure => ReferenceEquals(failure, cleanupFailure));
        unit.IsRollbackOnly.Should().BeTrue("failed cleanup prevents reuse, not the preceding durable commit");
        (await CountMarkerAsync(source, marker)).Should().Be(1);
        await FluentActions.Awaiting(() => unit.BeginTransactionAsync())
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Hanging_cleanup_remains_owned_after_acknowledged_commit()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(Context);
        var marker = await InsertMarkerAsync(unit);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transaction = Interpose(unit, releaseCleanup: release.Task);

        await frame.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(5));
        transaction.Disposals.Should().Be(0);
        var cleanup = frame.DisposeAsync().AsTask();
        try
        {
            await transaction.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cleanup.IsCompleted.Should().BeFalse("the actual cleanup callback still owns its transaction");
            unit.HasActiveTransaction.Should().BeFalse();
            (await CountMarkerAsync(source, marker)).Should().Be(1);
        }
        finally
        {
            release.TrySetResult();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
        }
        transaction.Disposals.Should().Be(1);
    }

    [Fact]
    public async Task Reuse_cleans_the_previous_commit_and_a_stale_frame_cannot_clean_the_next_one()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        var first = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(Context);
        var marker = await InsertMarkerAsync(unit);
        var previous = Interpose(unit);
        await first.CompleteAsync();
        previous.Disposals.Should().Be(0);

        var second = await unit.BeginTransactionAsync();
        previous.Disposals.Should().Be(1, "reuse must clean the completed transaction before BEGIN");
        await unit.SetTenantContextAsync(Context);
        var current = Interpose(unit);
        await first.DisposeAsync();
        unit.HasActiveTransaction.Should().BeTrue();
        current.Disposals.Should().Be(0, "the first frame belongs to an older generation");
        await second.CompleteAsync();
        current.Disposals.Should().Be(0);
        await first.DisposeAsync();
        current.Disposals.Should().Be(0, "stale disposal cannot clean a later committed generation either");
        await second.DisposeAsync();
        current.Disposals.Should().Be(1);
        (await CountMarkerAsync(source, marker)).Should().Be(1);
    }

    [Fact]
    public async Task Unknown_commit_preserves_its_primary_exception_when_disposal_also_fails()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(Context);
        var marker = await InsertMarkerAsync(unit);
        var primary = new IOException("commit proof fault");
        var transaction = Interpose(unit, primary, new IOException("cleanup proof fault"));
        try
        {
            await FluentActions.Awaiting(() => frame.CompleteAsync()).Should()
                .ThrowAsync<IOException>().Where(failure => ReferenceEquals(failure, primary));
            transaction.Commits.Should().Be(1);
            transaction.Disposals.Should().Be(1);
            unit.HasActiveTransaction.Should().BeFalse();
            await frame.DisposeAsync();
        }
        finally
        {
            // The injected COMMIT failed before sending; connection cleanup owns
            // the real uncommitted transaction even when its wrapper disposal faults.
            await unit.DisposeAsync();
        }
        (await CountMarkerAsync(source, marker)).Should().Be(0);
    }

    [Fact]
    public async Task Idempotency_end_disposes_the_committed_transaction_and_connection_once()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(Context);
        var connection = unit.Connection;
        var marker = await InsertMarkerAsync(unit);
        var transaction = Interpose(unit);
        await frame.CompleteAsync();
        unit.HasActiveTransaction.Should().BeFalse();
        connection.State.Should().Be(ConnectionState.Open);
        (await unit.EndBusinessOwnerAsync()).Should().BeTrue();
        connection.State.Should().Be(ConnectionState.Closed);
        transaction.Disposals.Should().Be(1);
        (await unit.EndBusinessOwnerAsync()).Should().BeTrue();
        transaction.Disposals.Should().Be(1);
        await FluentActions.Awaiting(() => unit.BeginTransactionAsync()).Should().ThrowAsync<ObjectDisposedException>();
        (await CountMarkerAsync(source, marker)).Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Idempotency_end_joins_actual_frame_or_unit_cleanup_instead_of_a_repeated_noop(bool wholeUnit)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(Context);
        var marker = await InsertMarkerAsync(unit);
        var connection = unit.Connection;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transaction = Interpose(unit, releaseCleanup: release.Task);
        await frame.CompleteAsync();
        var cleanup = wholeUnit ? unit.DisposeAsync().AsTask() : frame.DisposeAsync().AsTask();
        Task<bool>? end = null;
        Task? afterEnd = null;
        var outcomeStarted = false;
        try
        {
            await transaction.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            end = unit.EndBusinessOwnerAsync().AsTask();
            afterEnd = ObserveOutcomeAsync(end);
            // A scheduling yield alone could precede End's first continuation.
            // Observe the held owner over a bounded interval instead.
            await Assert.ThrowsAsync<TimeoutException>(() => afterEnd.WaitAsync(TimeSpan.FromMilliseconds(150)));
            cleanup.IsCompleted.Should().BeFalse();
            end.IsCompleted.Should().BeFalse();
            outcomeStarted.Should().BeFalse();
            connection.State.Should().Be(ConnectionState.Open);
            await FluentActions.Awaiting(() => unit.BeginTransactionAsync()).Should().ThrowAsync<ObjectDisposedException>();
        }
        finally
        {
            release.TrySetResult();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
            if (end is not null) (await end.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
            if (afterEnd is not null) await afterEnd.WaitAsync(TimeSpan.FromSeconds(5));
        }
        transaction.Disposals.Should().Be(1);
        connection.State.Should().Be(ConnectionState.Closed);
        outcomeStarted.Should().BeTrue();
        (await CountMarkerAsync(source, marker)).Should().Be(1);

        async Task ObserveOutcomeAsync(Task<bool> ended)
        {
            if (await ended) outcomeStarted = true;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Idempotency_end_never_relabels_a_failed_transaction_cleanup_as_confirmed(bool commitFailed)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(Context);
        var marker = await InsertMarkerAsync(unit);
        var connection = unit.Connection;
        var primary = new IOException("fixed unsent commit failure");
        var failure = new IOException("fixed transaction cleanup failure");
        Interpose(unit, commitFailed ? primary : null, failure);
        if (commitFailed)
        {
            // Injection fails before COMMIT is sent; this does not establish a
            // network lost-ACK case. The original exception must remain primary.
            await FluentActions.Awaiting(() => frame.CompleteAsync()).Should()
                .ThrowAsync<IOException>().Where(actual => ReferenceEquals(actual, primary));
        }
        else
        {
            await frame.CompleteAsync();
            await FluentActions.Awaiting(() => frame.DisposeAsync().AsTask()).Should()
                .ThrowAsync<IOException>().Where(actual => ReferenceEquals(actual, failure));
        }
        unit.HasActiveTransaction.Should().BeFalse();
        (await unit.EndBusinessOwnerAsync()).Should().BeFalse();
        connection.State.Should().Be(ConnectionState.Closed);
        (await unit.EndBusinessOwnerAsync()).Should().BeFalse();
        await unit.DisposeAsync();
        (await CountMarkerAsync(source, marker)).Should().Be(commitFailed ? 0 : 1);
    }

    [Fact]
    public async Task Positive_idempotency_cleanup_after_a_commit_exception_does_not_prove_business_rollback()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(Context);
        var marker = await InsertMarkerAsync(unit);
        var primary = new IOException("fixed post-commit observation failure");
        var transaction = Interpose(unit, afterCommitFailure: primary);
        // The real PostgreSQL COMMIT succeeds before this injected observation
        // fault. This is deliberately not described as a dropped wire ACK.
        await FluentActions.Awaiting(() => frame.CompleteAsync()).Should()
            .ThrowAsync<IOException>().Where(actual => ReferenceEquals(actual, primary));
        unit.HasActiveTransaction.Should().BeFalse();
        transaction.Disposals.Should().Be(1);
        (await unit.EndBusinessOwnerAsync()).Should().BeTrue();
        (await CountMarkerAsync(source, marker)).Should().Be(1,
            "cleanup establishes ended ownership, never business rollback or exactly-once execution");
    }

    private static CleanupTransaction Interpose(NpgsqlUnitOfWork unit,
        Exception? commitFailure = null, Exception? cleanupFailure = null, Task? releaseCleanup = null,
        Exception? afterCommitFailure = null)
    {
        var field = typeof(NpgsqlUnitOfWork).GetField("_transaction", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var current = (DbTransaction)field.GetValue(unit)!;
        var wrapper = new CleanupTransaction(current, commitFailure, cleanupFailure, releaseCleanup, afterCommitFailure);
        // Test-only failure injection: all SQL/COMMIT still uses the actual app-role
        // connection; no production transaction factory or alternate owner is added.
        field.SetValue(unit, wrapper);
        return wrapper;
    }

    private static async Task<Guid> InsertMarkerAsync(NpgsqlUnitOfWork unit)
    {
        var eventId = Guid.CreateVersion7();
        await SchemaQueries.AssertApplicationRoleAsync(unit.Connection);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO inbox_messages (tenant_id, subscription_id, event_id)
            VALUES (@tenant, @subscription, @event)
            """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!);
        insert.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
        insert.Parameters.AddWithValue("subscription", Subscription);
        insert.Parameters.AddWithValue("event", eventId);
        (await insert.ExecuteNonQueryAsync()).Should().Be(1);
        return eventId;
    }

    private static async Task<long> CountMarkerAsync(NpgsqlDataSource source, Guid eventId)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(connection);
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
        await using var query = new NpgsqlCommand("""
            SELECT count(*) FROM inbox_messages
            WHERE tenant_id = @tenant AND subscription_id = @subscription AND event_id = @event
            """, connection, transaction);
        query.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
        query.Parameters.AddWithValue("subscription", Subscription);
        query.Parameters.AddWithValue("event", eventId);
        return (long)(await query.ExecuteScalarAsync())!;
    }

    private sealed class CleanupContext : ITenantContext
    {
        public bool IsResolved => true;
        public TenantId TenantId => TenantId.From(SchemaFixture.TenantA);
        public OrganizationId? OrganizationId => null;
        public UserId? UserId => null;
        public string? CorrelationId => null;
        public string? ModuleName => "cleanup-proof";
    }

    private sealed class CleanupTransaction(DbTransaction inner, Exception? commitFailure,
        Exception? cleanupFailure, Task? releaseCleanup, Exception? afterCommitFailure) : DbTransaction
    {
        public int Commits { get; private set; }
        public int Disposals { get; private set; }
        public TaskCompletionSource CleanupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override IsolationLevel IsolationLevel => inner.IsolationLevel;
        protected override DbConnection? DbConnection => inner.Connection;
        public override void Commit() => inner.Commit();
        public override void Rollback() => inner.Rollback();
        public override Task RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);

        public override async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            Commits++;
            if (commitFailure is not null) throw commitFailure;
            await inner.CommitAsync(cancellationToken);
            if (afterCommitFailure is not null) throw afterCommitFailure;
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                Disposals++;
                CleanupEntered.TrySetResult();
                if (releaseCleanup is not null) await releaseCleanup;
                if (cleanupFailure is not null) throw cleanupFailure;
                await inner.DisposeAsync();
            }
            finally
            {
                await base.DisposeAsync();
            }
        }
    }
}
