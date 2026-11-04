using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Domain;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.Modules.Customization.Infrastructure.Projections;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class CustomizationContentTypeQuotaTests(SchemaFixture schema, ConstructionOnlyApiFactory factory)
    : IClassFixture<ConstructionOnlyApiFactory>
{
    private ComponentAuthorizationEvidence _evidence = null!;

    [Fact]
    public async Task The_real_feature_resolver_uses_its_five_key_catalog_floor_beside_the_builtin_then_refuses_the_sixth()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var evidence = await ComponentAuthorizationEvidence.CreateAsync(database);
        _evidence = evidence;
        var tenant = await ProvisionAsync(source);
        await using var host = Host(database, new Plan());
        (await SendAsync(host.Services, tenant, new InitializeTenantContentTypeDefaultCommand())).IsSuccess.Should().BeTrue();
        foreach (var key in new[] { "first", "second", "third", "fourth", "fifth" })
            (await SendAsync(host.Services, tenant, Register(key))).IsSuccess.Should().BeTrue();

        var refused = await SendAsync(host.Services, tenant, Register("sixth"));

        refused.Error!.Code.Should().Be("limit_exceeded");
        var problem = ProblemDetailsFactory.For(refused.Error);
        problem.Status.Should().Be(403);
        problem.Type.Should().Be("https://errors.learnstack.dev/limit_exceeded");
        System.Text.Json.JsonSerializer.Serialize(problem.Extensions["errors"]).Should()
            .Contain("limits.max_custom_content_types").And.Contain("\"limit\":\"5\"");
        (await UsageAsync(source, tenant)).Should().Be((5L, 6L, 6L));
        (await ComponentAuthorizationEvidence.CountDenialsAsync(source, tenant, "tests.component.quota.")).Should().Be(1);
    }

    [Fact]
    public async Task The_registered_null_provider_keeps_its_accepted_unlimited_authoring_behavior()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var evidence = await ComponentAuthorizationEvidence.CreateAsync(database);
        _evidence = evidence;
        var tenant = await ProvisionAsync(source);
        await using var host = Host(database);
        foreach (var key in new[] { "first", "second", "third", "fourth", "fifth", "sixth" })
            (await SendAsync(host.Services, tenant, Register(key))).IsSuccess.Should().BeTrue();
        (await UsageAsync(source, tenant)).Should().Be((6L, 6L, 6L));
    }

    [Fact]
    public async Task Revisions_and_retired_history_share_one_slot_until_its_last_nondeleted_revision_is_deleted()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var evidence = await ComponentAuthorizationEvidence.CreateAsync(database);
        _evidence = evidence;
        var tenant = await ProvisionAsync(source);
        await using var host = Host(database, new Plan(1));
        var first = Register("first");
        var successor = Register("first", 2);
        (await SendAsync(host.Services, tenant, first)).IsSuccess.Should().BeTrue();
        (await SendAsync(host.Services, tenant, new PublishTenantContentTypeCommand(first.ContentTypeId))).IsSuccess.Should().BeTrue();
        (await SendAsync(host.Services, tenant, successor)).IsSuccess.Should().BeTrue();
        (await SendAsync(host.Services, tenant, new PublishTenantContentTypeCommand(successor.ContentTypeId))).IsSuccess.Should().BeTrue();
        (await UsageAsync(source, tenant)).CustomKeys.Should().Be(1);
        (await SendAsync(host.Services, tenant, Register("second"))).Error!.Code.Should().Be("limit_exceeded");

        await SoftDeleteAsync(source, tenant, successor.ContentTypeId);
        (await SendAsync(host.Services, tenant, Register("second"))).Error!.Code.Should().Be("limit_exceeded",
            "a retained nondeleted deprecated revision still consumes the concept's slot");
        await SoftDeleteAsync(source, tenant, first.ContentTypeId);
        (await UsageAsync(source, tenant)).CustomKeys.Should().Be(0);
        (await SendAsync(host.Services, tenant, Register("second"))).IsSuccess.Should().BeTrue();
        (await SendAsync(host.Services, tenant, Register("first", 3))).Error!.Code.Should().Be("limit_exceeded",
            "deleted-only history is not a free existing key when the slot has been used elsewhere");
        (await UsageAsync(source, tenant)).Should().Be((1L, 3L, 5L));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    public async Task The_numeric_or_feature_unlimited_contract_bypasses_the_ceiling(long limit, bool unlimited)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var evidence = await ComponentAuthorizationEvidence.CreateAsync(database);
        _evidence = evidence;
        var tenant = await ProvisionAsync(source);
        await using var host = Host(database, new Plan(limit, unlimited));
        foreach (var key in new[] { "first", "second", "third" })
            (await SendAsync(host.Services, tenant, Register(key))).IsSuccess.Should().BeTrue();
        (await UsageAsync(source, tenant)).Should().Be((3L, 3L, 3L));
    }

    [Fact]
    public async Task Zero_allows_the_builtin_and_existing_revisions_but_no_new_custom_key()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var evidence = await ComponentAuthorizationEvidence.CreateAsync(database);
        _evidence = evidence;
        var tenant = await ProvisionAsync(source);
        await using (var allowed = Host(database, new Plan(1)))
            (await SendAsync(allowed.Services, tenant, Register("existing"))).IsSuccess.Should().BeTrue();
        await using var host = Host(database, new Plan(0));
        (await SendAsync(host.Services, tenant, new InitializeTenantContentTypeDefaultCommand())).IsSuccess.Should().BeTrue();
        (await SendAsync(host.Services, tenant, Register("card", 2))).IsSuccess.Should().BeTrue();
        (await SendAsync(host.Services, tenant, Register("existing", 2))).IsSuccess.Should().BeTrue();
        (await SendAsync(host.Services, tenant, Register("new-key"))).Error!.Code.Should().Be("limit_exceeded");
        (await UsageAsync(source, tenant)).Should().Be((1L, 4L, 4L));
    }

    [Fact]
    public async Task Different_key_registrations_serialize_at_the_last_slot_while_another_tenant_progresses()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var evidence = await ComponentAuthorizationEvidence.CreateAsync(database);
        _evidence = evidence;
        var firstTenant = await ProvisionAsync(source);
        var otherTenant = await ProvisionAsync(source);
        var gate = new QuotaGate();
        await using var host = Host(database, new Plan(1), gate);
        var first = SendAsync(host.Services, firstTenant, Register("winner"));
        Task<Result<TenantContentTypeDto>>? contender = null;
        try
        {
            await gate.Held.Task.WaitAsync(TimeSpan.FromSeconds(20));
            contender = SendAsync(host.Services, firstTenant, Register("contender"));
            var waitingPid = await gate.Contender.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await AssertAdvisoryWaitAsync(source, waitingPid);
            contender.IsCompleted.Should().BeFalse("a real database transaction lock serializes distinct keys");
            (await SendAsync(host.Services, otherTenant, Register("winner")).WaitAsync(TimeSpan.FromSeconds(20)))
                .IsSuccess.Should().BeTrue("another tenant neither shares the lock nor pays for the first tenant's usage");
        }
        finally
        {
            gate.Release.TrySetResult();
            // Finish both owned request scopes before the host/database can be
            // disposed, including when the positive lock observation refuses.
            await first.WaitAsync(TimeSpan.FromSeconds(20));
            if (contender is not null) await contender.WaitAsync(TimeSpan.FromSeconds(20));
        }

        (await first.WaitAsync(TimeSpan.FromSeconds(20))).IsSuccess.Should().BeTrue();
        (await contender!.WaitAsync(TimeSpan.FromSeconds(20))).Error!.Code.Should().Be("limit_exceeded");
        (await UsageAsync(source, firstTenant)).Should().Be((1L, 1L, 1L));
        (await UsageAsync(source, otherTenant)).Should().Be((1L, 1L, 1L));
    }

    private ServiceProvider BuildProvider(NpgsqlDataSource source, ITenantContext? context)
    {
        var services = ComponentOperationRegistration.CreateDatabaseServices(source, context);
        AddComponentAuthority(services);
        _evidence.AddTo(services);
        return services.BuildServiceProvider(validateScopes: true);
    }

    // Explicit admission for this component mechanism; this is not a live HTTP authorization proof.
    private static void AddComponentAuthority(IServiceCollection services) =>
        ComponentOperationRegistration.AddExactComponentAuthority(services,
            OperationPolicy.ForTrusted<InitializeTenantContentTypeDefaultCommand>("tests.component.quota.initialize", AuthorizationSourceKind.Seed),
            OperationPolicy.ForTrusted<RegisterTenantContentTypeCommand>("tests.component.quota.register", AuthorizationSourceKind.Seed),
            OperationPolicy.ForTrusted<PublishTenantContentTypeCommand>("tests.component.quota.publish", AuthorizationSourceKind.Seed),
            OperationPolicy.ForTrusted<ProvisionTenantCommand>("tests.component.quota.provision_tenant", AuthorizationSourceKind.Seed));

    private WebApplicationFactory<Program> Host(DisposableSchemaDatabase database, Plan? plan = null, QuotaGate? gate = null) =>
        factory.WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Default", database.AppConnectionString)
            .ConfigureServices(services =>
            {
                AddComponentAuthority(services);
                _evidence.AddTo(services);
            })
            .ConfigureServices(services =>
            {
                // Keep the real context-resolved FeatureFlags implementation;
                // only the sanctioned plan projection source is controlled.
                if (plan is not null) services.AddSingleton<IEntitlementProvider>(plan);
                if (gate is not null) services.AddScoped<ITenantContentTypeStore>(provider => new GatedStore(
                    new TenantContentTypeStore(provider.GetRequiredService<CustomizationDbContext>(), provider.GetRequiredService<CustomizationReadState>()),
                    provider.GetRequiredService<IUnitOfWork>(), gate));
            }));

    private static RegisterTenantContentTypeCommand Register(string key, int version = 1) => new(Guid.CreateVersion7(), key, version,
        new Dictionary<string, string> { ["en"] = "Quota proof" }, BuiltInCustomizations.Card.JsonSchema, BuiltInCustomizations.Card.RendererKey);

    private static async Task<Context> ProvisionAsync(NpgsqlDataSource source)
    {
        var id = TenantId.From(Guid.CreateVersion7());
        await ContentComponentFixture.CreateTenantAsync(source, id);
        return new(id);
    }

    private static async Task<Result<T>> SendAsync<T>(IServiceProvider services, ITenantContext? tenant, IRequest<Result<T>> command)
    {
        await using var scope = services.CreateAsyncScope();
        if (tenant is not null) scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().Current = tenant;
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(command);
    }

    private static async Task<(long CustomKeys, long Rows, long Generation)> UsageAsync(NpgsqlDataSource source, Context tenant)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, tenant.TenantId.Value);
        await AssertAppRoleAsync(connection, transaction);
        await using var command = new NpgsqlCommand("""
            SELECT count(DISTINCT key) FILTER (WHERE deleted_at IS NULL AND key <> 'card'), count(*),
              (SELECT generation FROM customization_generations WHERE tenant_id = @tenant)
            FROM tenant_content_types WHERE tenant_id = @tenant
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenant.TenantId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static async Task SoftDeleteAsync(NpgsqlDataSource source, Context tenant, Guid id)
    {
        // Fixture-only deletion exercises existing tombstone semantics without
        // introducing a restore or a new public lifecycle command.
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, tenant.TenantId.Value);
        await AssertAppRoleAsync(connection, transaction);
        await using var command = new NpgsqlCommand("""
            UPDATE tenant_content_types SET deleted_at = clock_timestamp(), deleted_by = @actor
            WHERE tenant_id = @tenant AND id = @id
            """, connection, transaction);
        command.Parameters.AddWithValue("actor", UserId.SystemActor.Value);
        command.Parameters.AddWithValue("tenant", tenant.TenantId.Value);
        command.Parameters.AddWithValue("id", id);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
        await transaction.CommitAsync();
    }

    private static async Task AssertAppRoleAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction)
    {
        await using var command = new NpgsqlCommand("SELECT current_user, rolsuper OR rolbypassrls FROM pg_roles WHERE rolname = current_user", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("learnstack_app");
        reader.GetBoolean(1).Should().BeFalse();
    }

    private static async Task AssertAdvisoryWaitAsync(NpgsqlDataSource source, int pid)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var connection = await source.OpenConnectionAsync(timeout.Token);
        await AssertAppRoleAsync(connection, null);
        while (true)
        {
            await using var command = new NpgsqlCommand("SELECT wait_event FROM pg_stat_activity WHERE pid = @pid", connection);
            command.Parameters.AddWithValue("pid", pid);
            if (await command.ExecuteScalarAsync(timeout.Token) is "advisory") return;
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed record Context(TenantId TenantId) : ITenantContext
    {
        public bool IsResolved => true;
        public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;
        public OrganizationId? OrganizationId => null;
        public UserId? UserId => LearnStack.SharedKernel.Identifiers.UserId.SystemActor;
        public string? CorrelationId => null;
        public string? ModuleName => "customization";
    }

    private sealed class Plan(long? limit = null, bool unlimited = false) : IEntitlementProvider
    {
        public Task<EntitlementProjection> GetAsync(TenantId tenantId, CancellationToken ct = default) =>
            Task.FromResult(new EntitlementProjection(tenantId, "quota-proof",
                new Dictionary<string, bool> { [FeatureKeys.UnlimitedContentTypes.Value] = unlimited },
                limit is { } value ? new Dictionary<string, long> { [LimitKeys.MaxCustomContentTypes.Value] = value }
                    : new Dictionary<string, long>(),
                ComplianceCaps.None, null, null, 1));

        public Task<EntitlementRefreshOutcome> RefreshAsync(EntitlementProjection projection, CancellationToken ct = default) =>
            throw new NotSupportedException("The quota proof only reads a plan projection.");
    }

    private sealed class QuotaGate
    {
        private int _calls;
        internal TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<int> Contender { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task LockAsync(ITenantContentTypeStore inner, IUnitOfWork unit, CancellationToken ct)
        {
            var first = Interlocked.Increment(ref _calls) == 1;
            if (!first) Contender.TrySetResult(((NpgsqlConnection)unit.Connection).ProcessID);
            await inner.LockRegistrationsAsync(ct);
            if (!first) return;
            Held.TrySetResult();
            await Release.Task.WaitAsync(ct);
        }
    }

    private sealed class GatedStore(ITenantContentTypeStore inner, IUnitOfWork unit, QuotaGate gate) : ITenantContentTypeStore
    {
        public Task LockRegistrationsAsync(CancellationToken cancellationToken = default) => gate.LockAsync(inner, unit, cancellationToken);
        public Task<ContentTypeQuotaUsage> ReadQuotaUsageAsync(string key, CancellationToken cancellationToken = default) => inner.ReadQuotaUsageAsync(key, cancellationToken);
        public Task LockConceptAsync(string key, CancellationToken cancellationToken = default) => inner.LockConceptAsync(key, cancellationToken);
        public Task<bool> HasConceptHistoryAsync(string key, CancellationToken cancellationToken = default) => inner.HasConceptHistoryAsync(key, cancellationToken);
        public Task<TenantContentType?> FindAsync(TenantContentTypeId id, CancellationToken cancellationToken = default) => inner.FindAsync(id, cancellationToken);
        public Task<TenantContentType?> FindActiveAsync(string key, CancellationToken cancellationToken = default) => inner.FindActiveAsync(key, cancellationToken);
        public Task AddAsync(TenantContentType aggregate, CancellationToken cancellationToken = default) => inner.AddAsync(aggregate, cancellationToken);
        public Task UpdateAsync(TenantContentType aggregate, CancellationToken cancellationToken = default) => inner.UpdateAsync(aggregate, cancellationToken);
    }
}
