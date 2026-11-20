using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Infrastructure.Identity.Federation;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Composition;

/// <summary>ADR-0099 § Authority: the real adapter replaces the fail-closed defaults only when the admin client is configured.</summary>
public sealed class FederationCompositionTests
{
    [Fact]
    public async Task Without_the_admin_secret_both_ports_keep_their_fail_closed_defaults()
    {
        await using var provider = Build(new Dictionary<string, string?>());
        provider.GetRequiredService<IFederationProvider>().Should().BeOfType<UnconfiguredFederationProvider>();
        provider.GetRequiredService<IFederationEgressFetcher>().Should().BeOfType<UnconfiguredFederationEgressFetcher>();
    }

    [Fact]
    public async Task With_the_admin_secret_the_confined_adapter_and_guarded_fetcher_are_selected()
    {
        await using var provider = Build(new Dictionary<string, string?>
        {
            ["Federation:ClientSecret"] = "federation-admin-secret",
            ["Authentication:Tenant:Issuer"] = "https://keycloak.example.com/realms/learnstack",
            ["Federation:Egress:AllowedHosts:0"] = "idp.corp.internal:8443",
        });
        provider.GetRequiredService<IFederationProvider>().GetType().Name.Should().Be("KeycloakFederationProvider");
        provider.GetRequiredService<IFederationEgressFetcher>().GetType().Name.Should().Be("FederationEgressFetcher");
        provider.GetRequiredService<FederationEgressOptions>().Count.Should().Be(1);
    }

    [Fact]
    public async Task Without_the_admin_secret_the_operator_port_is_closed_and_answers_nothing_it_could_act_on()
    {
        await using var provider = Build(new Dictionary<string, string?>());
        var operations = provider.GetRequiredService<IFederationOperator>();
        var connection = Guid.CreateVersion7();
        var owner = new FederationBrokerOwner(
            LearnStack.SharedKernel.Identifiers.TenantId.From(Guid.CreateVersion7()), connection, "lsf-" + connection.ToString("N"));

        operations.Should().BeOfType<UnconfiguredFederationOperator>();
        (await operations.ListAsync(0, 5)).Status.Should().Be(FederationOperatorStatus.Unavailable);
        (await operations.InspectAsync(owner.Alias)).Status.Should().Be(FederationInspectionStatus.Unavailable);
        (await operations.CountMappersAsync(owner)).Should().BeNull();
        (await operations.RemoveAsync(owner)).Should().Be(FederationOperatorOutcome.Unavailable);
        (await operations.DisableAsync(owner)).Should().Be(FederationOperatorOutcome.Unavailable);
    }

    [Fact]
    public async Task With_the_admin_secret_the_operator_is_its_own_adapter_instance_with_its_own_resilience_pipeline()
    {
        await using var provider = Build(new Dictionary<string, string?>
        {
            ["Federation:ClientSecret"] = "federation-admin-secret",
            ["Authentication:Tenant:Issuer"] = "https://keycloak.example.com/realms/learnstack",
        });

        var operations = provider.GetRequiredService<IFederationOperator>();
        var tenant = provider.GetRequiredService<IFederationProvider>();

        operations.GetType().Name.Should().Be("KeycloakFederationProvider");
        operations.Should().NotBeSameAs(tenant, "a sweep must not share the tenant calls' bulkhead and breaker");
        var operatorPipeline = provider.GetRequiredService<LearnStack.SharedKernel.Resilience.IProviderResilience<IFederationOperator>>();
        var tenantPipeline = provider.GetRequiredService<LearnStack.SharedKernel.Resilience.IProviderResilience<IFederationProvider>>();
        operatorPipeline.Pipeline.Should().NotBeSameAs(tenantPipeline.Pipeline);
    }

