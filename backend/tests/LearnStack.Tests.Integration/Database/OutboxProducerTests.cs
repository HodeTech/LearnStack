using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Application.Audit;
using LearnStack.Application.Messaging;
using LearnStack.Application.Persistence;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Modules.Customization.Domain;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Immediate production through the real application role and persistence registrations.
/// The one nested command has exact component authority, not a human permission grant.
/// Committing or deliberately breaking a database control uses an isolated database;
/// shared-schema cases roll back every successful INSERT.
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class OutboxProducerTests(SchemaFixture schema)
{
    private const string DirectOperation = "customization.content_type.outbox_probe";
    private const string AmbientCorrelation = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
    private static readonly DateTimeOffset Occurred = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid HumanActor = new("12345678-1234-7123-8123-123456789abc");
    private static readonly Error Refusal = new(new LocalizedMessage("lockey_business_rule_violation"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_roots_insert_the_complete_runtime_event_before_business_and_MUST_audit_commit(bool api)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await SeedTenantAsync(database.AppConnectionString);
        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = new ActingContext(OrganizationId.From(SchemaFixture.OrgA1));
        await using var seed = Compose(dataSource, context);
        await using var host = new ConstructionOnlyApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Default", database.AppConnectionString);
            builder.ConfigureServices(services => services.AddSingleton<ITenantContextAccessor>(
                new StaticTenantContextAccessor(context)));
        });
        var root = api ? host.Services : seed;
        await using var scope = root.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var unit = services.GetRequiredService<IUnitOfWork>();
        var businessId = TenantContentTypeId.From(Guid.CreateVersion7());
        IIntegrationEvent integrationEvent = Event();
        var previousActivity = Activity.Current;
        Activity.Current = null;
        string? traceparent = null;

        try
        {
            using var source = new ActivitySource("outbox-proof.no-listener");
            using var control = source.StartActivity("control");
            control.Should().BeNull("the fallback trace is proved without an ActivitySource listener");

            var result = await RunAsync(services, async () =>
            {
                (await ScalarInUnitAsync(unit, "SELECT current_user")).Should().Be("learnstack_app");
                (await ScalarInUnitAsync(unit,
                    "SELECT (rolbypassrls OR rolsuper)::text FROM pg_roles WHERE rolname = current_user"))
                    .Should().Be("false");
                var connection = unit.Connection;
                var transaction = unit.Transaction;
                await WriteBusinessAsync(services, businessId);
                traceparent = Activity.Current!.Id;
                traceparent.Should().MatchRegex("^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$");
                traceparent.Should().NotBe(context.CorrelationId,
                    "an arbitrary ambient string does not supply the current execution span");

                // Mutating the source context after admission cannot retarget delivery
                // organization or the trusted causal actor captured for the invocation.
                context.OrganizationId = OrganizationId.From(SchemaFixture.OrgA2);
                context.UserId = UserId.From(Guid.CreateVersion7());
                await services.GetRequiredService<IOutbox>().EnqueueAsync(integrationEvent);

                unit.Connection.Should().BeSameAs(connection);
                unit.Transaction.Should().BeSameAs(transaction);
                var pending = await RowInUnitAsync(unit, integrationEvent.EventId);
                pending.Should().NotBeNull("EnqueueAsync INSERTs immediately, before any flush or commit");
                pending!.ProcessedAt.Should().BeNull();
                pending.Attempts.Should().Be(0);
                pending.LastError.Should().BeNull();
                (await CountInUnitAsync(unit, "tenant_content_types", businessId.Value)).Should().Be(1);
                (await ScalarInUnitAsync(unit, "SELECT count(*) FROM audit_log")).Should().Be(0L,
                    "the owner has not reached MUST audit composition yet");
                (await ReadRowAsync(database.AppConnectionString, integrationEvent.EventId)).Should().BeNull(
                    "the row is still uncommitted and invisible on an independent application connection");
                return Result.Ok(None.Value);
            }, audited: true);

            result.IsSuccess.Should().BeTrue();
            Activity.Current.Should().BeNull();
        }
        finally
        {
            Activity.Current = previousActivity;
        }

        (await CountAsync(database.AppConnectionString, "tenant_content_types", businessId.Value)).Should().Be(1);
        var row = (await ReadRowAsync(database.AppConnectionString, integrationEvent.EventId))!;
        row.Id.Should().Be(integrationEvent.EventId);
        row.TenantId.Should().Be(SchemaFixture.TenantA);
        row.OrganizationId.Should().Be(SchemaFixture.OrgA1,
            "a non-marked event preserves the organization actually announced to PostgreSQL");
        row.ActorUserId.Should().Be(HumanActor);
        row.CausationId.Should().BeNull("normal command production invents no incoming event");
        row.CorrelationId.Should().Be(traceparent);
        ActivityContext.TryParse(row.CorrelationId, null, out _).Should().BeTrue();
        row.OccurredAt.Should().Be(Occurred);
        row.Type.Should().Be(integrationEvent.GetType().AssemblyQualifiedName);
        row.Topic.Should().Be(integrationEvent.Topic);
        row.PartitionKey.Should().Be(integrationEvent.PartitionKey);
        row.MetadataIsSqlNull.Should().BeTrue("JSON null is not SQL NULL");
        var roundTrip = JsonSerializer.Deserialize<ProbeIntegrationEventV1>(
            row.Payload, IntegrationEventBase.PayloadJsonOptions)!;
        roundTrip.EventId.Should().Be(integrationEvent.EventId);
        roundTrip.SubjectId.Should().Be(((ProbeIntegrationEventV1)integrationEvent).SubjectId,
            "serializing the interface alone loses concrete identifier fields");
        var audit = (await AuditRowsAsync(database.AppConnectionString)).Should().ContainSingle().Subject;
        audit.Operation.Should().Be(DirectOperation);
        audit.OperationClass.Should().Be("Must");
        audit.Outcome.Should().Be("success");
        audit.EntityId.Should().Be(businessId.Value.ToString());
        AssertClean(services);
    }

    [Fact]
    public async Task Enqueue_and_owner_completion_never_publish_to_the_transport()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var transport = new CountingEventBus();
        await using var host = new ConstructionOnlyApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Default", database.AppConnectionString);
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ITenantContextAccessor>(new StaticTenantContextAccessor(new ActingContext()));
                services.AddSingleton<IEventBus>(transport);
            });
        });
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<IEventBus>().Should().BeSameAs(transport);
        services.GetRequiredService<IOutbox>().Should().BeOfType<PostgresOutbox>();
        var integrationEvent = Event();

        (await RunAsync(services, async () =>
        {
            await services.GetRequiredService<IOutbox>().EnqueueAsync(integrationEvent);
            (await RowInUnitAsync(services.GetRequiredService<IUnitOfWork>(), integrationEvent.EventId))
                .Should().NotBeNull();
            transport.Calls.Should().Be(0, "enqueue persists a row without delivering it");
            return Result.Ok(None.Value);
        })).IsSuccess.Should().BeTrue();

        (await ReadRowAsync(database.AppConnectionString, integrationEvent.EventId)).Should().NotBeNull();
        transport.Calls.Should().Be(0, "owner completion commits without transport publication");
        await services.GetRequiredService<IEventBus>().PublishAsync(
            new IntegrationEventEnvelope(integrationEvent, AmbientCorrelation));
        transport.Calls.Should().Be(1, "the resolved transport spy must observe a real call");
        AssertClean(services);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_system_execution_preserves_only_its_separate_causal_human(bool hasCausalHuman)
    {
        await using var dataSource = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        var context = new ActingContext
        {
            UserId = UserId.SystemActor,
            CausalActorUserId = hasCausalHuman ? UserId.From(HumanActor) : null,
        };
        await using var provider = Compose(dataSource, context);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var integrationEvent = Event();

        var result = await RunAsync(services, async () =>
        {
            await services.GetRequiredService<IOutbox>().EnqueueAsync(integrationEvent);
            var row = (await RowInUnitAsync(services.GetRequiredService<IUnitOfWork>(), integrationEvent.EventId))!;
            row.ActorUserId.Should().Be(hasCausalHuman ? HumanActor : null);
            row.OrganizationId.Should().BeNull();
            return Result<None>.Fail(Refusal);
        });

        result.IsFailure.Should().BeTrue();
        (await ReadRowAsync(schema.Postgres.AppConnectionString, integrationEvent.EventId)).Should().BeNull();
        AssertClean(services);
    }

    [Fact]
    public async Task A_real_nested_command_keeps_its_MUST_intent_until_the_outbox_owner_commits()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await SeedTenantAsync(database.AppConnectionString);
        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var provider = Compose(dataSource);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var unit = services.GetRequiredService<IUnitOfWork>();
        var organizationId = OrganizationId.From(Guid.CreateVersion7());
        var integrationEvent = Event();

        var result = await RunAsync(services, async () =>
        {
            var nested = await services.GetRequiredService<IMediator>().Send(
                new CreateOrganizationCommand(organizationId, "outbox-branch", "Outbox branch"));
            nested.IsSuccess.Should().BeTrue("the actual eight-step pipeline admitted its joiner: {0}", nested.Error);
            services.GetRequiredService<IAuditStateCapture>().State.Should().Be(AuditIntentState.Pending);
            services.GetRequiredService<ProducerExecution>().HasInvocation.Should().BeTrue();
            await services.GetRequiredService<IOutbox>().EnqueueAsync(integrationEvent);
            (await CountInUnitAsync(unit, "organizations", organizationId.Value)).Should().Be(1);
            return Result.Ok(None.Value);
        });

        result.IsSuccess.Should().BeTrue();
        (await ReadRowAsync(database.AppConnectionString, integrationEvent.EventId)).Should().NotBeNull();
        (await CountAsync(database.AppConnectionString, "organizations", organizationId.Value)).Should().Be(1);
        var audit = (await AuditRowsAsync(database.AppConnectionString)).Should().ContainSingle().Subject;
        audit.Operation.Should().Be("tenancy.organization.create");
        audit.EntityId.Should().Be(organizationId.Value.ToString());
        audit.Outcome.Should().Be("success");
        AssertClean(services);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Actual_INSERT_receipts_prevent_an_absorbed_direct_or_descendant_refusal_from_committing(int descendantDepth)
    {
        await using var dataSource = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var provider = Compose(dataSource);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var coordinator = services.GetRequiredService<TransactionExecutionCoordinator>();
        var unit = services.GetRequiredService<IUnitOfWork>();
        var businessId = TenantContentTypeId.From(Guid.CreateVersion7());
        var integrationEvent = Event();
        var observed = false;
        var absorbed = false;

        var run = () => RunAsync(services, async () =>
        {
            var refused = await coordinator.ExecuteAsync(null, TransactionMode.ReadWrite, "refusing-producer", async () =>
            {
                async Task<Result<None>> ProduceAsync()
                {
                    await WriteBusinessAsync(services, businessId);
                    await services.GetRequiredService<IOutbox>().EnqueueAsync(integrationEvent);
                    (await RowInUnitAsync(unit, integrationEvent.EventId)).Should().NotBeNull();
                    (await CountInUnitAsync(unit, "tenant_content_types", businessId.Value)).Should().Be(1);
                    observed = true;
                    return Result.Ok(None.Value);
                }

                var produced = descendantDepth == 0
                    ? await ProduceAsync()
                    : await coordinator.ExecuteAsync(null, TransactionMode.ReadWrite, "successful-descendant", ProduceAsync);
                produced.IsSuccess.Should().BeTrue();
                return Result<None>.Fail(Refusal);
            });

            refused.Error.Should().BeSameAs(Refusal);
            unit.IsRollbackOnly.Should().BeTrue("the real outbox records receipts in every ancestor");
            absorbed = true;
            return Result.Ok(None.Value);
        });

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*rollback-only*");
        observed.Should().BeTrue();
        absorbed.Should().BeTrue();
        (await ReadRowAsync(schema.Postgres.AppConnectionString, integrationEvent.EventId)).Should().BeNull();
        (await CountAsync(schema.Postgres.AppConnectionString, "tenant_content_types", businessId.Value)).Should().Be(0);
        AssertClean(services);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_or_fault_after_observed_business_and_outbox_writes_rolls_both_back(bool cancel)
    {
        await using var dataSource = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var provider = Compose(dataSource);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var unit = services.GetRequiredService<IUnitOfWork>();
        var businessId = TenantContentTypeId.From(Guid.CreateVersion7());
        var integrationEvent = Event();
        var observed = false;
        using var cancellation = new CancellationTokenSource();
        var fault = new InvalidOperationException("Deliberate failure after production");

        var run = () => RunAsync(services, async () =>
        {
            await WriteBusinessAsync(services, businessId);
            await services.GetRequiredService<IOutbox>().EnqueueAsync(integrationEvent);
            (await RowInUnitAsync(unit, integrationEvent.EventId)).Should().NotBeNull();
            (await CountInUnitAsync(unit, "tenant_content_types", businessId.Value)).Should().Be(1);
            observed = true;
            if (cancel)
            {
                await cancellation.CancelAsync();
                throw new OperationCanceledException(cancellation.Token);
            }
            throw fault;
        });

        if (cancel)
        {
            (await run.Should().ThrowAsync<OperationCanceledException>()).Which.CancellationToken
                .Should().Be(cancellation.Token);
        }
        else
        {
            (await run.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(fault);
        }
        observed.Should().BeTrue();
        (await CountAsync(schema.Postgres.AppConnectionString, "tenant_content_types", businessId.Value)).Should().Be(0);
        (await ReadRowAsync(schema.Postgres.AppConnectionString, integrationEvent.EventId)).Should().BeNull();
        AssertClean(services);
    }

    [Fact]
    public async Task A_real_MUST_audit_INSERT_failure_rolls_back_positively_observed_business_and_outbox_rows()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await SeedTenantAsync(database.AppConnectionString);
        await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        await using (var deny = new NpgsqlCommand(
            "ALTER TABLE audit_log ADD CONSTRAINT reject_outbox_probe CHECK (operation <> 'customization.content_type.outbox_probe')", owner))
        {
            await deny.ExecuteNonQueryAsync();
        }
        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var provider = Compose(dataSource);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var businessId = TenantContentTypeId.From(Guid.CreateVersion7());
        var integrationEvent = Event();
        var observed = false;

        var run = () => RunAsync(services, async () =>
        {
            await WriteBusinessAsync(services, businessId);
            await services.GetRequiredService<IOutbox>().EnqueueAsync(integrationEvent);
            var unit = services.GetRequiredService<IUnitOfWork>();
            (await CountInUnitAsync(unit, "tenant_content_types", businessId.Value)).Should().Be(1);
            (await RowInUnitAsync(unit, integrationEvent.EventId)).Should().NotBeNull();
            observed = true;
            return Result.Ok(None.Value);
        }, audited: true);

        var failure = (await run.Should().ThrowAsync<AuditWriteFailedException>()).Which;
        failure.InnerException.Should().BeOfType<PostgresException>().Which.SqlState
            .Should().Be(PostgresErrorCodes.CheckViolation);
        observed.Should().BeTrue();
        (await CountAsync(database.AppConnectionString, "tenant_content_types", businessId.Value)).Should().Be(0);
        (await ReadRowAsync(database.AppConnectionString, integrationEvent.EventId)).Should().BeNull();
        (await AuditRowsAsync(database.AppConnectionString)).Should().BeEmpty();
        AssertClean(services);
    }

    [Theory]
    [InlineData("empty-tenant")]
    [InlineData("sentinel-tenant")]
    [InlineData("foreign-tenant")]
    [InlineData("read-only")]
    [InlineData("unannounced")]
    public async Task Enqueue_refuses_an_unusable_event_tenant_or_transaction_before_INSERT(string refusal)
    {
        await using var dataSource = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        ITenantContext context = refusal == "unannounced" ? UnresolvedTenantContext.Instance : new ActingContext();
        await using var provider = Compose(dataSource, context);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var integrationEvent = Event(refusal switch
        {
            "empty-tenant" => Guid.Empty,
            "sentinel-tenant" => TenantId.PlatformSentinel.Value,
            "foreign-tenant" => SchemaFixture.TenantB,
            _ => SchemaFixture.TenantA,
        });
        var reached = false;

        var result = await RunAsync(services, async () =>
        {
            reached = true;
            var enqueue = () => services.GetRequiredService<IOutbox>().EnqueueAsync(integrationEvent);
            await enqueue.Should().ThrowAsync<InvalidOperationException>();
            CurrentInvocation(services).HasProducedWork.Should().BeFalse();
            services.GetRequiredService<IUnitOfWork>().IsRollbackOnly.Should().BeFalse();
            return Result.Ok(None.Value);
        }, mode: refusal == "read-only" ? TransactionMode.ReadOnly : TransactionMode.ReadWrite);

        result.IsSuccess.Should().BeTrue("refused enqueue attempts alone do not leave production receipts");
        reached.Should().BeTrue();
        (await ReadRowAsync(schema.Postgres.AppConnectionString, integrationEvent.EventId)).Should().BeNull();
        AssertClean(services);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_raw_transaction_cannot_authorize_outbox_production_even_when_announced_or_joined(bool announced, bool join)
    {
        await using var dataSource = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var provider = Compose(dataSource);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var unit = services.GetRequiredService<IUnitOfWork>();
        var integrationEvent = Event();
        await using var owner = await unit.BeginTransactionAsync();
        if (announced)
        {
            await unit.SetTenantContextAsync(new ActingContext());
            unit.GetTenantContextAnnouncement(unit.Transaction).Should().NotBeNull();
        }
        else
        {
            unit.IsTenantContextIssuedOn(unit.Transaction).Should().BeFalse();
        }
        var reached = false;

        async Task<Result<None>> ProduceAsync()
        {
            reached = true;
            var enqueue = () => services.GetRequiredService<IOutbox>().EnqueueAsync(integrationEvent);
            await enqueue.Should().ThrowAsync<InvalidOperationException>().WithMessage("*enrolled writable frame*");
            return Result.Ok(None.Value);
        }

        var result = join
            ? await services.GetRequiredService<TransactionExecutionCoordinator>().ExecuteAsync(
                null, TransactionMode.ReadWrite, "raw-joiner", ProduceAsync)
            : await ProduceAsync();
        result.IsSuccess.Should().BeTrue();
        reached.Should().BeTrue();
        (await RowInUnitAsync(unit, integrationEvent.EventId)).Should().BeNull();
        await owner.FailAsync();
    }

    [Fact]
    public async Task An_outbox_cannot_borrow_a_producer_enrolled_on_another_unit_of_work()
    {
        await using var dataSource = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var provider = Compose(dataSource);
        await using var scope = provider.CreateAsyncScope();
        await using var elsewhere = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var foreignUnit = elsewhere.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var foreignOwner = await foreignUnit.BeginTransactionAsync();
        await foreignUnit.SetTenantContextAsync(new ActingContext());
        var integrationEvent = Event();

        var result = await RunAsync(services, async () =>
        {
            var unit = services.GetRequiredService<IUnitOfWork>();
            var producer = services.GetRequiredService<ProducerExecution>();
            producer.RequireAnnouncement().TenantId.Should().Be(
                foreignUnit.GetTenantContextAnnouncement(foreignUnit.Transaction)!.TenantId,
                "both units really are announced for the same tenant");
            var mismatched = new PostgresOutbox(foreignUnit, producer);
            var enqueue = () => mismatched.EnqueueAsync(integrationEvent);
            await enqueue.Should().ThrowAsync<InvalidOperationException>().WithMessage("*share its producer's unit of work*");
            CurrentInvocation(services).HasProducedWork.Should().BeFalse();
            (await RowInUnitAsync(foreignUnit, integrationEvent.EventId)).Should().BeNull();
            await services.GetRequiredService<IOutbox>().EnqueueAsync(integrationEvent);
            (await RowInUnitAsync(unit, integrationEvent.EventId)).Should().NotBeNull(
                "the matched production registration is the positive control");
            return Result<None>.Fail(Refusal);
        });

        result.IsFailure.Should().BeTrue();
        await foreignOwner.FailAsync();
        AssertClean(services);
    }

    [Fact]
    public async Task An_organization_marked_event_requires_the_announced_organization()
    {
        await using var dataSource = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var provider = Compose(dataSource);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var integrationEvent = new OrganizationProbeIntegrationEventV1
        {
            EventId = Guid.CreateVersion7(),
            TenantId = SchemaFixture.TenantA,
            OccurredAt = Occurred,
            SubjectId = Guid.CreateVersion7(),
        };
        var validControl = Event();

        var result = await RunAsync(services, async () =>
        {
            var enqueue = () => services.GetRequiredService<IOutbox>().EnqueueAsync(integrationEvent);
            await enqueue.Should().ThrowAsync<ArgumentException>().WithMessage("*OrganizationId*");
            CurrentInvocation(services).HasProducedWork.Should().BeFalse();
            await services.GetRequiredService<IOutbox>().EnqueueAsync(validControl);
            (await RowInUnitAsync(services.GetRequiredService<IUnitOfWork>(), validControl.EventId)).Should().NotBeNull();
            return Result<None>.Fail(Refusal);
        });

        result.IsFailure.Should().BeTrue();
        (await ReadRowAsync(schema.Postgres.AppConnectionString, integrationEvent.EventId)).Should().BeNull();
        (await ReadRowAsync(schema.Postgres.AppConnectionString, validControl.EventId)).Should().BeNull();
        AssertClean(services);
    }

    [Fact]
    public async Task Provisioning_uses_the_SQL_announcement_while_the_ambient_context_stays_unresolved()
    {
        await using var dataSource = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var provider = Compose(dataSource, UnresolvedTenantContext.Instance);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var tenantId = TenantId.From(Guid.CreateVersion7());
        var integrationEvent = Event(tenantId.Value);

        var result = await RunAsync(services, async () =>
        {
            services.GetRequiredService<ITenantContext>().IsResolved.Should().BeFalse();
            services.GetRequiredService<ITenantContextAccessor>().Current!.IsResolved.Should().BeFalse();
            await services.GetRequiredService<IOutbox>().EnqueueAsync(integrationEvent);
            var row = (await RowInUnitAsync(services.GetRequiredService<IUnitOfWork>(), integrationEvent.EventId))!;
            row.TenantId.Should().Be(tenantId.Value);
            row.OrganizationId.Should().BeNull();
            row.ActorUserId.Should().BeNull();
            return Result<None>.Fail(Refusal);
        }, provisioning: new ProvisioningProbe(tenantId));

        result.IsFailure.Should().BeTrue();
        AssertClean(services);
    }

    [Fact]
    public async Task A_retained_producer_frame_refuses_a_reused_transaction_object_with_a_later_generation()
    {
        var builder = new NpgsqlDataSourceBuilder(schema.Postgres.AppConnectionString);
        builder.ConnectionStringBuilder.MaxPoolSize = 1;
        builder.ConnectionStringBuilder.NoResetOnClose = true;
        await using var dataSource = builder.Build();
        await using var provider = Compose(dataSource);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var unit = services.GetRequiredService<IUnitOfWork>();
        var integrationEvent = Event();
        var stop = new InvalidOperationException("End stale-generation proof");
        var reached = false;

        var run = () => RunAsync(services, async () =>
        {
            var physicalTransaction = unit.Transaction;
            var first = services.GetRequiredService<ProducerExecution>().RequireAnnouncement();
            await unit.CommitAsync();
            await using var later = await unit.BeginTransactionAsync();
            unit.Transaction.Should().BeSameAs(physicalTransaction,
                "the driver actually reuses this object; an identity-only control would be unsound");
            await unit.SetTenantContextAsync(new ActingContext());
            unit.GetTenantContextAnnouncement(unit.Transaction)!.Generation.Should().BeGreaterThan(first.Generation);
            var enqueue = () => services.GetRequiredService<IOutbox>().EnqueueAsync(integrationEvent);
            await enqueue.Should().ThrowAsync<InvalidOperationException>().WithMessage("*enrolled writable frame*");
            (await services.GetRequiredService<TransactionExecutionCoordinator>().ExecuteAsync(
                null, TransactionMode.ReadWrite, "stale-ancestor-joiner", async () =>
                {
                    await enqueue.Should().ThrowAsync<InvalidOperationException>().WithMessage("*enrolled writable frame*");
                    return Result.Ok(None.Value);
                })).IsSuccess.Should().BeTrue();
            (await RowInUnitAsync(unit, integrationEvent.EventId)).Should().BeNull();
            reached = true;
            throw stop;
        });

        (await run.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(stop);
        reached.Should().BeTrue();
        AssertClean(services);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_duplicate_EventId_poisons_the_unit_without_creating_a_child_receipt(bool absorb)
    {
        await using var dataSource = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var provider = Compose(dataSource);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var unit = services.GetRequiredService<IUnitOfWork>();
        var integrationEvent = Event();
        var observed = false;
        var duplicateAttempted = false;
        PostgresException? insertFailure = null;

        var run = () => RunAsync(services, async () =>
        {
            await services.GetRequiredService<IOutbox>().EnqueueAsync(integrationEvent);
            var parent = CurrentInvocation(services);
            parent.HasProducedWork.Should().BeTrue();
            unit.IsRollbackOnly.Should().BeFalse("a successful INSERT does not poison the unit");
            (await RowInUnitAsync(unit, integrationEvent.EventId)).Should().NotBeNull();
            observed = true;
            return await services.GetRequiredService<TransactionExecutionCoordinator>().ExecuteAsync<Result<None>>(
                null, TransactionMode.ReadWrite, "duplicate-child", async () =>
                {
                    CurrentInvocation(services).HasProducedWork.Should().BeFalse();
                    var enqueue = () => services.GetRequiredService<IOutbox>().EnqueueAsync(integrationEvent);
                    var failure = (await enqueue.Should().ThrowAsync<PostgresException>()).Which;
                    insertFailure = failure;
                    failure.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
                    unit.IsRollbackOnly.Should().BeTrue("the outbox must poison before its caller can absorb the INSERT error");
                    CurrentInvocation(services).HasProducedWork.Should().BeFalse(
                        "the parent's successful INSERT cannot hide a receipt recorded before this failed INSERT");
                    parent.HasProducedWork.Should().BeTrue("failed production cannot erase an earlier receipt");
                    duplicateAttempted = true;
                    if (absorb) return Result.Ok(None.Value);
                    throw failure;
                });
        });

        if (absorb)
        {
            await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*rollback-only*");
        }
        else
        {
            (await run.Should().ThrowAsync<PostgresException>()).Which.Should().BeSameAs(insertFailure);
        }
        observed.Should().BeTrue();
        duplicateAttempted.Should().BeTrue();
        (await ReadRowAsync(schema.Postgres.AppConnectionString, integrationEvent.EventId)).Should().BeNull();
        AssertClean(services);
    }

    [Fact]
    public async Task A_successful_owner_can_produce_in_a_later_generation_of_the_same_DI_scope()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var provider = Compose(dataSource);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var first = Event();
        var second = Event();
        long generation = 0;

        (await RunAsync(services, async () =>
        {
            generation = services.GetRequiredService<ProducerExecution>().RequireAnnouncement().Generation;
            await services.GetRequiredService<IOutbox>().EnqueueAsync(first);
            return Result.Ok(None.Value);
        })).IsSuccess.Should().BeTrue();
        AssertClean(services);

        (await RunAsync(services, async () =>
        {
            CurrentInvocation(services).HasProducedWork.Should().BeFalse(
                "a completed invocation cannot replay its receipt into the new unit");
            services.GetRequiredService<ProducerExecution>().RequireAnnouncement().Generation.Should().BeGreaterThan(generation);
            await services.GetRequiredService<IOutbox>().EnqueueAsync(second);
            return Result.Ok(None.Value);
        })).IsSuccess.Should().BeTrue();

        (await ReadRowAsync(database.AppConnectionString, first.EventId)).Should().NotBeNull();
        (await ReadRowAsync(database.AppConnectionString, second.EventId)).Should().NotBeNull();
        AssertClean(services);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task The_complete_canonical_UTF8_payload_has_an_inclusive_256_KiB_limit(int extraBytes, bool unicodeName)
    {
        // The only extra payload value is a GUID. A long declared member name
        // reaches the bound without smuggling text values or serializer overrides.
        var targetBytes = OutboxPayloadLimits.MaxBytes + extraBytes;
        var prefix = unicodeName ? new string('é', 64) : "Identifier";
        var baseline = BoundaryEvent(prefix);
        var baselineBytes = Encoding.UTF8.GetByteCount(baseline.ToPayloadJson());
        var integrationEvent = BoundaryEvent(prefix + new string('x', targetBytes - baselineBytes));
        var canonical = integrationEvent.ToPayloadJson();
        Encoding.UTF8.GetByteCount(canonical).Should().Be(targetBytes,
            "the measured budget includes identity, routing, timestamp and the complete runtime member name");
        if (unicodeName)
        {
            canonical.Should().Contain("\\u00E9");
            canonical.Should().NotContain("é", "the frozen default encoder escapes non-ASCII names");
            Encoding.UTF8.GetByteCount(canonical.Replace("\\u00E9", "é", StringComparison.Ordinal))
                .Should().BeLessThan(OutboxPayloadLimits.MaxBytes,
                    "counting a hypothetical unescaped representation would understate canonical bytes");
        }
        await using var dataSource = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var provider = Compose(dataSource);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var result = await RunAsync(services, async () =>
        {
            var outbox = services.GetRequiredService<IOutbox>();
            if (extraBytes == 0)
            {
                await outbox.EnqueueAsync(integrationEvent);
                var row = (await RowInUnitAsync(services.GetRequiredService<IUnitOfWork>(), integrationEvent.EventId))!;
                using var payload = JsonDocument.Parse(row.Payload);
                payload.RootElement.GetProperty(prefix + new string('x', targetBytes - baselineBytes))
                    .GetGuid().Should().Be(BoundaryIdentifier);
                CurrentInvocation(services).HasProducedWork.Should().BeTrue();
            }
            else
            {
                var enqueue = () => outbox.EnqueueAsync(integrationEvent);
                await enqueue.Should().ThrowAsync<ArgumentException>().WithMessage("*262144*");
                CurrentInvocation(services).HasProducedWork.Should().BeFalse();
                (await RowInUnitAsync(services.GetRequiredService<IUnitOfWork>(), integrationEvent.EventId)).Should().BeNull();
            }
            return Result<None>.Fail(Refusal);
        });

        result.IsFailure.Should().BeTrue();
        (await ReadRowAsync(schema.Postgres.AppConnectionString, integrationEvent.EventId)).Should().BeNull();
        AssertClean(services);
    }

    private static ServiceProvider Compose(NpgsqlDataSource dataSource, ITenantContext? context = null)
    {
        var services = ComponentOperationRegistration.CreateDatabaseServices(dataSource, context ?? new ActingContext());
        ComponentOperationRegistration.AddExactComponentAuthority(services,
            OperationPolicy.ForTrusted<CreateOrganizationCommand>(
                "tests.component.outbox.create_organization", AuthorizationSourceKind.Seed));
        return services.BuildServiceProvider();
    }

    private static Task<Result<None>> RunAsync(
        IServiceProvider services, Func<Task<Result<None>>> body, bool audited = false,
        TransactionMode mode = TransactionMode.ReadWrite, IProvisionsTenant? provisioning = null)
    {
        var audit = new AuditExecutionCoordinator(
            new ProbeCatalog(services.GetRequiredService<IAuditCatalog>()),
            services.GetRequiredService<IAuditConfigService>(),
            services.GetRequiredService<IAuditStateCapture>(),
            services.GetRequiredService<IAuditStore>(),
            services.GetRequiredService<ITenantContext>(),
            services.GetRequiredService<IClock>(),
            services.GetRequiredService<IGuidFactory>(),
            NullLogger<AuditExecutionCoordinator>.Instance, services.GetRequiredService<IUnitOfWork>());
        return audit.ExecuteAsync(audited ? typeof(DirectHandler) : typeof(OffHandler), provisioning,
            () => services.GetRequiredService<TransactionExecutionCoordinator>().ExecuteAsync(
                provisioning, mode, "outbox-proof", body), CancellationToken.None);
    }

    private static ProbeIntegrationEventV1 Event(Guid? tenantId = null) => new()
    {
        EventId = Guid.CreateVersion7(),
        TenantId = tenantId ?? SchemaFixture.TenantA,
        OccurredAt = Occurred,
        SubjectId = Guid.CreateVersion7(),
    };

    private static async Task WriteBusinessAsync(IServiceProvider services, TenantContentTypeId id)
    {
        var db = services.GetRequiredService<CustomizationDbContext>();
        db.Add(TenantContentType.Create(id, TenantId.From(SchemaFixture.TenantA), $"outbox-{id.Value:N}"[..24], 1,
            LocalizedText.From(("en", "Outbox probe")), """{"type":"object"}""", "default-card",
            services.GetRequiredService<IClock>(), UserId.From(SchemaFixture.Actor)));
        await db.SaveChangesAsync();
    }

    private static void AssertClean(IServiceProvider services)
    {
        var unit = services.GetRequiredService<IUnitOfWork>();
        unit.Transaction.Should().BeNull();
        unit.GetTenantContextAnnouncement(unit.Transaction).Should().BeNull();
        services.GetRequiredService<ProducerExecution>().HasInvocation.Should().BeFalse();
        var capture = services.GetRequiredService<IAuditStateCapture>();
        capture.HasOpenFrame.Should().BeFalse();
        capture.Intents.Should().BeEmpty();
        capture.Changes.Should().BeEmpty();
    }

    // Read-only inspection is necessary here: a failed SQL statement aborts
    // PostgreSQL's transaction regardless of receipt state, so a rollback alone
    // cannot prove that the failed INSERT recorded no successful-production receipt.
    private static ProducerExecution.Invocation CurrentInvocation(IServiceProvider services) =>
        (ProducerExecution.Invocation)typeof(ProducerExecution).GetField("_current",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(
                services.GetRequiredService<ProducerExecution>())!;

    private static async Task<object?> ScalarInUnitAsync(IUnitOfWork unit, string sql)
    {
        await using var command = unit.Connection.CreateCommand();
        command.Transaction = unit.Transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    // Table names are closed fixture constants, never external input.
    private static async Task<long> CountInUnitAsync(IUnitOfWork unit, string table, Guid id)
    {
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE id = @id",
            (NpgsqlConnection)unit.Connection, (NpgsqlTransaction?)unit.Transaction);
        command.Parameters.AddWithValue("id", id);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountAsync(string connectionString, string table, Guid id)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE id = @id", connection, transaction);
        command.Parameters.AddWithValue("id", id);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<OutboxRow?> RowInUnitAsync(IUnitOfWork unit, Guid id) =>
        await ReadRowAsync((NpgsqlConnection)unit.Connection, (NpgsqlTransaction?)unit.Transaction, id);

    private static async Task<OutboxRow?> ReadRowAsync(string connectionString, Guid id)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
        return await ReadRowAsync(connection, transaction, id);
    }

    private static async Task<OutboxRow?> ReadRowAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid id)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id, occurred_at, tenant_id, organization_id, correlation_id, causation_id,
                   actor_user_id, type, topic, partition_key, payload::text, metadata IS NULL,
                   processed_at, attempts, last_error
            FROM outbox_messages WHERE id = @id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new OutboxRow(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetGuid(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetGuid(5), reader.IsDBNull(6) ? null : reader.GetGuid(6),
            reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetBoolean(11),
            reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12), reader.GetInt32(13),
            reader.IsDBNull(14) ? null : reader.GetString(14));
    }

    private static async Task<IReadOnlyList<AuditRow>> AuditRowsAsync(string connectionString)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
        await SchemaQueries.SetSettingAsync(connection, transaction, "app.organization_id", SchemaFixture.OrgA1.ToString());
        await using var command = new NpgsqlCommand(
            "SELECT operation, operation_class, outcome, entity_id FROM audit_log", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<AuditRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new AuditRow(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return rows;
    }

    private static async Task SeedTenantAsync(string connectionString)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO tenants (id, slug, display_name, status, created_at, created_by, row_version)
            VALUES (@id, 'outbox-probe', 'Outbox probe', 'Trial', now(), @actor, 0)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", SchemaFixture.TenantA);
        command.Parameters.AddWithValue("actor", SchemaFixture.Actor);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static readonly Guid BoundaryIdentifier = new("e95685d3-576c-4b48-9553-18f576670eb2");

    /// <summary>
    /// A runtime event contract with one GUID-valued extra property. Only the
    /// declaration's name varies; serialization keeps the canonical frozen options.
    /// </summary>
    private static IntegrationEventBase BoundaryEvent(string identifierName)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("OutboxBoundary" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.RunAndCollect);
        var type = assembly.DefineDynamicModule("Events").DefineType(
            "BoundaryIntegrationEventV1", TypeAttributes.Public | TypeAttributes.Sealed, typeof(IntegrationEventBase));
        type.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(IntegrationEventPartitionAttribute).GetConstructor([typeof(string), typeof(bool)])!,
            [nameof(IIntegrationEvent.TenantId), true]));
        var constructor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
        var constructorIl = constructor.GetILGenerator();
        constructorIl.Emit(OpCodes.Ldarg_0);
        constructorIl.Emit(OpCodes.Call, typeof(IntegrationEventBase).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null)!);
        constructorIl.Emit(OpCodes.Ret);

        foreach (var abstractMethod in typeof(IntegrationEventBase).GetMethods(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(method => method.IsAbstract))
        {
            var implementation = type.DefineMethod(abstractMethod.Name,
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                abstractMethod.ReturnType, Type.EmptyTypes);
            var il = implementation.GetILGenerator();
            if (abstractMethod.Name == "get_Topic")
            {
                il.Emit(OpCodes.Ldstr, "learnstack.customization.contenttype");
            }
            else if (abstractMethod.Name == "get_PartitionKey")
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, typeof(IntegrationEventBase).GetProperty(nameof(IIntegrationEvent.TenantId))!.GetMethod!);
                il.Emit(OpCodes.Box, typeof(Guid));
                il.Emit(OpCodes.Callvirt, typeof(object).GetMethod(nameof(ToString))!);
            }
            else
            {
                // The abstract record clone is irrelevant to serialization; the
                // emitted concrete record still implements its inherited contract.
                abstractMethod.Name.Should().Be("<Clone>$");
                il.Emit(OpCodes.Ldarg_0);
            }
            il.Emit(OpCodes.Ret);
            type.DefineMethodOverride(implementation, abstractMethod);
        }

        var property = type.DefineProperty(identifierName, PropertyAttributes.None, typeof(Guid), Type.EmptyTypes);
        var getter = type.DefineMethod("get_Identifier", MethodAttributes.Public | MethodAttributes.SpecialName
            | MethodAttributes.HideBySig, typeof(Guid), Type.EmptyTypes);
        var getterIl = getter.GetILGenerator();
        getterIl.Emit(OpCodes.Ldstr, BoundaryIdentifier.ToString());
        getterIl.Emit(OpCodes.Newobj, typeof(Guid).GetConstructor([typeof(string)])!);
        getterIl.Emit(OpCodes.Ret);
        property.SetGetMethod(getter);
        var integrationEvent = (IntegrationEventBase)Activator.CreateInstance(type.CreateType()!)!;
        typeof(IntegrationEventBase).GetProperty(nameof(IIntegrationEvent.EventId))!
            .SetValue(integrationEvent, Guid.CreateVersion7());
        typeof(IntegrationEventBase).GetProperty(nameof(IIntegrationEvent.TenantId))!
            .SetValue(integrationEvent, SchemaFixture.TenantA);
        typeof(IntegrationEventBase).GetProperty(nameof(IIntegrationEvent.OccurredAt))!
            .SetValue(integrationEvent, Occurred);
        return integrationEvent;
    }

    private sealed class CountingEventBus : IEventBus
    {
        public int Calls { get; private set; }

        public Task PublishAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed record OutboxRow(Guid Id, DateTimeOffset OccurredAt, Guid TenantId, Guid? OrganizationId,
        string CorrelationId, Guid? CausationId, Guid? ActorUserId, string Type, string Topic,
        string PartitionKey, string Payload, bool MetadataIsSqlNull, DateTimeOffset? ProcessedAt,
        int Attempts, string? LastError);
    private sealed record AuditRow(string Operation, string OperationClass, string Outcome, string? EntityId);
    private sealed record ProvisioningProbe(TenantId ProvisioningTenantId) : IProvisionsTenant;
    private sealed class DirectHandler;
    private sealed class OffHandler;

    [IntegrationEventPartition(nameof(SubjectId))]
    private sealed record ProbeIntegrationEventV1 : IntegrationEventBase
    {
        public required Guid SubjectId { get; init; }
        public override string Topic => "learnstack.customization.contenttype";
        public override string PartitionKey => SubjectId.ToString();
    }

    [IntegrationEventPartition(nameof(SubjectId))]
    private sealed record OrganizationProbeIntegrationEventV1 : IntegrationEventBase, IOrganizationScopedIntegrationEvent
    {
        public required Guid SubjectId { get; init; }
        public override string Topic => "learnstack.customization.contenttype";
        public override string PartitionKey => SubjectId.ToString();
    }

    private sealed class ActingContext(OrganizationId? organizationId = null) : ITenantContext
    {
        public bool IsResolved => true;
        public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;
        public TenantId TenantId => TenantId.From(SchemaFixture.TenantA);
        public OrganizationId? OrganizationId { get; set; } = organizationId;
        public UserId? UserId { get; set; } = SharedKernel.Identifiers.UserId.From(HumanActor);
        public UserId? CausalActorUserId { get; init; }
        public string? CorrelationId => AmbientCorrelation;
        public string? ModuleName => "customization";
    }

    private sealed class ProbeCatalog(IAuditCatalog actual) : IAuditCatalog
    {
        public bool TryGet(Type requestType, out AuditRegistration registration)
        {
            if (requestType == typeof(DirectHandler))
            {
                registration = new AuditRegistration(false,
                    [new AuditCatalogEntry("customization", DirectOperation, OperationType.Create,
                        OperationClass.Must, typeof(TenantContentType))]);
                return true;
            }
            if (requestType == typeof(OffHandler))
            {
                registration = new AuditRegistration(true, []);
                return true;
            }
            return actual.TryGet(requestType, out registration);
        }
        public bool TryGetOffPath(string operation, out AuditCatalogEntry entry) => actual.TryGetOffPath(operation, out entry);
        public IReadOnlyCollection<AuditCatalogEntry> All => actual.All;
    }
}
