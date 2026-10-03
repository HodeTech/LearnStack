using FluentAssertions;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[CollectionDefinition(Name)]
public sealed class PublicReadTestGroup : ICollectionFixture<PublicReadFixture>
{
    public const string Name = "Public read HTTP";
}

/// <summary>Production API composition, seeded through real writers in an isolated four-role database.</summary>
public sealed class PublicReadFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();
    public string AppConnectionString => _postgres.AppConnectionString;
    public PublicReadObservation Observation { get; } = new();

    public async Task InitializeAsync()
    {
        await _postgres.InitializeAsync();
        await MigrationChains.ApplyAllAsync(_postgres.MigrationConnectionString);
        await using var source = NpgsqlDataSource.Create(AppConnectionString);
        var runner = new SeedRunner(context => SeedComposition.Build(source, context, NullLoggerFactory.Instance), NullLogger<SeedRunner>.Instance);
        (await runner.RunAsync(CancellationToken.None)).Should().Be(0);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    public HttpClient ClientFor(string host, Action<IServiceCollection>? configure = null)
    {
        // Each client gets its own limiter/cache host; the database alone is shared.
        // A budget exhausted by one scenario cannot turn another scenario into a 429.
        var factory = WithWebHostBuilder(builder => builder.ConfigureTestServices(services => configure?.Invoke(services)));
        var client = factory.CreateClient();
        client.BaseAddress = new Uri($"http://{host}/");
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Default", AppConnectionString);
        builder.UseSetting("ConnectionStrings:PlatformAdmin", _postgres.PlatformConnectionString);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(Observation);
            services.AddScoped<PublicTenantConfigurationReader>();
            services.AddScoped<IPublicTenantConfigurationReader, ObservedPublicConfigurationReader>();
        });
    }

    /// <summary>Raw corruption/lifecycle fixture setup, never the connection on which isolation is observed.</summary>
    public async Task ExecuteAsync(SeedTenant tenant, string sql)
    {
        await using var connection = new NpgsqlConnection(AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var announce = new NpgsqlCommand("SELECT set_config('app.tenant_id', @tenant, true), set_config('app.organization_id', '', true)", connection, transaction))
        {
            announce.Parameters.AddWithValue("tenant", tenant.TenantId.Value.ToString("D"));
            await announce.ExecuteNonQueryAsync();
        }
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenant.TenantId.Value);
        command.Parameters.AddWithValue("organization", tenant.DefaultOrganization.OrganizationId.Value);
        command.Parameters.AddWithValue("actor", UserId.SystemActor.Value);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    public async Task<long> AuditCountAsync(SeedTenant tenant)
    {
        await using var connection = new NpgsqlConnection(AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var announce = new NpgsqlCommand("SELECT set_config('app.tenant_id', @tenant, true)", connection, transaction);
        announce.Parameters.AddWithValue("tenant", tenant.TenantId.Value.ToString("D"));
        await announce.ExecuteNonQueryAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM audit_log WHERE tenant_id=@tenant", connection, transaction);
        count.Parameters.AddWithValue("tenant", tenant.TenantId.Value);
        return (long)(await count.ExecuteScalarAsync())!;
    }
}

public sealed class PublicReadObservation
{
    private int _reads;
    public int Reads => Volatile.Read(ref _reads);
    public void Record() => Interlocked.Increment(ref _reads);
}

/// <summary>Observes the actual enlisted transaction before invoking the production reader.</summary>
public sealed class ObservedPublicConfigurationReader(
    PublicTenantConfigurationReader reader, IUnitOfWork unit, PublicReadObservation observation) : IPublicTenantConfigurationReader
{
    public async Task<Result<PublicTenantConfiguration>> ReadAsync(CancellationToken cancellationToken)
    {
        unit.Mode.Should().Be(TransactionMode.ReadOnly);
        await using var command = new NpgsqlCommand(
            "SELECT current_setting('transaction_read_only'), current_user, rolsuper, rolbypassrls FROM pg_roles WHERE rolname=current_user",
            (NpgsqlConnection)unit.Connection, (NpgsqlTransaction?)unit.Transaction);
        await using (var proof = await command.ExecuteReaderAsync(cancellationToken))
        {
            (await proof.ReadAsync(cancellationToken)).Should().BeTrue();
            proof.GetString(0).Should().Be("on");
            proof.GetString(1).Should().Be("learnstack_app");
            proof.GetBoolean(2).Should().BeFalse();
            proof.GetBoolean(3).Should().BeFalse();
        }
        observation.Record();
        return await reader.ReadAsync(cancellationToken);
    }
}