    [Fact]
    public void Plain_http_issuer_is_admitted_only_for_development_loopback()
    {
        var configuration = new Dictionary<string, string?>
        {
            ["Federation:ClientSecret"] = "federation-admin-secret",
            ["Authentication:Tenant:Issuer"] = "http://localhost:8080/realms/learnstack",
        };
        using (var development = Build(configuration, Environments.Development))
            development.GetRequiredService<IFederationProvider>().GetType().Name.Should().Be("KeycloakFederationProvider");
        using var production = Build(configuration, Environments.Production);
        FluentActions.Invoking(() => production.GetRequiredService<IFederationProvider>()).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("Resilience:federation:Retry:Enabled", "true")]
    [InlineData("Resilience:federation:Retry:MaxRetryAttempts", "2")]
    [InlineData("Resilience:federation:Timeout:Enabled", "false")]
    [InlineData("Resilience:federation:Timeout:TotalSeconds", "30")]
    [InlineData("Resilience:federation:Timeout:TotalSeconds", "2")]
    [InlineData("Resilience:federation:CircuitBreaker:Enabled", "false")]
    [InlineData("Resilience:federation:Bulkhead:MaxConcurrency", "8")]
    [InlineData("Resilience:federation:Bulkhead:MaxConcurrency", "1")]
    [InlineData("Resilience:federation:Bulkhead:QueueLength", "2")]
    public void Write_retries_unbounded_timeouts_and_a_disabled_breaker_refuse_at_composition(string key, string value)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();
        FluentActions.Invoking(() => services.AddLearnStackFederationAdapter(configuration)).Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("Resilience:federation:CircuitBreaker:FailureRatio", "0")]
    [InlineData("Resilience:federation:CircuitBreaker:FailureRatio", "1.01")]
    [InlineData("Resilience:federation:CircuitBreaker:FailureRatio", "NaN")]
    [InlineData("Resilience:federation:CircuitBreaker:MinimumThroughput", "1")]
    [InlineData("Resilience:federation:CircuitBreaker:MinimumThroughput", "101")]
    [InlineData("Resilience:federation:CircuitBreaker:SamplingDurationSeconds", "0.5")]
    [InlineData("Resilience:federation:CircuitBreaker:SamplingDurationSeconds", "61")]
    [InlineData("Resilience:federation:CircuitBreaker:BreakDurationSeconds", "0.5")]
    [InlineData("Resilience:federation:CircuitBreaker:BreakDurationSeconds", "301")]
    public void An_enabled_breaker_outside_its_fixed_range_refuses_at_composition(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();
        FluentActions.Invoking(() => new ServiceCollection().AddLearnStackFederationAdapter(configuration))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_breaker_at_every_range_edge_is_the_positive_control()
    {
        foreach (var values in new[]
        {
            new Dictionary<string, string?>
            {
                ["Resilience:federation:CircuitBreaker:FailureRatio"] = "1",
                ["Resilience:federation:CircuitBreaker:MinimumThroughput"] = "2",
                ["Resilience:federation:CircuitBreaker:SamplingDurationSeconds"] = "1",
                ["Resilience:federation:CircuitBreaker:BreakDurationSeconds"] = "1",
            },
            new Dictionary<string, string?>
            {
                ["Resilience:federation:CircuitBreaker:FailureRatio"] = "0.01",
                ["Resilience:federation:CircuitBreaker:MinimumThroughput"] = "100",
                ["Resilience:federation:CircuitBreaker:SamplingDurationSeconds"] = "60",
                ["Resilience:federation:CircuitBreaker:BreakDurationSeconds"] = "300",
            },
        })
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            FluentActions.Invoking(() => new ServiceCollection().AddLearnStackFederationAdapter(configuration)).Should().NotThrow();
        }
    }

    [Fact]
    public void The_fixed_resilience_shape_is_the_dirty_control()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Resilience:federation:Timeout:TotalSeconds"] = "2.5",
            ["Resilience:federation:Bulkhead:MaxConcurrency"] = "4",
        }).Build();
        FluentActions.Invoking(() => new ServiceCollection().AddLearnStackFederationAdapter(configuration)).Should().NotThrow();
    }

    [Fact]
    public void A_wildcard_allow_list_entry_refuses_at_composition()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Federation:Egress:AllowedHosts:0"] = "*.corp.internal" }).Build();
        FluentActions.Invoking(() => new ServiceCollection().AddLearnStackFederationAdapter(configuration)).Should().Throw<ArgumentException>();
    }

    private static ServiceProvider Build(Dictionary<string, string?> values, string environment = "Production")
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<ISecretProvider>(new ConfigurationSecrets(configuration));
        services.AddSingleton<IHostEnvironment>(new Environment(environment));
        // The module's defaults are registered first, exactly as the Identity foundation does.
        services.TryAddSingleton<IFederationProvider, UnconfiguredFederationProvider>();
        services.TryAddSingleton<IFederationEgressFetcher, UnconfiguredFederationEgressFetcher>();
        // The operator's port has its own fail-closed default, registered by the Identity foundation the same way.
        services.TryAddSingleton<IFederationOperator, UnconfiguredFederationOperator>();
        services.AddLearnStackFederationAdapter(configuration);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private sealed class ConfigurationSecrets(IConfiguration configuration) : ISecretProvider
    {
        public string? GetSecret(string key) => configuration[key];
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "LearnStack.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
