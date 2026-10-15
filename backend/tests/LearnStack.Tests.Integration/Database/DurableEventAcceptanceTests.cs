using FluentAssertions;
using System.Text.Json;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Observability;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Durable publication through the real app-role unit of work and PostgreSQL.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class DurableEventAcceptanceTests(SchemaFixture schema)
{
    private const string Trace = "00-11111111111111111111111111111111-2222222222222222-01";
    private const string OtherTrace = "00-33333333333333333333333333333333-4444444444444444-01";
    private const string First = "proof.first.v1";
    private const string Second = "proof.second.v1";
    private const string EarlierIdentity = "LearnStack.Previous.ProofEvent, LearnStack.Previous.Contracts, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    private const string OtherEarlierIdentity = "LearnStack.Previous.OtherEvent, LearnStack.Previous.Contracts, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    private const string RetiredIdentity = "LearnStack.Retired.ProofEvent, LearnStack.Retired.Contracts";
    private static readonly DateTimeOffset Occurred = new(2026, 10, 13, 10, 20, 30, TimeSpan.Zero);

    [Fact]
    public async Task Integration_Event_Publication_Is_Durable_Acceptance()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var services = Compose(source);
        var fact = Event();

        await services.GetRequiredService<IEventBus>().PublishAsync(Envelope(fact));

        var rows = await RowsAsync(database.AppConnectionString, fact.EventId);
        rows.Select(row => row.Subscription).Should().BeEquivalentTo([First, Second]);
        rows.Should().OnlyContain(row => row.Tenant == SchemaFixture.TenantA
            && row.Organization == SchemaFixture.OrgA1 && row.EventId == fact.EventId
            && row.Attempts == 0 && row.ProcessedAt == null && row.LastError == null);
        ExplodingHandler.Constructions.Should().Be(0);
        ExplodingHandler.Invocations.Should().Be(0);
    }

    [Fact]
    public async Task Identical_republication_preserves_status_lease_and_row_version()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var services = Compose(source);
        var envelope = Envelope(Event());
        var bus = services.GetRequiredService<IEventBus>();
        await bus.PublishAsync(envelope);
        await using (var admin = await OpenAsync(database.OutboxConnectionString))
        await using (var update = new NpgsqlCommand("""
            UPDATE event_deliveries SET attempts = 2, last_error = 'publish:failed:closed',
                available_after = clock_timestamp() + interval '1 hour'
            WHERE event_id = @event
            """, admin))
        {
            update.Parameters.AddWithValue("event", envelope.Event.EventId);
            (await update.ExecuteNonQueryAsync()).Should().Be(2);
        }
        var before = await RowsAsync(database.AppConnectionString, envelope.Event.EventId);

        await bus.PublishAsync(envelope);

        (await RowsAsync(database.AppConnectionString, envelope.Event.EventId)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Canonical_republication_preserves_compatible_alias_copies_and_their_scheduling_state()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var services = Compose(source, withAliases: true);
        var envelope = Envelope(Event());
        var registry = services.GetRequiredService<OutboxEventRegistry>();
        registry.Resolve(EarlierIdentity).Disposition.Should().Be(OutboxEventDisposition.Active);
        registry.Resolve(EarlierIdentity).EventType.Should().Be<ProofEvent>();
        await InsertCopyAsync(database.AppConnectionString, envelope, First, storedType: EarlierIdentity);
        await InsertCopyAsync(database.AppConnectionString, envelope, Second, storedType: EarlierIdentity);
        await using (var admin = await OpenAsync(database.OutboxConnectionString))
        await using (var update = new NpgsqlCommand("""
            UPDATE event_deliveries SET attempts = 2, last_error = 'execute:failed:handler',
                available_after = clock_timestamp() + interval '1 hour',
                locked_by = @token, locked_until = clock_timestamp() + interval '1 hour'
            WHERE event_id = @event
            """, admin))
        {
            update.Parameters.AddWithValue("token", Guid.CreateVersion7().ToString("D"));
            update.Parameters.AddWithValue("event", envelope.Event.EventId);
            (await update.ExecuteNonQueryAsync()).Should().Be(2);
        }
        var before = await RowsAsync(database.AppConnectionString, envelope.Event.EventId);

        await services.GetRequiredService<IEventBus>().PublishAsync(envelope);

        (await RowsAsync(database.AppConnectionString, envelope.Event.EventId)).Should().BeEquivalentTo(before);
        ExplodingHandler.Constructions.Should().Be(0);
    }

    [Fact]
    public async Task A_compatible_alias_subset_is_completed_with_the_current_canonical_identity()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var services = Compose(source, withAliases: true);
        var envelope = Envelope(Event());
        await InsertCopyAsync(database.AppConnectionString, envelope, First, storedType: EarlierIdentity);
        var before = (await RowsAsync(database.AppConnectionString, envelope.Event.EventId)).Single();

        await services.GetRequiredService<IEventBus>().PublishAsync(envelope);

        var rows = await RowsAsync(database.AppConnectionString, envelope.Event.EventId);
        rows.Select(row => row.Subscription).Should().BeEquivalentTo([First, Second]);
        rows.Single(row => row.Subscription == First).Should().BeEquivalentTo(before);
        rows.Single(row => row.Subscription == Second).Type.Should().Be(typeof(ProofEvent).AssemblyQualifiedName);
        ExplodingHandler.Constructions.Should().Be(0);
    }

    [Theory]
    [InlineData("other-canonical")]
    [InlineData("other-alias")]
    [InlineData("unknown")]
    [InlineData("retired")]
    [InlineData("alias-payload")]
    public async Task Alias_compatibility_never_accepts_a_different_contract_or_changed_payload(string collision)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var services = Compose(source, withAliases: true);
        var envelope = Envelope(Event());
        var storedType = collision switch
        {
            "other-canonical" => typeof(OtherEvent).AssemblyQualifiedName!,
            "other-alias" => OtherEarlierIdentity,
            "unknown" => "LearnStack.Undeclared.ProofEvent, LearnStack.Undeclared.Contracts",
            "retired" => RetiredIdentity,
            "alias-payload" => EarlierIdentity,
            _ => throw new ArgumentOutOfRangeException(nameof(collision)),
        };
        var expectedType = collision switch
        {
            "other-canonical" or "other-alias" => typeof(OtherEvent),
            "alias-payload" => typeof(ProofEvent),
            _ => null,
        };
        services.GetRequiredService<OutboxEventRegistry>().Resolve(storedType).EventType.Should().Be(expectedType);
        var storedEnvelope = collision == "alias-payload"
            ? Envelope(((ProofEvent)envelope.Event) with { RelatedId = Guid.CreateVersion7() })
            : envelope;
        // The earlier missing subscription would be inserted before this later
        // collision; refusal must roll that insertion back with the whole set.
        await InsertCopyAsync(database.AppConnectionString, storedEnvelope, Second, storedType: storedType,
            storedPartition: envelope.PartitionKey);
        var before = await RowsAsync(database.AppConnectionString, envelope.Event.EventId);

        await FluentActions.Awaiting(() => services.GetRequiredService<IEventBus>().PublishAsync(envelope))
            .Should().ThrowAsync<InvalidOperationException>();

        (await RowsAsync(database.AppConnectionString, envelope.Event.EventId)).Should().BeEquivalentTo(before);
        ExplodingHandler.Constructions.Should().Be(0);
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("occurred-microsecond")]
    [InlineData("organization")]
    [InlineData("causation")]
    [InlineData("actor")]
    [InlineData("correlation")]
    public async Task Any_immutable_envelope_collision_refuses_without_overwriting(string change)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var services = Compose(source);
        var fact = Event();
        var original = Envelope(fact);
        var bus = services.GetRequiredService<IEventBus>();
        await bus.PublishAsync(original);
        var before = await RowsAsync(database.AppConnectionString, fact.EventId);
        var collision = change switch
        {
            "payload" => Envelope(fact with { SubjectId = Guid.CreateVersion7() }),
            "occurred-microsecond" => Envelope(fact with { OccurredAt = fact.OccurredAt.AddTicks(10) }),
            "organization" => Envelope(fact, organization: SchemaFixture.OrgA2),
            "causation" => Envelope(fact, causation: Guid.CreateVersion7()),
            "actor" => Envelope(fact, actor: UserId.From(Guid.CreateVersion7())),
            "correlation" => Envelope(fact, trace: OtherTrace),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };

        await FluentActions.Awaiting(() => bus.PublishAsync(collision)).Should().ThrowAsync<InvalidOperationException>();

        (await RowsAsync(database.AppConnectionString, fact.EventId)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task A_matching_existing_subset_is_completed_without_changing_its_copy()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var services = Compose(source);
        var envelope = Envelope(Event());
        await InsertCopyAsync(database.AppConnectionString, envelope, First);
        var before = (await RowsAsync(database.AppConnectionString, envelope.Event.EventId)).Single();

        await services.GetRequiredService<IEventBus>().PublishAsync(envelope);

        var rows = await RowsAsync(database.AppConnectionString, envelope.Event.EventId);
        rows.Should().HaveCount(2);
        rows.Single(row => row.Subscription == First).Should().BeEquivalentTo(before);
        rows.Single(row => row.Subscription == Second).Should().NotBeNull();
    }

    [Fact]
    public async Task Equivalent_json_with_different_object_key_order_is_an_identical_copy()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var services = Compose(source);
        var envelope = Envelope(Event());
        using var document = JsonDocument.Parse(((IntegrationEventBase)envelope.Event).ToPayloadJson());
        var reordered = JsonSerializer.Serialize(document.RootElement.EnumerateObject()
            .Reverse().ToDictionary(property => property.Name, property => property.Value));
        await InsertCopyAsync(database.AppConnectionString, envelope, First, reordered);
        var existing = (await RowsAsync(database.AppConnectionString, envelope.Event.EventId)).Single();

        await services.GetRequiredService<IEventBus>().PublishAsync(envelope);

        var rows = await RowsAsync(database.AppConnectionString, envelope.Event.EventId);
        rows.Should().HaveCount(2);
        rows.Single(row => row.Subscription == First).Should().BeEquivalentTo(existing);
    }

    [Fact]
    public async Task A_conflicting_later_copy_rolls_back_the_new_earlier_copy()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var services = Compose(source);
        var envelope = Envelope(Event());
        await InsertCopyAsync(database.AppConnectionString, Envelope(
            ((ProofEvent)envelope.Event) with { SubjectId = Guid.CreateVersion7() }), Second);
        var before = await RowsAsync(database.AppConnectionString, envelope.Event.EventId);

        await FluentActions.Awaiting(() => services.GetRequiredService<IEventBus>().PublishAsync(envelope))
            .Should().ThrowAsync<InvalidOperationException>();

        (await RowsAsync(database.AppConnectionString, envelope.Event.EventId)).Should().BeEquivalentTo(before);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("topic")]
    [InlineData("partition")]
    public async Task Stored_contract_field_collision_refuses_without_overwriting(string field)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var services = Compose(source);
        var envelope = Envelope(Event());
        await InsertCopyAsync(database.AppConnectionString, envelope, First,
            storedType: field == "type" ? "proof.previous.contract" : null,
            storedTopic: field == "topic" ? "learnstack.proof.other" : null,
            storedPartition: field == "partition" ? Guid.CreateVersion7().ToString() : null);
        var before = await RowsAsync(database.AppConnectionString, envelope.Event.EventId);

        await FluentActions.Awaiting(() => services.GetRequiredService<IEventBus>().PublishAsync(envelope))
            .Should().ThrowAsync<InvalidOperationException>();

        (await RowsAsync(database.AppConnectionString, envelope.Event.EventId)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Simultaneous_duplicate_publishers_commit_only_the_declared_tuple_copies()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var first = Compose(source);
        await using var second = Compose(source);
        var envelope = Envelope(Event());
        var buses = Enumerable.Range(0, 8)
            .Select(index => (index % 2 == 0 ? first : second).GetRequiredService<IEventBus>())
            .ToArray();

        await Task.WhenAll(buses.Select(bus => Task.Run(() => bus.PublishAsync(envelope))));

        (await RowsAsync(database.AppConnectionString, envelope.Event.EventId))
            .Select(row => row.Subscription).Should().BeEquivalentTo([First, Second]);
        ExplodingHandler.Constructions.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_publisher_waits_for_an_uncommitted_duplicate_then_reconciles_its_outcome(
        bool firstCommits)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var services = Compose(source);
        var envelope = Envelope(Event());
        await using var blocker = await OpenAsync(database.AppConnectionString);
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(blocker, blockerTransaction, SchemaFixture.TenantA);
        await SchemaQueries.SetSettingAsync(blocker, blockerTransaction, "app.organization_id",
            SchemaFixture.OrgA1.ToString());
        await InsertMatchingFirstAsync(blocker, blockerTransaction, envelope);
        await using var pid = new NpgsqlCommand("SELECT pg_backend_pid()", blocker, blockerTransaction);
        var blockerPid = (int)(await pid.ExecuteScalarAsync())!;

        var publish = services.GetRequiredService<IEventBus>().PublishAsync(envelope);
        await WaitForBlockedAcceptanceAsync(database.AppConnectionString, blockerPid);
        publish.IsCompleted.Should().BeFalse("a separate app transaction owns the tuple until commit or rollback");
        if (firstCommits) await blockerTransaction.CommitAsync();
        else await blockerTransaction.RollbackAsync();
        var committedFirst = firstCommits
            ? (await RowsAsync(database.AppConnectionString, envelope.Event.EventId))
                .Single(row => row.Subscription == First)
            : null;

        await publish.WaitAsync(TimeSpan.FromSeconds(10));

        var rows = await RowsAsync(database.AppConnectionString, envelope.Event.EventId);
        rows.Select(row => row.Subscription).Should().BeEquivalentTo([First, Second]);
        if (firstCommits)
            rows.Single(row => row.Subscription == First).Should().BeEquivalentTo(committedFirst);
        ExplodingHandler.Constructions.Should().Be(0);
    }

    [Fact]
    public async Task Publication_uses_a_fresh_transaction_and_restores_the_caller_ambient_context()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        var caller = EventTenantContext.FromEnvelope(Envelope(Event(SchemaFixture.TenantB),
            organization: SchemaFixture.OrgB1));
        await using var services = Compose(source);
        var accessor = services.GetRequiredService<ITenantContextAccessor>();
        accessor.Current = caller;
        await using var publisher = new NpgsqlUnitOfWork(source,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var publisherFrame = await publisher.BeginTransactionAsync();
        await publisher.SetTenantContextAsync(caller);
        var marker = Guid.CreateVersion7();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO inbox_messages (tenant_id, subscription_id, event_id)
            VALUES (@tenant, @subscription, @event)
            """, (NpgsqlConnection)publisher.Connection, (NpgsqlTransaction)publisher.Transaction!))
        {
            insert.Parameters.AddWithValue("tenant", SchemaFixture.TenantB);
            insert.Parameters.AddWithValue("subscription", First);
            insert.Parameters.AddWithValue("event", marker);
            (await insert.ExecuteNonQueryAsync()).Should().Be(1);
        }
        var envelope = Envelope(Event());

        await services.GetRequiredService<IEventBus>().PublishAsync(envelope);

        accessor.Current.Should().BeSameAs(caller);
        publisher.HasActiveTransaction.Should().BeTrue();
        (await RowsAsync(database.AppConnectionString, envelope.Event.EventId)).Should().HaveCount(2,
            "acceptance committed while the publisher's separate transaction stayed open");
        (await InboxCountAsync(database.AppConnectionString, SchemaFixture.TenantB, marker)).Should().Be(0);
        await publisherFrame.FailAsync();
        (await InboxCountAsync(database.AppConnectionString, SchemaFixture.TenantB, marker)).Should().Be(0);
    }

    [Fact]
    public async Task Accepted_copies_obey_the_actual_application_role_tenant_and_organization_scope()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var services = Compose(source);
        var bus = services.GetRequiredService<IEventBus>();
        var a1 = Envelope(Event());
        var a2 = Envelope(Event(), organization: SchemaFixture.OrgA2);
        var b1 = Envelope(Event(SchemaFixture.TenantB), organization: SchemaFixture.OrgB1);
        await bus.PublishAsync(a1);
        await bus.PublishAsync(a2);
        await bus.PublishAsync(b1);

        (await VisibleCountAsync(database.AppConnectionString, SchemaFixture.TenantA,
            SchemaFixture.OrgA1, a1.Event.EventId)).Should().Be(2);
        (await VisibleCountAsync(database.AppConnectionString, SchemaFixture.TenantA,
            SchemaFixture.OrgA1, a2.Event.EventId)).Should().Be(0);
        (await VisibleCountAsync(database.AppConnectionString, SchemaFixture.TenantA,
            SchemaFixture.OrgA1, b1.Event.EventId)).Should().Be(0);
        (await VisibleCountAsync(database.AppConnectionString, SchemaFixture.TenantA,
            SchemaFixture.OrgA2, a2.Event.EventId)).Should().Be(2);
        (await VisibleCountAsync(database.AppConnectionString, SchemaFixture.TenantB,
            SchemaFixture.OrgB1, b1.Event.EventId)).Should().Be(2);
    }

    [Fact]
    public async Task Blocked_publication_does_not_leak_ambient_scope_or_block_another_tenant_partition()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var services = Compose(source);
        var bus = services.GetRequiredService<IEventBus>();
        var accessor = services.GetRequiredService<ITenantContextAccessor>();
        var a1 = Envelope(Event());
        var b1 = Envelope(Event(SchemaFixture.TenantB), organization: SchemaFixture.OrgB1);
        a1.PartitionKey.Should().NotBe(b1.PartitionKey);
        await using var blocker = await OpenAsync(database.AppConnectionString);
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(blocker, blockerTransaction, SchemaFixture.TenantA);
        await SchemaQueries.SetSettingAsync(blocker, blockerTransaction, "app.organization_id",
            SchemaFixture.OrgA1.ToString());
        await InsertMatchingFirstAsync(blocker, blockerTransaction, a1);
        await using var pid = new NpgsqlCommand("SELECT pg_backend_pid()", blocker, blockerTransaction);
        var blockerPid = (int)(await pid.ExecuteScalarAsync())!;
        var blockedA = bus.PublishAsync(a1);
        await WaitForBlockedAcceptanceAsync(database.AppConnectionString, blockerPid);

        try
        {
            await bus.PublishAsync(b1).WaitAsync(TimeSpan.FromSeconds(5));
            blockedA.IsCompleted.Should().BeFalse();
            (await VisibleCountAsync(database.AppConnectionString, SchemaFixture.TenantB,
                SchemaFixture.OrgB1, b1.Event.EventId)).Should().Be(2);
            (await VisibleCountAsync(database.AppConnectionString, SchemaFixture.TenantA,
                SchemaFixture.OrgA1, b1.Event.EventId)).Should().Be(0);
            (await RowsAsync(database.AppConnectionString, a1.Event.EventId)).Should().BeEmpty();
            accessor.Current.Should().BeNull("the publisher's ambient context was empty before both calls");
        }
        finally
        {
            await blockerTransaction.CommitAsync();
            await blockedA.WaitAsync(TimeSpan.FromSeconds(10));
        }

        (await RowsAsync(database.AppConnectionString, a1.Event.EventId)).Should().HaveCount(2);
        accessor.Current.Should().BeNull();
    }

    [Fact]
    public async Task Cancellation_before_publication_writes_no_copies()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var services = Compose(source);
        var envelope = Envelope(Event());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await FluentActions.Awaiting(() => services.GetRequiredService<IEventBus>()
                .PublishAsync(envelope, cancellation.Token)).Should().ThrowAsync<OperationCanceledException>();

        (await RowsAsync(database.AppConnectionString, envelope.Event.EventId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_while_the_second_copy_waits_rolls_back_the_first_copy()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var services = Compose(source);
        var envelope = Envelope(Event());
        await using var blocker = await OpenAsync(database.AppConnectionString);
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(blocker, blockerTransaction, SchemaFixture.TenantA);
        await SchemaQueries.SetSettingAsync(blocker, blockerTransaction, "app.organization_id",
            SchemaFixture.OrgA1.ToString());
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO event_deliveries
                (tenant_id, subscription_id, event_id, organization_id, occurred_at,
                 correlation_id, type, topic, partition_key, payload)
            VALUES (@tenant, @subscription, @event, @organization, @occurred,
                @trace, @type, @topic, @partition, @payload)
            """, blocker, blockerTransaction))
        {
            insert.Parameters.AddWithValue("tenant", envelope.Event.TenantId);
            insert.Parameters.AddWithValue("subscription", Second);
            insert.Parameters.AddWithValue("event", envelope.Event.EventId);
            insert.Parameters.AddWithValue("organization", SchemaFixture.OrgA1);
            insert.Parameters.AddWithValue("occurred", envelope.Event.OccurredAt);
            insert.Parameters.AddWithValue("trace", Trace);
            insert.Parameters.AddWithValue("type", envelope.Event.GetType().AssemblyQualifiedName!);
            insert.Parameters.AddWithValue("topic", envelope.Topic);
            insert.Parameters.AddWithValue("partition", envelope.PartitionKey);
            insert.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb)
            { Value = ((IntegrationEventBase)envelope.Event).ToPayloadJson() });
            (await insert.ExecuteNonQueryAsync()).Should().Be(1);
        }
        await using var pid = new NpgsqlCommand("SELECT pg_backend_pid()", blocker, blockerTransaction);
        var blockerPid = (int)(await pid.ExecuteScalarAsync())!;
        using var cancellation = new CancellationTokenSource();
        var publish = services.GetRequiredService<IEventBus>().PublishAsync(envelope, cancellation.Token);

        await WaitForBlockedAcceptanceAsync(database.AppConnectionString, blockerPid);
        publish.IsCompleted.Should().BeFalse();
        cancellation.Cancel();
        await FluentActions.Awaiting(() => publish).Should().ThrowAsync<Exception>();
        await blockerTransaction.RollbackAsync();

        (await RowsAsync(database.AppConnectionString, envelope.Event.EventId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Dropped_commit_acknowledgement_leaves_durable_copies_and_republication_is_idempotent()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var proxy = new CommitResponseDroppingProxy(database.AppConnectionString);
        await using var uncertainSource = ApplicationDataSource.Build(proxy.ConnectionString);
        await using var uncertain = Compose(uncertainSource);
        var envelope = Envelope(Event());

        await FluentActions.Awaiting(() => uncertain.GetRequiredService<IEventBus>().PublishAsync(envelope))
            .Should().ThrowAsync<Exception>();
        proxy.DroppedCommitResponses.Should().Be(1);
        var accepted = await RowsAsync(database.AppConnectionString, envelope.Event.EventId);
        accepted.Should().HaveCount(2);

        await using var retrySource = ApplicationDataSource.Build(database.AppConnectionString);
        await using var retry = Compose(retrySource);
        await retry.GetRequiredService<IEventBus>().PublishAsync(envelope);
        (await RowsAsync(database.AppConnectionString, envelope.Event.EventId)).Should().BeEquivalentTo(accepted);
    }

    [Fact]
    public async Task Unknown_pending_and_retired_contracts_refuse_and_explicit_absence_is_validated()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        var fact = Event();
        var envelope = Envelope(fact);

        foreach (var disposition in new[] { "unknown", "pending", "retired" })
        {
            await using var services = Compose(source, disposition);
            await FluentActions.Awaiting(() => services.GetRequiredService<IEventBus>().PublishAsync(envelope))
                .Should().ThrowAsync<InvalidOperationException>();
            (await RowsAsync(database.AppConnectionString, fact.EventId)).Should().BeEmpty();
        }

        await using var absent = Compose(source, "absent");
        await absent.GetRequiredService<IEventBus>().PublishAsync(envelope);
        (await RowsAsync(database.AppConnectionString, fact.EventId)).Should().BeEmpty();
    }

    private static ServiceProvider Compose(NpgsqlDataSource source, string disposition = "active", bool withAliases = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(source);
        services.AddLearnStackObservabilityServices();
        services.AddScoped<ITenantContext>(provider => provider.GetRequiredService<ITenantContextAccessor>().Current
            ?? UnresolvedTenantContext.Instance);
        services.AddScoped<IUnitOfWork, NpgsqlUnitOfWork>();
        services.AddScoped<PostgresDeliveryAcceptance>();
        services.AddSingleton<IPartitionSerializer, PartitionSerializer>();
        if (disposition == "active")
        {
            services.AddScoped<IIntegrationEventHandler<ProofEvent>, ExplodingHandler>();
            services.AddScoped<IIntegrationEventHandler<ProofEvent>, OtherExplodingHandler>();
        }
        var handlers = IntegrationEventHandlerRegistry.FromServiceDescriptors(services);
        OutboxEventRegistration[] registrations = disposition switch
        {
            "active" => [OutboxEventRegistration.Required(typeof(ProofEvent),
                [OutboxSubscriptionManifest.Active(First, typeof(ExplodingHandler)),
                 OutboxSubscriptionManifest.Active(Second, typeof(OtherExplodingHandler))],
                withAliases ? [EarlierIdentity] : [])],
            "pending" => [OutboxEventRegistration.Required(typeof(ProofEvent),
                [OutboxSubscriptionManifest.Pending(First)])],
            "absent" => [OutboxEventRegistration.IntentionallyUnsubscribed(typeof(ProofEvent))],
            _ => [OutboxEventRegistration.IntentionallyUnsubscribed(typeof(OtherEvent))],
        };
        if (withAliases)
            registrations = [.. registrations,
                OutboxEventRegistration.IntentionallyUnsubscribed(typeof(OtherEvent), OtherEarlierIdentity)];
        string[] retired = disposition == "retired" ? [typeof(ProofEvent).AssemblyQualifiedName!]
            : withAliases ? [RetiredIdentity] : [];
        services.AddSingleton(OutboxEventRegistry.Create(registrations, retired, handlers,
            [typeof(ProofEvent).Assembly]));
        services.AddSingleton<IEventBus, InProcessEventBus>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static ProofEvent Event(Guid? tenant = null) => new()
    {
        EventId = Guid.CreateVersion7(),
        TenantId = tenant ?? SchemaFixture.TenantA,
        OccurredAt = Occurred,
        SubjectId = Guid.CreateVersion7(),
    };

    private static IntegrationEventEnvelope Envelope(ProofEvent fact, Guid? organization = null,
        Guid? causation = null, UserId? actor = null, string trace = Trace) =>
        new(fact, trace, organization ?? SchemaFixture.OrgA1, causation, actor);

    private static async Task InsertCopyAsync(string connectionString, IntegrationEventEnvelope envelope,
        string subscription, string? payload = null, string? storedType = null,
        string? storedTopic = null, string? storedPartition = null)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(connection);
        await SchemaQueries.SetTenantAsync(connection, transaction, envelope.Event.TenantId);
        await SchemaQueries.SetSettingAsync(connection, transaction, "app.organization_id",
            envelope.OrganizationId?.ToString() ?? string.Empty);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO event_deliveries
                (tenant_id, subscription_id, event_id, organization_id, occurred_at,
                 correlation_id, causation_id, actor_user_id, type, topic, partition_key, payload)
            VALUES (@tenant, @subscription, @event, @organization, @occurred,
                @correlation, @causation, @actor, @type, @topic, @partition, @payload)
            """, connection, transaction);
        insert.Parameters.AddWithValue("tenant", envelope.Event.TenantId);
        insert.Parameters.AddWithValue("subscription", subscription);
        insert.Parameters.AddWithValue("event", envelope.Event.EventId);
        insert.Parameters.AddWithValue("organization", (object?)envelope.OrganizationId ?? DBNull.Value);
        insert.Parameters.AddWithValue("occurred", envelope.Event.OccurredAt);
        insert.Parameters.AddWithValue("correlation", envelope.CorrelationId);
        insert.Parameters.AddWithValue("causation", (object?)envelope.CausationId ?? DBNull.Value);
        insert.Parameters.AddWithValue("actor", (object?)envelope.ActorUserId?.Value ?? DBNull.Value);
        insert.Parameters.AddWithValue("type", storedType ?? envelope.Event.GetType().AssemblyQualifiedName!);
        insert.Parameters.AddWithValue("topic", storedTopic ?? envelope.Topic);
        insert.Parameters.AddWithValue("partition", storedPartition ?? envelope.PartitionKey);
        insert.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb)
        { Value = payload ?? ((IntegrationEventBase)envelope.Event).ToPayloadJson() });
        (await insert.ExecuteNonQueryAsync()).Should().Be(1);
        await transaction.CommitAsync();
    }

    private static async Task InsertMatchingFirstAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, IntegrationEventEnvelope envelope)
    {
        await using var insert = new NpgsqlCommand("""
            INSERT INTO event_deliveries
                (tenant_id, subscription_id, event_id, organization_id, occurred_at,
                 correlation_id, type, topic, partition_key, payload)
            VALUES (@tenant, @subscription, @event, @organization, @occurred,
                @trace, @type, @topic, @partition, @payload)
            """, connection, transaction);
        insert.Parameters.AddWithValue("tenant", envelope.Event.TenantId);
        insert.Parameters.AddWithValue("subscription", First);
        insert.Parameters.AddWithValue("event", envelope.Event.EventId);
        insert.Parameters.AddWithValue("organization", envelope.OrganizationId!.Value);
        insert.Parameters.AddWithValue("occurred", envelope.Event.OccurredAt);
        insert.Parameters.AddWithValue("trace", envelope.CorrelationId);
        insert.Parameters.AddWithValue("type", envelope.Event.GetType().AssemblyQualifiedName!);
        insert.Parameters.AddWithValue("topic", envelope.Topic);
        insert.Parameters.AddWithValue("partition", envelope.PartitionKey);
        insert.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb)
        { Value = ((IntegrationEventBase)envelope.Event).ToPayloadJson() });
        (await insert.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private static async Task<List<DeliveryRow>> RowsAsync(string connectionString, Guid eventId)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(connection);
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
        await SchemaQueries.SetSettingAsync(connection, transaction, "app.organization_id",
            SchemaFixture.OrgA1.ToString());
        await SchemaQueries.SetSettingAsync(connection, transaction, "app.scope", "tenant");
        await using var command = new NpgsqlCommand("""
            SELECT tenant_id, subscription_id, event_id, organization_id, attempts,
                   processed_at, last_error, available_after, locked_by, locked_until,
                   correlation_id, causation_id, actor_user_id, occurred_at, type, topic,
                   partition_key, payload::text, xmin::text
            FROM event_deliveries WHERE event_id = @event ORDER BY subscription_id
            """, connection, transaction);
        command.Parameters.AddWithValue("event", eventId);
        var rows = new List<DeliveryRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new DeliveryRow(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7), reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
                reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetGuid(11),
                reader.IsDBNull(12) ? null : reader.GetGuid(12), reader.GetFieldValue<DateTimeOffset>(13),
                reader.GetString(14), reader.GetString(15), reader.GetString(16), reader.GetString(17),
                reader.GetString(18)));
        return rows;
    }

    private static async Task<long> InboxCountAsync(string connectionString, Guid tenantId, Guid eventId)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, tenantId);
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM inbox_messages WHERE tenant_id = @tenant AND event_id = @event
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("event", eventId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> VisibleCountAsync(string connectionString, Guid tenantId,
        Guid organizationId, Guid eventId)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(connection);
        await SchemaQueries.SetTenantAsync(connection, transaction, tenantId);
        await SchemaQueries.SetSettingAsync(connection, transaction, "app.organization_id",
            organizationId.ToString());
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM event_deliveries WHERE event_id = @event", connection, transaction);
        command.Parameters.AddWithValue("event", eventId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task WaitForBlockedAcceptanceAsync(string connectionString, int blockerPid)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var connection = await OpenAsync(connectionString, deadline.Token);
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            await using var command = new NpgsqlCommand("""
                SELECT count(*) FROM pg_stat_activity
                WHERE wait_event_type = 'Lock' AND @blocker = ANY(pg_blocking_pids(pid))
                  AND query LIKE 'INSERT INTO event_deliveries%'
                """, connection);
            command.Parameters.AddWithValue("blocker", blockerPid);
            if ((long)(await command.ExecuteScalarAsync(deadline.Token))! > 0) return;
            await Task.Yield();
        }
    }

    [IntegrationEventPartition(nameof(SubjectId))]
    private sealed record ProofEvent : IntegrationEventBase
    {
        public required Guid SubjectId { get; init; }
        public Guid? RelatedId { get; init; }
        public override string Topic => "learnstack.proof.delivery";
        public override string PartitionKey => SubjectId.ToString();
    }

    [IntegrationEventPartition(nameof(SubjectId))]
    private sealed record OtherEvent : IntegrationEventBase
    {
        public required Guid SubjectId { get; init; }
        public override string Topic => "learnstack.proof.other";
        public override string PartitionKey => SubjectId.ToString();
    }

    private class ExplodingHandler : IIntegrationEventHandler<ProofEvent>
    {
        public static int Constructions;
        public static int Invocations;
        public ExplodingHandler() { Interlocked.Increment(ref Constructions); throw new InvalidOperationException("handler construction is forbidden"); }
        public Task HandleAsync(ProofEvent @event, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Invocations);
            throw new InvalidOperationException("handler invocation is forbidden");
        }
    }

    private sealed class OtherExplodingHandler : ExplodingHandler { }

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString,
        CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private sealed record DeliveryRow(Guid Tenant, string Subscription, Guid EventId, Guid? Organization,
        int Attempts, DateTimeOffset? ProcessedAt, string? LastError, DateTimeOffset AvailableAfter,
        string? LockedBy, DateTimeOffset? LockedUntil, string CorrelationId, Guid? CausationId,
        Guid? ActorUserId, DateTimeOffset OccurredAt, string Type, string Topic, string Partition,
        string Payload, string Version);
}
