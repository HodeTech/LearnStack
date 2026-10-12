using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Application.Audit;
using LearnStack.Application.Messaging;
using LearnStack.Application.Persistence;
using LearnStack.Modules.Customization.Domain;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Contracts.IntegrationEvents;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0057's shared execution boundary, including real outbox receipts across
/// direct and nested invocations. Domain save-promotion proofs live in
/// <c>DomainEventProductionTests</c>.
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class ProducerExecutionTests(SchemaFixture schema)
{
    private const string DirectOperation = "customization.content_type.direct_probe";
    private const string Correlation = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
    private static readonly Error Refusal = new(new LocalizedMessage("lockey_business_rule_violation"));
    private static readonly ProbeContext TenantA = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_leaked_frame_refuses_completion_with_a_failed_not_indeterminate_audit(bool joiner)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await SeedTenantAsync(database.AppConnectionString);
        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var provider = Compose(dataSource);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var coordinator = services.GetRequiredService<TransactionExecutionCoordinator>();
        var unit = services.GetRequiredService<IUnitOfWork>();
        var capture = services.GetRequiredService<IAuditStateCapture>();
        var id = TenantContentTypeId.From(Guid.CreateVersion7());
        var integrationEvent = new TenantCreatedIntegrationEventV1
        {
            EventId = Guid.CreateVersion7(),
            OccurredAt = services.GetRequiredService<IClock>().UtcNow,
            TenantId = TenantA.TenantId.Value,
        };
        IUnitOfWorkScope? leaked = null;
        var observed = false;

        async Task<Result<None>> LeakAsync()
        {
            leaked = await unit.BeginTransactionAsync();
            return Result.Ok(None.Value);
        }

        var run = () => DirectAudit(services).ExecuteAsync(typeof(DirectHandler), null,
            () => coordinator.ExecuteAsync(null, TransactionMode.ReadWrite, "leaked-frame", async () =>
            {
                await SchemaQueries.AssertApplicationRoleAsync(unit.Connection);
                await WriteContentTypeAsync(services, id);
                await services.GetRequiredService<IOutbox>().EnqueueAsync(integrationEvent);
                (await CountInUnitAsync(unit, "tenant_content_types", id.Value)).Should().Be(1);
                (await CountInUnitAsync(unit, "outbox_messages", integrationEvent.EventId)).Should().Be(1);
                observed = true;
                if (!joiner) return await LeakAsync();

                var nested = () => coordinator.ExecuteAsync(
                    null, TransactionMode.ReadWrite, "leaking-joiner", LeakAsync);
                await nested.Should().ThrowAsync<InvalidOperationException>().WithMessage("*innermost-first*");
                unit.IsRollbackOnly.Should().BeTrue("absorbing the joiner's exception cannot save its transaction");
                unit.HasActiveTransaction.Should().BeFalse("the leaked stack collapses before the owner resumes");
                return Result.Ok(None.Value);
            }), CancellationToken.None);

        try
        {
            await run.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage(joiner ? "*rollback-only*" : "*innermost-first*");
            observed.Should().BeTrue("the refusal must roll back an actual saved row");
            unit.IsRollbackOnly.Should().BeTrue();
            AssertClean(services);
            capture.State.Should().Be(AuditIntentState.None, "the audit owner reconciled and cleared its frame");
            (await CountAsync(database.AppConnectionString, "tenant_content_types", id.Value)).Should().Be(0);
            (await CountAsync(database.AppConnectionString, "outbox_messages", integrationEvent.EventId)).Should().Be(0);
            var row = (await AuditRowsAsync(database.AppConnectionString)).Should().ContainSingle().Subject;
            row.Operation.Should().Be(DirectOperation);
            row.Outcome.Should().Be("failed", "frame bookkeeping refused before any physical COMMIT");
            row.After.Should().Contain(Key(id), "the failed row retains the positively observed attempted write");
            var reuse = () => unit.BeginTransactionAsync();
            await reuse.Should().ThrowAsync<InvalidOperationException>().WithMessage("*rollback-only*");
        }
        finally
        {
            if (leaked is not null) await leaked.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task An_absorbed_producer_refusal_rolls_back_direct_and_descendant_work(int descendantDepth)
    {
        await using var dataSource = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var provider = Compose(dataSource);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var transaction = services.GetRequiredService<TransactionExecutionCoordinator>();
        var producer = services.GetRequiredService<ProducerExecution>();
        var unit = services.GetRequiredService<IUnitOfWork>();
        var id = TenantContentTypeId.From(Guid.CreateVersion7());
        var integrationEvent = new TenantCreatedIntegrationEventV1
        {
            EventId = Guid.CreateVersion7(),
            OccurredAt = services.GetRequiredService<IClock>().UtcNow,
            TenantId = TenantA.TenantId.Value,
        };
        var observedWrite = false;
        var absorbedRefusal = false;

        async Task<Result<None>> WriteAsync()
        {
            await WriteContentTypeAsync(services, id);
            await services.GetRequiredService<IOutbox>().EnqueueAsync(integrationEvent);
            (await CountInUnitAsync(unit, "tenant_content_types", id.Value)).Should().Be(1);
            (await CountInUnitAsync(unit, "outbox_messages", integrationEvent.EventId)).Should().Be(1);
            observedWrite = true;
            return Result.Ok(None.Value);
        }

        var run = () => transaction.ExecuteAsync(null, TransactionMode.ReadWrite, "outer", async () =>
        {
            var refused = await transaction.ExecuteAsync(null, TransactionMode.ReadWrite, "refused", async () =>
            {
                async Task<Result<None>> DescendantAsync(int remainingDepth)
                {
                    if (remainingDepth == 0) return await WriteAsync();
                    var child = await transaction.ExecuteAsync(
                        null, TransactionMode.ReadWrite, "successful-descendant",
                        () => DescendantAsync(remainingDepth - 1));
                    child.IsSuccess.Should().BeTrue();
                    return child;
                }

                (await DescendantAsync(descendantDepth)).IsSuccess.Should().BeTrue();

                return Result<None>.Fail(Refusal);
            });

            refused.Error.Should().BeSameAs(Refusal);
            absorbedRefusal = true;
            unit.IsRollbackOnly.Should().BeTrue("the refusing invocation resolves after poisoning its unit");
            return Result.Ok(None.Value);
        });

        await run.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*rollback-only*");
        observedWrite.Should().BeTrue("zero rows after an earlier failure would prove no rollback");
        absorbedRefusal.Should().BeTrue("the owner actually absorbed the nested Result.Fail");
        (await CountAsync(schema.Postgres.AppConnectionString, "tenant_content_types", id.Value)).Should().Be(0);
        (await CountAsync(schema.Postgres.AppConnectionString, "outbox_messages", integrationEvent.EventId)).Should().Be(0);
        producer.HasInvocation.Should().BeFalse();
        unit.Transaction.Should().BeNull();
        unit.GetTenantContextAnnouncement(unit.Transaction).Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_raw_announced_transaction_and_its_joiner_cannot_authorize_production(bool join)
    {
        await using var dataSource = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var provider = Compose(dataSource);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var unit = services.GetRequiredService<IUnitOfWork>();
        var producer = services.GetRequiredService<ProducerExecution>();
        typeof(ProducerExecution).GetMethod("BeginInvocation",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .IsAssembly.Should().BeTrue("only the core coordinator can enroll producer ownership");
        await using var owner = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(TenantA);
        unit.GetTenantContextAnnouncement(unit.Transaction).Should().NotBeNull(
            "missing SQL authority would be a weaker reason to refuse production");
        producer.HasInvocation.Should().BeFalse();
        var reached = false;

        Task<Result<None>> WorkAsync()
        {
            reached = true;
            var produce = () => producer.RequireAnnouncement();
            produce.Should().Throw<InvalidOperationException>().WithMessage("*enrolled writable frame*");
            return Task.FromResult(Result.Ok(None.Value));
        }

        if (join)
        {
            (await services.GetRequiredService<TransactionExecutionCoordinator>().ExecuteAsync(
                null, TransactionMode.ReadWrite, "orphan-joiner", WorkAsync)).IsSuccess.Should().BeTrue();
        }
        else
        {
            (await WorkAsync()).IsSuccess.Should().BeTrue();
        }

        reached.Should().BeTrue();
        producer.HasInvocation.Should().BeFalse();
        unit.HasActiveTransaction.Should().BeTrue("only the raw owner resolves its transaction");
    }

    [Fact]
    public async Task A_stale_owner_cannot_confer_production_authority_on_a_new_transaction_joiner()
    {
        await using var dataSource = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var provider = Compose(dataSource);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var unit = services.GetRequiredService<IUnitOfWork>();
        var producer = services.GetRequiredService<ProducerExecution>();
        var coordinator = services.GetRequiredService<TransactionExecutionCoordinator>();
        var stop = new InvalidOperationException("Stop after stale ancestry proof");
        var reached = false;

        var run = () => DirectAudit(services).ExecuteAsync(typeof(OffHandler), null,
            () => coordinator.ExecuteAsync<Result<None>>(
            null, TransactionMode.ReadWrite, "retained-owner", async () =>
            {
                var previous = producer.RequireAnnouncement();
                await unit.CommitAsync();
                await using var later = await unit.BeginTransactionAsync();
                await unit.SetTenantContextAsync(TenantA);
                unit.GetTenantContextAnnouncement(unit.Transaction)!.Generation.Should()
                    .BeGreaterThan(previous.Generation);

                (await coordinator.ExecuteAsync(null, TransactionMode.ReadWrite, "later-joiner", () =>
                {
                    reached = true;
                    var produce = () => producer.RequireAnnouncement();
                    produce.Should().Throw<InvalidOperationException>()
                        .WithMessage("*enrolled writable frame*");
                    return Task.FromResult(Result.Ok(None.Value));
                })).IsSuccess.Should().BeTrue();

                throw stop;
            }), CancellationToken.None);

        (await run.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(stop);
        reached.Should().BeTrue();
        AssertClean(services);
    }

    [Fact]
    public async Task An_event_free_absorbed_refusal_can_commit_its_observed_mutation()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await SeedTenantAsync(database.AppConnectionString);
        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var provider = Compose(dataSource);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var transaction = services.GetRequiredService<TransactionExecutionCoordinator>();
        var unit = services.GetRequiredService<IUnitOfWork>();
        var id = TenantContentTypeId.From(Guid.CreateVersion7());

        var result = await transaction.ExecuteAsync(null, TransactionMode.ReadWrite, "outer-control", async () =>
        {
            var refused = await transaction.ExecuteAsync(null, TransactionMode.ReadWrite, "event-free", async () =>
            {
                await WriteContentTypeAsync(services, id);
                (await CountInUnitAsync(unit, "tenant_content_types", id.Value)).Should().Be(1);
                return Result<None>.Fail(Refusal);
            });
            refused.Error.Should().BeSameAs(Refusal);
            unit.IsRollbackOnly.Should().BeFalse("ADR-0040 still permits an event-free refusal to be absorbed");
            return Result.Ok(None.Value);
        });

        result.IsSuccess.Should().BeTrue();
        (await CountAsync(database.AppConnectionString, "tenant_content_types", id.Value)).Should().Be(1);
    }

    [Fact]
    public async Task A_direct_owner_constructs_its_handler_after_admission_and_writes_one_correct_MUST_row()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await SeedTenantAsync(database.AppConnectionString);
        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var provider = Compose(dataSource);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var capture = new ObservedCapture(services.GetRequiredService<IAuditStateCapture>());
        var audit = DirectAudit(services, capture);
        var id = TenantContentTypeId.From(Guid.CreateVersion7());
        var constructed = false;

        var result = await audit.ExecuteAsync(typeof(DirectHandler), null,
            () => services.GetRequiredService<TransactionExecutionCoordinator>().ExecuteAsync(
                null, TransactionMode.ReadWrite, nameof(DirectHandler), () =>
                {
                    constructed = true;
                    return new DirectHandler(services).HandleAsync(id);
                }), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        constructed.Should().BeTrue();
        capture.ClearCount.Should().Be(1);
        AssertClean(services);
        (await CountAsync(database.AppConnectionString, "tenant_content_types", id.Value)).Should().Be(1);

        var row = (await AuditRowsAsync(database.AppConnectionString)).Should().ContainSingle().Subject;
        row.Operation.Should().Be(DirectOperation);
        row.OperationClass.Should().Be("Must");
        row.Outcome.Should().Be("success");
        row.EntityType.Should().Be(nameof(TenantContentType));
        row.EntityId.Should().Be(id.Value.ToString());
        row.Actor.Should().Be(SchemaFixture.Actor);
        row.Correlation.Should().Be(Correlation);
        row.Before.Should().BeNull();
        using var after = JsonDocument.Parse(row.After!);
        after.RootElement.GetProperty("Key").GetString().Should().Be(Key(id));
    }

    [Fact]
    public async Task An_unregistered_direct_owner_is_refused_before_its_handler_factory_runs()
    {
        await using var dataSource = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var provider = Compose(dataSource);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var constructed = false;

        var result = await DirectAudit(services).ExecuteAsync(typeof(UnregisteredHandler), null,
            () => services.GetRequiredService<TransactionExecutionCoordinator>().ExecuteAsync(
                null, TransactionMode.ReadWrite, nameof(UnregisteredHandler), () =>
                {
                    constructed = true;
                    return Task.FromResult(Result.Ok(None.Value));
                }), CancellationToken.None);

        result.Error.Should().Be(AuditErrors.UnclassifiedOperation);
        constructed.Should().BeFalse();
        services.GetRequiredService<IUnitOfWork>().HasActiveTransaction.Should().BeFalse();
        AssertClean(services);
    }

    [Fact]
    public async Task An_Off_direct_owner_preserves_nested_command_intents_and_clears_only_after_commit()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await SeedTenantAsync(database.AppConnectionString);
        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var provider = Compose(dataSource);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var sharedCapture = services.GetRequiredService<IAuditStateCapture>();
        var capture = new ObservedCapture(sharedCapture);
        var id = OrganizationId.From(Guid.CreateVersion7());

        var result = await DirectAudit(services, capture).ExecuteAsync(typeof(OffHandler), null,
            () => services.GetRequiredService<TransactionExecutionCoordinator>().ExecuteAsync(
                null, TransactionMode.ReadWrite, nameof(OffHandler), async () =>
                {
                    var nested = await services.GetRequiredService<IMediator>().Send(
                        new CreateOrganizationCommand(id, "direct-branch", "Direct branch"));
                    nested.IsSuccess.Should().BeTrue("the nested command is admitted: {0}", nested.Error);
                    (await CountInUnitAsync(services.GetRequiredService<IUnitOfWork>(),
                        "organizations", id.Value)).Should().Be(1);
                    sharedCapture.Intents.Should().ContainSingle();
                    sharedCapture.Changes.Should().NotBeEmpty();
                    sharedCapture.HasOpenFrame.Should().BeTrue("the outer direct audit frame is still open");
                    sharedCapture.State.Should().Be(AuditIntentState.Pending,
                        "the nested command joined and cannot claim commit ownership");
                    capture.ClearCount.Should().Be(0);
                    return Result.Ok(None.Value);
                }), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        capture.ClearCount.Should().Be(1);
        AssertClean(services);
        var row = (await AuditRowsAsync(database.AppConnectionString)).Should().ContainSingle().Subject;
        row.Operation.Should().Be("tenancy.organization.create");
        row.EntityId.Should().Be(id.Value.ToString());
        row.Outcome.Should().Be("success");
        row.After.Should().Contain("Direct branch");
        (await CountAsync(database.AppConnectionString, "organizations", id.Value)).Should().Be(1);
    }

    [Fact]
    public async Task A_real_direct_MUST_audit_failure_rolls_back_a_positively_observed_business_write()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await SeedTenantAsync(database.AppConnectionString);
        await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        await using (var deny = new NpgsqlCommand(
            "ALTER TABLE audit_log ADD CONSTRAINT reject_direct_probe CHECK (operation <> 'customization.content_type.direct_probe')",
            owner))
        {
            await deny.ExecuteNonQueryAsync();
        }

        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var provider = Compose(dataSource);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var id = TenantContentTypeId.From(Guid.CreateVersion7());
        var observed = false;
        var run = () => DirectAudit(services).ExecuteAsync(typeof(DirectHandler), null,
            () => services.GetRequiredService<TransactionExecutionCoordinator>().ExecuteAsync(
                null, TransactionMode.ReadWrite, nameof(DirectHandler), async () =>
                {
                    var result = await new DirectHandler(services).HandleAsync(id);
                    (await CountInUnitAsync(services.GetRequiredService<IUnitOfWork>(),
                        "tenant_content_types", id.Value)).Should().Be(1);
                    observed = true;
                    return result;
                }), CancellationToken.None);

        var failure = (await run.Should().ThrowAsync<AuditWriteFailedException>()).Which;
        failure.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        observed.Should().BeTrue();
        (await CountAsync(database.AppConnectionString, "tenant_content_types", id.Value)).Should().Be(0);
        (await AuditRowsAsync(database.AppConnectionString)).Should().BeEmpty();
        AssertClean(services);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_compositions_reconcile_an_independent_scope_inside_another_audit_execution(bool api)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await SeedTenantAsync(database.AppConnectionString);
        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var seed = Compose(dataSource);
        await using var host = new ConstructionOnlyApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Default", database.AppConnectionString);
            builder.ConfigureServices(services => services.AddSingleton<ITenantContextAccessor>(
                new StaticTenantContextAccessor(TenantA)));
        });
        var root = api ? host.Services : seed;
        await using var outer = root.CreateAsyncScope();
        var outerServices = outer.ServiceProvider;
        var outerCapture = new ObservedCapture(outerServices.GetRequiredService<IAuditStateCapture>());
        var id = OrganizationId.From(Guid.CreateVersion7());

        var result = await DirectAudit(outerServices, outerCapture).ExecuteAsync(typeof(OffHandler), null, async () =>
        {
            await using var inner = root.CreateAsyncScope();
            var innerServices = inner.ServiceProvider;
            innerServices.GetRequiredService<IAuditStateCapture>()
                .Should().NotBeSameAs(outerServices.GetRequiredService<IAuditStateCapture>());
            innerServices.GetRequiredService<ProducerExecution>()
                .Should().NotBeSameAs(outerServices.GetRequiredService<ProducerExecution>());
            innerServices.GetRequiredService<AuditExecutionCoordinator>()
                .Should().NotBeSameAs(outerServices.GetRequiredService<AuditExecutionCoordinator>());
            innerServices.GetRequiredService<TransactionExecutionCoordinator>()
                .Should().NotBeSameAs(outerServices.GetRequiredService<TransactionExecutionCoordinator>());

            var nested = await innerServices.GetRequiredService<IMediator>().Send(
                new CreateOrganizationCommand(id, "independent-branch", "Independent branch"));
            nested.IsSuccess.Should().BeTrue("the independent command is admitted: {0}", nested.Error);
            AssertClean(innerServices);
            outerCapture.HasOpenFrame.Should().BeTrue();
            outerCapture.ClearCount.Should().Be(0);
            return Result.Ok(None.Value);
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        outerCapture.ClearCount.Should().Be(1);
        AssertClean(outerServices);
        var row = (await AuditRowsAsync(database.AppConnectionString)).Should().ContainSingle().Subject;
        row.Operation.Should().Be("tenancy.organization.create");
        row.EntityId.Should().Be(id.Value.ToString());
        row.Outcome.Should().Be("success");
    }

    [Theory]
    [InlineData("success")]
    [InlineData("refusal")]
    [InlineData("cancel")]
    [InlineData("fault")]
    public async Task A_no_listener_boundary_has_a_W3C_trace_and_cleans_every_exit(string exit)
    {
        await using var dataSource = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var provider = Compose(dataSource);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var parent = Activity.Current;
        Activity.Current = null;
        try
        {
            using var source = new ActivitySource("learnstack.producer.test.no-listener");
            using var control = source.StartActivity("control");
            control.Should().BeNull("this proof installs no telemetry listener");
            var reached = false;
            var fault = new InvalidOperationException("deliberate handler fault");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            long generation = 0;

            Task<Result<None>> WorkAsync()
            {
                reached = true;
                Activity.Current.Should().NotBeNull();
                var activity = Activity.Current!;
                activity.IdFormat.Should().Be(ActivityIdFormat.W3C);
                activity.Id.Should().MatchRegex("^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$");
                activity.TraceId.Should().NotBe(default(ActivityTraceId));
                generation = services.GetRequiredService<ProducerExecution>().RequireAnnouncement().Generation;
                return exit switch
                {
                    "refusal" => Task.FromResult(Result<None>.Fail(Refusal)),
                    "cancel" => Task.FromCanceled<Result<None>>(cancellation.Token),
                    "fault" => Task.FromException<Result<None>>(fault),
                    _ => Task.FromResult(Result.Ok(None.Value)),
                };
            }

            var run = () => DirectAudit(services).ExecuteAsync(typeof(OffHandler), null,
                () => services.GetRequiredService<TransactionExecutionCoordinator>().ExecuteAsync(
                    null, TransactionMode.ReadWrite, "trace-probe", WorkAsync), CancellationToken.None);

            if (exit == "fault")
            {
                (await run.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(fault);
            }
            else if (exit == "cancel")
            {
                (await run.Should().ThrowAsync<OperationCanceledException>()).Which.CancellationToken
                    .Should().Be(cancellation.Token);
            }
            else
            {
                (await run()).IsSuccess.Should().Be(exit == "success");
            }

            reached.Should().BeTrue();
            generation.Should().BeGreaterThan(0);
            Activity.Current.Should().BeNull();
            AssertClean(services);

            // Successful scopes may start another unit. Poisoned scopes deliberately
            // cannot: cleanup must preserve ADR-0040's irreversible poison contract.
            if (exit == "success")
            {
                await DirectAudit(services).ExecuteAsync(typeof(OffHandler), null,
                    () => services.GetRequiredService<TransactionExecutionCoordinator>().ExecuteAsync(
                        null, TransactionMode.ReadWrite, "same-scope", () =>
                        {
                            services.GetRequiredService<ProducerExecution>().RequireAnnouncement().Generation
                                .Should().BeGreaterThan(generation);
                            return Task.FromResult(Result.Ok(None.Value));
                        }), CancellationToken.None);
                AssertClean(services);
                Activity.Current.Should().BeNull();
            }
            else
            {
                services.GetRequiredService<IUnitOfWork>().IsRollbackOnly.Should().BeTrue();
            }
        }
        finally
        {
            Activity.Current = parent;
        }
    }

    private static ServiceProvider Compose(NpgsqlDataSource dataSource) =>
        SeedComposition.Build(dataSource, TenantA, NullLoggerFactory.Instance);

    private static AuditExecutionCoordinator DirectAudit(IServiceProvider services, IAuditStateCapture? capture = null) =>
        new(new ProbeCatalog(services.GetRequiredService<IAuditCatalog>()),
            services.GetRequiredService<IAuditConfigService>(),
            capture ?? services.GetRequiredService<IAuditStateCapture>(),
            services.GetRequiredService<IAuditStore>(),
            services.GetRequiredService<ITenantContext>(),
            services.GetRequiredService<IClock>(),
            services.GetRequiredService<IGuidFactory>(),
            NullLogger<AuditExecutionCoordinator>.Instance);

    private static void AssertClean(IServiceProvider services)
    {
        var capture = services.GetRequiredService<IAuditStateCapture>();
        capture.HasOpenFrame.Should().BeFalse();
        capture.Intents.Should().BeEmpty();
        capture.Changes.Should().BeEmpty();
        capture.State.Should().Be(AuditIntentState.None);
        services.GetRequiredService<ProducerExecution>().HasInvocation.Should().BeFalse();
        var unit = services.GetRequiredService<IUnitOfWork>();
        unit.Transaction.Should().BeNull();
        unit.GetTenantContextAnnouncement(unit.Transaction).Should().BeNull();
    }

    private static async Task WriteContentTypeAsync(IServiceProvider services, TenantContentTypeId id)
    {
        var db = services.GetRequiredService<CustomizationDbContext>();
        db.Add(TenantContentType.Create(id, TenantA.TenantId, Key(id), 1,
            LocalizedText.From(("en", "Execution probe")), """{"type":"object"}""", "default-card",
            services.GetRequiredService<IClock>(), UserId.From(SchemaFixture.Actor)));
        await db.SaveChangesAsync();
    }

    private static string Key(TenantContentTypeId id) => $"probe-{id.Value:N}"[..24];

    // Table names below are closed fixture constants, never request input.
    private static async Task<long> CountInUnitAsync(IUnitOfWork unit, string table, Guid id)
    {
        await using var command = unit.Connection.CreateCommand();
        command.Transaction = unit.Transaction;
        command.CommandText = $"SELECT count(*) FROM {table} WHERE id = @id";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "id";
        parameter.Value = id;
        command.Parameters.Add(parameter);
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

    private static async Task SeedTenantAsync(string connectionString)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO tenants (id, slug, display_name, status, created_at, created_by, row_version)
            VALUES (@id, 'execution-probe', 'Execution probe', 'Trial', now(), @actor, 0)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", SchemaFixture.TenantA);
        command.Parameters.AddWithValue("actor", SchemaFixture.Actor);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async Task<IReadOnlyList<AuditRow>> AuditRowsAsync(string connectionString)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
        await using var command = new NpgsqlCommand(
            """
            SELECT operation, operation_class, outcome, entity_type, entity_id,
                   actor_user_id, correlation_id, before_state::text, after_state::text
            FROM audit_log ORDER BY timestamp
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<AuditRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new AuditRow(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetGuid(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        return rows;
    }

    private sealed record AuditRow(string Operation, string OperationClass, string Outcome,
        string? EntityType, string? EntityId, Guid? Actor, string? Correlation, string? Before, string? After);

    private sealed class DirectHandler
    {
        private readonly IServiceProvider _services;

        public DirectHandler(IServiceProvider services)
        {
            _services = services;
            services.GetRequiredService<IUnitOfWork>().HasActiveTransaction.Should().BeTrue();
            services.GetRequiredService<IAuditStateCapture>().HasOpenFrame.Should().BeTrue();
            services.GetRequiredService<ProducerExecution>().RequireAnnouncement().TenantId.Should().Be(TenantA.TenantId);
        }

        public async Task<Result<None>> HandleAsync(TenantContentTypeId id)
        {
            await WriteContentTypeAsync(_services, id);
            return Result.Ok(None.Value);
        }
    }

    private sealed class OffHandler;
    private sealed class UnregisteredHandler;

    /// <summary>The real request catalogue remains intact; only controlled direct types are added.</summary>
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

    private sealed class ObservedCapture(IAuditStateCapture actual) : IAuditStateCapture
    {
        public int ClearCount { get; private set; }
        public IReadOnlyList<CapturedEntityChange> Changes => actual.Changes;
        public IReadOnlyList<AuditIntent> Intents => actual.Intents;
        public AuditIntentState State => actual.State;
        public bool HasOpenFrame => actual.HasOpenFrame;
        public void Add(CapturedEntityChange change) => actual.Add(change);
        public IReadOnlyList<CapturedEntityChange> ChangesOf(AuditIntent intent) => actual.ChangesOf(intent);
        public void OpenFrame() => actual.OpenFrame();
        public void CloseFrame(AuditIntentResult result) => actual.CloseFrame(result);
        public void DeclareIntent(AuditIntent intent) => actual.DeclareIntent(intent);
        public void MarkWrittenInTransaction() => actual.MarkWrittenInTransaction();
        public void MarkCommitted() => actual.MarkCommitted();
        public void MarkRolledBack() => actual.MarkRolledBack();
        public void MarkIndeterminate(Exception cause) => actual.MarkIndeterminate(cause);
        public void Clear()
        {
            ClearCount++;
            actual.Clear();
        }
    }

    private sealed class ProbeContext : ITenantContext
    {
        public bool IsResolved => true;
        public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;
        public TenantId TenantId => TenantId.From(SchemaFixture.TenantA);
        public OrganizationId? OrganizationId => null;
        public UserId? UserId => SharedKernel.Identifiers.UserId.From(SchemaFixture.Actor);
        public string? CorrelationId => Correlation;
        public string? ModuleName => "customization";
    }
}
