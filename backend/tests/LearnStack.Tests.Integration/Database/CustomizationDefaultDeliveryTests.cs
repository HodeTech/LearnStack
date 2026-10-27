using FluentAssertions;
using System.Collections.Concurrent;
using LearnStack.Application.Authorization;
using LearnStack.Api.Authentication;
using LearnStack.Api.Messaging;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Modules.Tenancy.Application.Contracts.IntegrationEvents;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Customization.Application.Authorization;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using System.Text.Json;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Production TenantCreated delivery and Seeder converge on the same tenant defaults.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class CustomizationDefaultDeliveryTests(SchemaFixture schema)
{
    private const string ContentSubscription = "customization.initialize-content-type-default.v1";
    private const string TaxonomySubscription = "customization.initialize-taxonomy-default.v1";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);

    [Fact]
    public async Task Both_real_initializers_capture_only_their_validated_delivery_and_revoke_evidence_after_completion()
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        await fixture.ProvisionAsync();
        fixture.StartApi(observeTrustedOperations: true);
        await fixture.WaitForBothDeliveriesAsync();
        await fixture.AssertCreatedDefaultsAsync();
        fixture.AssertTrustedOperations();
        await using var scope = fixture.ApiServices.CreateAsyncScope();
        var fabricated = new SeedIdentityTenantContext(SeedData.English.TenantId, null);
        scope.ServiceProvider.GetServices<ITrustedOperationSource>()
            .Select(source => source.Capture(typeof(InitializeTenantContentTypeDefaultCommand), fabricated))
            .Should().OnlyContain(descriptor => descriptor == null,
                "constructing an Ambient SystemActor context outside the delivery grants no initializer authority");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stored_backlog_and_fresh_provisioning_reach_both_real_subscribers(bool fresh)
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        if (!fresh) await fixture.ProvisionAsync();
        fixture.StartApi();
        fixture.AssertProductionSubscriptions();
        if (fresh) await fixture.ProvisionAsync(fixture.ApiServices);

        await fixture.WaitForBothDeliveriesAsync();
        await fixture.AssertCreatedDefaultsAsync();
        (await fixture.CountAsync("outbox_messages", "processed_at IS NOT NULL")).Should().Be(1);
        (await fixture.CountAsync("event_deliveries", "processed_at IS NOT NULL")).Should().Be(2);
        (await fixture.CountAsync("inbox_messages", "recorded_at IS NOT NULL")).Should().Be(2);
    }

    [Fact]
    public async Task A_failed_MUST_audit_rolls_back_both_business_effects_and_inbox_completion()
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        await fixture.ProvisionAsync();
        await fixture.AlterSchemaAsync("""
            ALTER TABLE audit_log ADD CONSTRAINT refuse_default_delivery_audit
            CHECK (operation NOT IN ('customization.content_type.initialize',
                                     'customization.level_taxonomy.initialize'))
            """);
        fixture.StartApi();
        await Fixture.WaitUntilAsync(async () =>
            await fixture.CountAsync("event_deliveries", "last_error IS NOT NULL") == 2);

        (await fixture.CountAsync("tenant_content_types", "key = 'card'")).Should().Be(0);
        (await fixture.CountAsync("tenant_level_taxonomies", "key = 'plain'")).Should().Be(0);
        (await fixture.CountAsync("inbox_messages", "recorded_at IS NOT NULL")).Should().Be(0);
        (await fixture.CountAsync("event_deliveries", "processed_at IS NOT NULL")).Should().Be(0);
        (await fixture.GenerationAsync()).Should().Be(0);

        await fixture.AlterSchemaAsync("ALTER TABLE audit_log DROP CONSTRAINT refuse_default_delivery_audit");
        await fixture.MakeFailedDeliveriesDueAsync();
        await fixture.WaitForBothDeliveriesAsync();
        await fixture.AssertCreatedDefaultsAsync();
    }

    [Fact]
    public async Task Reaccepting_the_exact_provisioning_fact_does_not_repeat_inbox_effect_or_audit()
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        await fixture.ProvisionAsync();
        fixture.StartApi();
        await fixture.WaitForBothDeliveriesAsync();
        var before = await fixture.DefaultStateAsync();

        await fixture.RepublishOriginalAsync();

        (await fixture.DefaultStateAsync()).Should().Be(before);
        (await fixture.CountAsync("event_deliveries", "processed_at IS NOT NULL")).Should().Be(2);
        (await fixture.CountAsync("inbox_messages", "recorded_at IS NOT NULL")).Should().Be(2);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Real_Seeder_and_real_subscriber_serialize_on_each_builtin_concept(bool taxonomy, bool consumerFirst)
    {
        await using var fixture = await Fixture.CreateAsync(schema);
        await fixture.ProvisionAsync();
        await fixture.InstallDefinitionInsertGateAsync(taxonomy);
        Task<int>? seed = null;
        try
        {
            if (consumerFirst)
            {
                fixture.StartApi();
                await fixture.WaitForGateWinnerAsync();
                seed = fixture.RunSeederAsync();
            }
            else
            {
                seed = fixture.RunSeederAsync();
                await fixture.WaitForGateWinnerAsync();
                fixture.StartApi();
            }

            await fixture.WaitForConceptLoserAsync();
            (await fixture.CountAsync(taxonomy ? "tenant_level_taxonomies" : "tenant_content_types",
                taxonomy ? "key = 'plain'" : "key = 'card'")).Should().Be(0,
                "the winning insert is still inside its uncommitted transaction");
        }
        finally
        {
            await fixture.ReleaseDefinitionInsertGateAsync();
            if (seed is not null) await seed.WaitAsync(Deadline);
        }

        seed.Should().NotBeNull();
        (await seed!).Should().Be(0);
        await fixture.WaitForBothDeliveriesAsync();
        await fixture.AssertCreatedDefaultsAsync();
        (await fixture.CountAsync("tenant_content_types", "key = 'card'")).Should().Be(1);
        (await fixture.CountAsync("audit_log", "operation = 'customization.content_type.initialize' AND outcome = 'success' AND operation_type = 'Create' AND after_state IS NOT NULL")).Should().Be(1,
            "the first writer creates exactly one root; a preserved attempt may have a separate classified audit");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DisposableSchemaDatabase _database;
        private readonly NpgsqlDataSource _seedSource;
        private readonly SeedTenant _tenant;
        private ApiFactory? _api;
        private NpgsqlConnection? _gateOwner;
        private long _gateKey;
        private int _gateOwnerPid;
        private string _gateTable = "";
        private CancellationTokenSource? _seedCancellation;
        private Task<int>? _seedTask;
        private readonly TrustedObservation _trusted = new();

        private Fixture(DisposableSchemaDatabase database)
        {
            _database = database;
            _seedSource = NpgsqlDataSource.Create(database.AppConnectionString);
            _tenant = SeedData.English with { Curriculum = null };
        }

        public IServiceProvider ApiServices => _api?.Services ?? throw new InvalidOperationException("API has not started.");

        public static async Task<Fixture> CreateAsync(SchemaFixture schema) =>
            new(await DisposableSchemaDatabase.CreateAsync(schema.Postgres));

        public void StartApi(bool observeTrustedOperations = false)
        {
            _api = new ApiFactory(_database, observeTrustedOperations ? _trusted : null);
            _ = _api.Services;
        }

        public void AssertTrustedOperations()
        {
            var observations = _trusted.Items.ToArray();
            observations.Select(item => item.RequestType).Should().BeEquivalentTo(
                [typeof(InitializeTenantContentTypeDefaultCommand), typeof(InitializeTenantLevelTaxonomyDefaultCommand)]);
            observations.Should().OnlyContain(item => item.WasActive && item.WrongRequestRefused && item.ForeignContextRefused
                && item.Descriptor.SourceKind == AuthorizationSourceKind.IntegrationEvent && !item.Descriptor.IsActive);
        }

        public void AssertProductionSubscriptions()
        {
            var registry = ApiServices.GetRequiredService<OutboxEventRegistry>();
            var resolved = registry.Resolve(typeof(LearnStack.Modules.Tenancy.Application.Contracts.IntegrationEvents.TenantCreatedIntegrationEventV1).AssemblyQualifiedName);
            resolved.Subscriptions.Select(subscription => (subscription.Id, subscription.Readiness, subscription.HandlerType))
                .Should().BeEquivalentTo(new (string, OutboxSubscriptionReadiness, Type)[] {
                    (ContentSubscription, OutboxSubscriptionReadiness.Active,
                        typeof(LearnStack.Modules.Customization.Application.IntegrationEvents.InitializeContentTypeDefaultHandler)),
                    (TaxonomySubscription, OutboxSubscriptionReadiness.Active,
                        typeof(LearnStack.Modules.Customization.Application.IntegrationEvents.InitializeTaxonomyDefaultHandler)),
                });
            ApiServices.GetServices<IHostedService>().OfType<OutboxProcessor>().Should().ContainSingle();
            ApiServices.GetServices<IHostedService>().OfType<DeliveryProcessor>().Should().ContainSingle();
        }

        public async Task ProvisionAsync(IServiceProvider? provider = null)
        {
            if (provider is null)
            {
                await using var seed = SeedComposition.Build(_seedSource, null, NullLoggerFactory.Instance);
                await ProvisionInAsync(seed);
            }
            else await ProvisionInAsync(provider);
        }

        private async Task ProvisionInAsync(IServiceProvider provider)
        {
            await using var scope = provider.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ProvisionTenantCommand(
                _tenant.TenantId, _tenant.Slug, _tenant.DisplayName,
                _tenant.DefaultOrganization.OrganizationId, _tenant.DefaultOrganization.Slug,
                _tenant.DefaultOrganization.DisplayName));
            result.IsSuccess.Should().BeTrue();
        }

        public Task<int> RunSeederAsync()
        {
            _seedCancellation = new CancellationTokenSource(Deadline);
            _seedTask = new SeedRunner(
                context => SeedComposition.Build(_seedSource, context, NullLoggerFactory.Instance),
                NullLogger<SeedRunner>.Instance).RunAsync(_seedCancellation.Token, [_tenant]);
            return _seedTask;
        }

        public async Task RepublishOriginalAsync()
        {
            IntegrationEventEnvelope envelope;
            await using (var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString))
            {
                await SchemaQueries.AssertApplicationRoleAsync(connection);
                await using var transaction = await connection.BeginTransactionAsync();
                await SchemaQueries.SetTenantAsync(connection, transaction, _tenant.TenantId.Value);
                await using var query = new NpgsqlCommand("""
                    SELECT payload::text, correlation_id, organization_id, causation_id, actor_user_id
                    FROM outbox_messages WHERE tenant_id = @tenant
                    """, connection, transaction);
                query.Parameters.AddWithValue("tenant", _tenant.TenantId.Value);
                await using var row = await query.ExecuteReaderAsync();
                (await row.ReadAsync()).Should().BeTrue();
                var fact = JsonSerializer.Deserialize<TenantCreatedIntegrationEventV1>(
                    row.GetString(0), IntegrationEventBase.PayloadJsonOptions)!;
                envelope = new IntegrationEventEnvelope(fact, row.GetString(1),
                    row.IsDBNull(2) ? null : row.GetGuid(2),
                    row.IsDBNull(3) ? null : row.GetGuid(3),
                    row.IsDBNull(4) ? null : UserId.From(row.GetGuid(4)));
                (await row.ReadAsync()).Should().BeFalse();
            }
            await using var scope = ApiServices.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IEventBus>().PublishAsync(envelope);
        }

        public async Task<(Guid Content, Guid Taxonomy, long Generation, long Audits, long Inbox)> DefaultStateAsync() =>
            (await DefinitionIdAsync("tenant_content_types", "card"),
             await DefinitionIdAsync("tenant_level_taxonomies", "plain"),
             await GenerationAsync(),
             await CountAsync("audit_log", "operation IN ('customization.content_type.initialize', 'customization.level_taxonomy.initialize')"),
             await CountAsync("inbox_messages", $"subscription_id IN ('{ContentSubscription}', '{TaxonomySubscription}')"));

        private async Task<Guid> DefinitionIdAsync(string table, string key)
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
            await SchemaQueries.AssertApplicationRoleAsync(connection);
            await using var transaction = await connection.BeginTransactionAsync();
            await SchemaQueries.SetTenantAsync(connection, transaction, _tenant.TenantId.Value);
            await using var query = new NpgsqlCommand($"SELECT id FROM {table} WHERE key = @key", connection, transaction);
            query.Parameters.AddWithValue("key", key);
            return (Guid)(await query.ExecuteScalarAsync())!;
        }

        public async Task WaitForBothDeliveriesAsync() =>
            await WaitUntilAsync(async () => await CountAsync("event_deliveries", "processed_at IS NOT NULL") == 2
                && await CountAsync("inbox_messages", "recorded_at IS NOT NULL") == 2);

        public async Task AssertCreatedDefaultsAsync()
        {
            (await CountAsync("tenant_content_types", "key = 'card' AND status = 'Active' AND deleted_at IS NULL"))
                .Should().Be(1);
            (await CountAsync("tenant_level_taxonomies", "key = 'plain' AND status = 'Active' AND deleted_at IS NULL"))
                .Should().Be(1);
            (await CountAsync("tenant_level_taxonomy_items", "taxonomy_key = 'plain' AND schema_version = 1")).Should().Be(3);
            (await GenerationAsync()).Should().Be(2, "each create-only initializer advances its own definition generation once");
            (await CountAsync("audit_log", "operation = 'customization.content_type.initialize' AND operation_class = 'Must' AND outcome = 'success' AND operation_type = 'Create' AND after_state IS NOT NULL"))
                .Should().Be(1);
            (await CountAsync("audit_log", "operation = 'customization.level_taxonomy.initialize' AND operation_class = 'Must' AND outcome = 'success' AND operation_type = 'Create' AND after_state IS NOT NULL"))
                .Should().Be(1);
            (await CountAsync("inbox_messages inbox JOIN outbox_messages source ON source.id = inbox.event_id AND source.tenant_id = inbox.tenant_id",
                $"inbox.subscription_id IN ('{ContentSubscription}', '{TaxonomySubscription}') AND inbox.recorded_at IS NOT NULL"))
                .Should().Be(2);
        }

        public async Task<long> CountAsync(string table, string predicate)
        {
            // Closed, test-owned identifiers and predicates only; the first transaction
            // statement announces the tenant and PostgreSQL RLS scopes this read.
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
            await SchemaQueries.AssertApplicationRoleAsync(connection);
            await using var transaction = await connection.BeginTransactionAsync();
            await SchemaQueries.SetTenantAsync(connection, transaction, _tenant.TenantId.Value);
            await using var query = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE {predicate}", connection, transaction);
            return (long)(await query.ExecuteScalarAsync())!;
        }

        public async Task<long> GenerationAsync()
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
            await SchemaQueries.AssertApplicationRoleAsync(connection);
            await using var transaction = await connection.BeginTransactionAsync();
            await SchemaQueries.SetTenantAsync(connection, transaction, _tenant.TenantId.Value);
            await using var query = new NpgsqlCommand("SELECT COALESCE((SELECT generation FROM customization_generations), 0)", connection, transaction);
            return (long)(await query.ExecuteScalarAsync())!;
        }

        public async Task AlterSchemaAsync(string sql)
        {
            await using var owner = await PostgresFixture.OpenAsync(_database.MigrationConnectionString);
            await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)owner);
            await command.ExecuteNonQueryAsync();
        }

        public async Task MakeFailedDeliveriesDueAsync()
        {
            await using var dispatcher = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.OutboxConnectionString);
            await using var command = new NpgsqlCommand("""
                UPDATE event_deliveries SET available_after = clock_timestamp()
                WHERE processed_at IS NULL AND last_error IS NOT NULL
                """, dispatcher);
            await command.ExecuteNonQueryAsync();
        }

        public async Task InstallDefinitionInsertGateAsync(bool taxonomy)
        {
            _gateKey = Random.Shared.NextInt64(1, long.MaxValue);
            _gateTable = taxonomy ? "tenant_level_taxonomies" : "tenant_content_types";
            var conceptKey = taxonomy ? "plain" : "card";
            _gateOwner = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.MigrationConnectionString);
            await using (var setup = new NpgsqlCommand($"""
                CREATE FUNCTION default_definition_insert_gate() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.tenant_id = '{_tenant.TenantId.Value:D}'::uuid AND NEW.key = '{conceptKey}' THEN
                        PERFORM pg_advisory_xact_lock({_gateKey});
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER default_definition_insert_gate BEFORE INSERT ON {_gateTable}
                FOR EACH ROW EXECUTE FUNCTION default_definition_insert_gate();
                SELECT pg_advisory_lock({_gateKey});
                """, _gateOwner))
                await setup.ExecuteNonQueryAsync();
            await using var pid = new NpgsqlCommand("SELECT pg_backend_pid()", _gateOwner);
            _gateOwnerPid = (int)(await pid.ExecuteScalarAsync())!;
        }

        public async Task WaitForGateWinnerAsync() => await WaitUntilAsync(async () =>
            await BlockedByAsync(_gateOwnerPid) == 1);

        public async Task WaitForConceptLoserAsync() => await WaitUntilAsync(async () =>
        {
            await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
            await SchemaQueries.AssertApplicationRoleAsync(observer);
            await using var query = new NpgsqlCommand("""
                SELECT count(*) FROM pg_locks waiting
                JOIN pg_locks held ON held.locktype = 'advisory' AND held.granted
                  AND held.database = waiting.database
                  AND held.classid = waiting.classid AND held.objid = waiting.objid
                  AND held.objsubid = waiting.objsubid AND held.pid <> waiting.pid
                JOIN pg_stat_activity loser ON loser.pid = waiting.pid
                JOIN pg_stat_activity winner ON winner.pid = held.pid
                WHERE waiting.locktype = 'advisory' AND NOT waiting.granted
                  AND loser.datname = current_database() AND winner.datname = current_database()
                  AND loser.usename = 'learnstack_app' AND winner.usename = 'learnstack_app'
                  AND loser.wait_event_type = 'Lock' AND winner.wait_event_type = 'Lock'
                  AND @gate = ANY(pg_blocking_pids(winner.pid))
                """, observer);
            query.Parameters.AddWithValue("gate", _gateOwnerPid);
            return (long)(await query.ExecuteScalarAsync())! == 1;
        });

        private async Task<long> BlockedByAsync(int blockerPid)
        {
            await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
            await SchemaQueries.AssertApplicationRoleAsync(observer);
            await using var query = new NpgsqlCommand("""
                SELECT count(*) FROM pg_stat_activity
                WHERE datname = current_database() AND usename = 'learnstack_app'
                  AND wait_event_type = 'Lock' AND @blocker = ANY(pg_blocking_pids(pid))
                  AND query LIKE @table
                """, observer);
            query.Parameters.AddWithValue("blocker", blockerPid);
            query.Parameters.AddWithValue("table", $"%{_gateTable}%");
            return (long)(await query.ExecuteScalarAsync())!;
        }

        public async Task ReleaseDefinitionInsertGateAsync()
        {
            if (_gateOwner is null) return;
            await using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({_gateKey})", _gateOwner);
            ((bool)(await release.ExecuteScalarAsync())!).Should().BeTrue();
            await _gateOwner.DisposeAsync();
            _gateOwner = null;
        }

        public static async Task WaitUntilAsync(Func<Task<bool>> condition)
        {
            using var deadline = new CancellationTokenSource(Deadline);
            while (!await condition()) await Task.Delay(20, deadline.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await ReleaseDefinitionInsertGateAsync();
            _seedCancellation?.Cancel();
            if (_seedTask is { IsCompleted: false })
            {
                try { await _seedTask.WaitAsync(Deadline); }
                catch (OperationCanceledException) { }
            }
            if (_api is not null)
            {
                foreach (var worker in _api.Services.GetServices<IHostedService>()
                    .Where(service => service is OutboxProcessor or DeliveryProcessor))
                    await worker.StopAsync(CancellationToken.None).WaitAsync(Deadline);
                await _api.DisposeAsync();
            }
            _seedCancellation?.Dispose();
            await _seedSource.DisposeAsync();
            await _database.DisposeAsync();
        }

        private sealed class ApiFactory(DisposableSchemaDatabase database, TrustedObservation? trusted = null) : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment(Environments.Development);
                builder.UseSetting("ConnectionStrings:Default", database.AppConnectionString);
                builder.UseSetting("ConnectionStrings:PlatformAdmin", database.PlatformConnectionString);
                builder.UseSetting("ConnectionStrings:OutboxDispatcher", database.OutboxConnectionString);
                builder.ConfigureTestServices(services =>
                {
                    if (trusted is not null)
                    {
                        services.TryAddSingleton<IOperationPolicyCatalog>(_ => new OperationPolicyCatalog([new CustomizationOperationPolicySource()]));
                        services.AddTrustedDeliveryOperations();
                        services.AddSingleton(trusted);
                        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ObserveTrustedOperation<,>));
                    }
                    // This fixture owns real default delivery and provisioning, but no Identity authority.
                    foreach (var descriptor in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                                 && (descriptor.ImplementationType ?? descriptor.ImplementationFactory?.Method.ReturnType)
                                 == typeof(TenantIdentityAuthorityStartupService)).ToArray())
                        services.Remove(descriptor);
                });
            }
        }
    }

    private sealed class TrustedObservation
    {
        internal ConcurrentQueue<TrustedObservationItem> Items { get; } = new();
    }

    private sealed record TrustedObservationItem(Type RequestType, TrustedOperationDescriptor Descriptor,
        bool WasActive, bool WrongRequestRefused, bool ForeignContextRefused);

    private sealed class ObserveTrustedOperation<TRequest, TResponse>(IEnumerable<ITrustedOperationSource> sources,
        ITenantContext context, TrustedObservation observations) : IPipelineBehavior<TRequest, TResponse>
        where TRequest : notnull where TResponse : IResultBase
    {
        public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            if (typeof(TRequest) == typeof(InitializeTenantContentTypeDefaultCommand)
                || typeof(TRequest) == typeof(InitializeTenantLevelTaxonomyDefaultCommand))
            {
                var owners = sources.ToArray();
                var descriptor = owners.Select(source => source.Capture(typeof(TRequest), context))
                    .Where(value => value is not null).Cast<TrustedOperationDescriptor>().Should().ContainSingle().Subject;
                var other = typeof(TRequest) == typeof(InitializeTenantContentTypeDefaultCommand)
                    ? typeof(InitializeTenantLevelTaxonomyDefaultCommand) : typeof(InitializeTenantContentTypeDefaultCommand);
                var wrongRequestRefused = owners.All(source => source.Capture(other, context) is null)
                    && owners.All(source => source.Capture(typeof(ProvisionTenantCommand), context) is null);
                var fabricated = new SeedIdentityTenantContext(context.TenantId, context.OrganizationId);
                observations.Items.Enqueue(new(typeof(TRequest), descriptor, descriptor.IsActive,
                    wrongRequestRefused, owners.All(source => source.Capture(typeof(TRequest), fabricated) is null)));
            }
            return next();
        }
    }
}
