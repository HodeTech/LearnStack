using FluentAssertions;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>The transport-owned inbox claim on an announced application transaction.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class InboxGuardTests(SchemaFixture schema)
{
    private const string First = "proof.first.v1";
    private const string Second = "proof.second.v1";

    [Fact]
    public async Task A_committed_claim_is_false_on_duplicate_but_distinct_tuples_are_independent()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        var eventId = Guid.CreateVersion7();
        await using (var unit = Unit(source))
        {
            var context = Context(SchemaFixture.TenantA);
            await using var frame = await AnnounceAsync(unit, context);
            var guard = new PostgresInboxGuard(unit, context);
            (await guard.TryClaimAsync(TenantId.From(SchemaFixture.TenantA), First, eventId)).Should().BeTrue();
            (await guard.TryClaimAsync(TenantId.From(SchemaFixture.TenantA), First, eventId)).Should().BeFalse();
            (await guard.TryClaimAsync(TenantId.From(SchemaFixture.TenantA), Second, eventId)).Should().BeTrue();
            (await guard.TryClaimAsync(TenantId.From(SchemaFixture.TenantA), First, Guid.CreateVersion7()))
                .Should().BeTrue();
            await frame.CompleteAsync();
        }

        await using (var unit = Unit(source))
        {
            var context = Context(SchemaFixture.TenantA);
            await using var frame = await AnnounceAsync(unit, context);
            (await new PostgresInboxGuard(unit, context)
                .TryClaimAsync(TenantId.From(SchemaFixture.TenantA), First, eventId)).Should().BeFalse();
            await frame.CompleteAsync();
        }
        await using (var unit = Unit(source))
        {
            var context = Context(SchemaFixture.TenantB);
            await using var frame = await AnnounceAsync(unit, context);
            (await new PostgresInboxGuard(unit, context)
                .TryClaimAsync(TenantId.From(SchemaFixture.TenantB), First, eventId)).Should().BeTrue();
            await frame.CompleteAsync();
        }
        (await CountAsync(database.AppConnectionString, SchemaFixture.TenantA, eventId)).Should().Be(2);
        (await CountAsync(database.AppConnectionString, SchemaFixture.TenantB, eventId)).Should().Be(1);
    }

    [Fact]
    public async Task Guard_refuses_missing_wrong_or_nonwritable_announcement_before_inserting()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        var eventId = Guid.CreateVersion7();
        var tenantA = TenantId.From(SchemaFixture.TenantA);

        await using (var unit = Unit(source))
        {
            var context = Context(SchemaFixture.TenantA);
            var guard = new PostgresInboxGuard(unit, context);
            await FluentActions.Awaiting(() => guard.TryClaimAsync(tenantA, First, eventId))
                .Should().ThrowAsync<InvalidOperationException>();
            await using var frame = await unit.BeginTransactionAsync();
            await FluentActions.Awaiting(() => guard.TryClaimAsync(tenantA, First, eventId))
                .Should().ThrowAsync<InvalidOperationException>();
            await frame.FailAsync();
        }

        await using (var unit = Unit(source))
        {
            var context = Context(SchemaFixture.TenantA);
            await using var frame = await AnnounceAsync(unit, context);
            await FluentActions.Awaiting(() => new PostgresInboxGuard(unit, context)
                    .TryClaimAsync(TenantId.From(SchemaFixture.TenantB), First, eventId))
                .Should().ThrowAsync<InvalidOperationException>();
            await frame.FailAsync();
        }

        await using (var unit = Unit(source))
        {
            var announced = Context(SchemaFixture.TenantA, SchemaFixture.OrgA1);
            await using var frame = await AnnounceAsync(unit, announced);
            var wrongOrganization = Context(SchemaFixture.TenantA, SchemaFixture.OrgA2);
            await FluentActions.Awaiting(() => new PostgresInboxGuard(unit, wrongOrganization)
                    .TryClaimAsync(tenantA, First, eventId))
                .Should().ThrowAsync<InvalidOperationException>();
            await frame.FailAsync();
        }

        await using (var unit = Unit(source))
        {
            var context = Context(SchemaFixture.TenantA);
            await using var frame = await AnnounceAsync(unit, context, TransactionMode.ReadOnly);
            await FluentActions.Awaiting(() => new PostgresInboxGuard(unit, context)
                    .TryClaimAsync(tenantA, First, eventId))
                .Should().ThrowAsync<InvalidOperationException>();
            await frame.FailAsync();
        }

        await using (var unit = Unit(source))
        {
            var context = Context(SchemaFixture.TenantA);
            await using var frame = await AnnounceAsync(unit, context);
            unit.MarkRollbackOnly();
            await FluentActions.Awaiting(() => new PostgresInboxGuard(unit, context)
                    .TryClaimAsync(tenantA, First, eventId))
                .Should().ThrowAsync<InvalidOperationException>();
            await frame.FailAsync();
        }

        (await CountAsync(database.AppConnectionString, SchemaFixture.TenantA, eventId)).Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("proof.first.v0")]
    [InlineData("proof.first.v01")]
    [InlineData("Proof.first.v1")]
    [InlineData("proof.bad/name.v1")]
    [InlineData("proof.first.v1 ")]
    public async Task Invalid_subscription_identity_refuses_before_SQL(string subscription)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var unit = Unit(source);
        var context = Context(SchemaFixture.TenantA);
        await using var frame = await AnnounceAsync(unit, context);
        var eventId = Guid.CreateVersion7();

        await FluentActions.Awaiting(() => new PostgresInboxGuard(unit, context)
                .TryClaimAsync(TenantId.From(SchemaFixture.TenantA), subscription, eventId))
            .Should().ThrowAsync<ArgumentException>();

        unit.IsRollbackOnly.Should().BeFalse("input rejection occurs before any SQL statement");
        (await CountInUnitAsync(unit, eventId)).Should().Be(0);
        await frame.CompleteAsync();
    }

    [Fact]
    public async Task Empty_event_identity_refuses_before_SQL_and_recorded_at_is_database_owned()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var unit = Unit(source);
        var context = Context(SchemaFixture.TenantA);
        await using var frame = await AnnounceAsync(unit, context);
        var guard = new PostgresInboxGuard(unit, context);
        await FluentActions.Awaiting(() => guard.TryClaimAsync(TenantId.From(SchemaFixture.TenantA), First,
            Guid.Empty)).Should().ThrowAsync<ArgumentException>();
        var eventId = Guid.CreateVersion7();
        (await guard.TryClaimAsync(TenantId.From(SchemaFixture.TenantA), First, eventId)).Should().BeTrue();
        await using var read = new NpgsqlCommand("""
            SELECT recorded_at <= clock_timestamp() AND recorded_at > clock_timestamp() - interval '1 minute'
            FROM inbox_messages WHERE tenant_id = @tenant AND subscription_id = @subscription AND event_id = @event
            """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!);
        read.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
        read.Parameters.AddWithValue("subscription", First);
        read.Parameters.AddWithValue("event", eventId);
        (await read.ExecuteScalarAsync()).Should().Be(true);
        await frame.CompleteAsync();

        await using var connection = await OpenAsync(database.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
        await using var overrideTimestamp = new NpgsqlCommand("""
            INSERT INTO inbox_messages (tenant_id, subscription_id, event_id, recorded_at)
            VALUES (@tenant, @subscription, @event, @timestamp)
            """, connection, transaction);
        overrideTimestamp.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
        overrideTimestamp.Parameters.AddWithValue("subscription", First);
        overrideTimestamp.Parameters.AddWithValue("event", Guid.CreateVersion7());
        overrideTimestamp.Parameters.AddWithValue("timestamp", DateTimeOffset.UtcNow);
        var denied = await FluentActions.Awaiting(() => overrideTimestamp.ExecuteNonQueryAsync())
            .Should().ThrowAsync<PostgresException>();
        denied.Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Default_empty_and_platform_tenants_refuse_before_SQL_and_completed_frame_cannot_claim()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var unit = Unit(source);
        var context = Context(SchemaFixture.TenantA);
        await using var frame = await AnnounceAsync(unit, context);
        var guard = new PostgresInboxGuard(unit, context);
        var eventId = Guid.CreateVersion7();
        // An array element supplies the uninitialized Vogen value without using
        // its prohibited default constructor in application code.
        var uninitialized = (new TenantId[1])[0];
        foreach (var invalid in new[] { uninitialized, TenantId.From(Guid.Empty),
                     TenantId.PlatformSentinel })
        {
            await FluentActions.Awaiting(() => guard.TryClaimAsync(invalid, First, eventId))
                .Should().ThrowAsync<ArgumentException>();
            unit.IsRollbackOnly.Should().BeFalse();
        }
        (await CountInUnitAsync(unit, eventId)).Should().Be(0);
        await frame.CompleteAsync();

        await FluentActions.Awaiting(() => guard.TryClaimAsync(
                TenantId.From(SchemaFixture.TenantA), First, eventId))
            .Should().ThrowAsync<InvalidOperationException>();
        (await CountAsync(database.AppConnectionString, SchemaFixture.TenantA, eventId)).Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Concurrent_claim_waits_on_uniqueness_and_observes_first_transaction_outcome(bool firstCommits)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var first = Unit(source);
        await using var second = Unit(source);
        var context = Context(SchemaFixture.TenantA);
        var eventId = Guid.CreateVersion7();
        await using var firstFrame = await AnnounceAsync(first, context);
        await using var secondFrame = await AnnounceAsync(second, context);
        var tenant = TenantId.From(SchemaFixture.TenantA);
        (await new PostgresInboxGuard(first, context).TryClaimAsync(tenant, First, eventId)).Should().BeTrue();
        var secondPid = await BackendPidAsync(second);

        var pending = new PostgresInboxGuard(second, context).TryClaimAsync(tenant, First, eventId);
        await BlockedByAsync(database.AppConnectionString, secondPid, first);
        pending.IsCompleted.Should().BeFalse("PostgreSQL must wait for the first transaction's unique key");
        if (firstCommits) await firstFrame.CompleteAsync();
        else await firstFrame.FailAsync();

        (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(!firstCommits);
        await secondFrame.CompleteAsync();
        (await CountAsync(database.AppConnectionString, SchemaFixture.TenantA, eventId)).Should().Be(1);
    }

    private static NpgsqlUnitOfWork Unit(NpgsqlDataSource source) =>
        new(source, NullLogger<NpgsqlUnitOfWork>.Instance);

    private static ClaimContext Context(Guid tenant, Guid? organization = null) =>
        new ClaimContext(TenantId.From(tenant), organization is { } id ? OrganizationId.From(id) : null);

    private static async Task<IUnitOfWorkScope> AnnounceAsync(NpgsqlUnitOfWork unit, ITenantContext context,
        TransactionMode mode = TransactionMode.ReadWrite)
    {
        var frame = await unit.BeginTransactionAsync(mode);
        await unit.SetTenantContextAsync(context);
        return frame;
    }

    private static async Task<int> BackendPidAsync(NpgsqlUnitOfWork unit)
    {
        await using var command = new NpgsqlCommand("SELECT pg_backend_pid()",
            (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task BlockedByAsync(string connectionString, int secondPid, NpgsqlUnitOfWork first)
    {
        var firstPid = await BackendPidAsync(first);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var connection = await OpenAsync(connectionString, deadline.Token);
        await SchemaQueries.AssertApplicationRoleAsync(connection);
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            await using var command = new NpgsqlCommand("""
                SELECT @first = ANY(pg_blocking_pids(@second))
                FROM pg_stat_activity WHERE pid = @second AND wait_event_type = 'Lock'
                """, connection);
            command.Parameters.AddWithValue("first", firstPid);
            command.Parameters.AddWithValue("second", secondPid);
            if (await command.ExecuteScalarAsync(deadline.Token) is true) return;
            // The observed blocker, rather than elapsed time, releases this proof.
            await Task.Yield();
        }
    }

    private static async Task<long> CountAsync(string connectionString, Guid tenantId, Guid eventId)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(connection);
        await SchemaQueries.SetTenantAsync(connection, transaction, tenantId);
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM inbox_messages WHERE tenant_id = @tenant AND event_id = @event
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("event", eventId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountInUnitAsync(NpgsqlUnitOfWork unit, Guid eventId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM inbox_messages WHERE event_id = @event",
            (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!);
        command.Parameters.AddWithValue("event", eventId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class ClaimContext(TenantId tenant, OrganizationId? organization) : ITenantContext
    {
        public bool IsResolved => true;
        public TenantId TenantId => tenant;
        public OrganizationId? OrganizationId => organization;
        public UserId? UserId => LearnStack.SharedKernel.Identifiers.UserId.SystemActor;
        public string? CorrelationId => null;
        public string? ModuleName => null;
    }

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString,
        CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
