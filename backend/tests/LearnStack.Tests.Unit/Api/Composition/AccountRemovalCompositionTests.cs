using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Resilience;
using LearnStack.SharedKernel.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Composition;

/// <summary>ADR-0107 § 7: the confined adapter replaces the refusing default only when its own client secret is configured.</summary>
public sealed class AccountRemovalCompositionTests
{
    private const string Issuer = "https://keycloak.example.com/realms/learnstack";

    [Fact]
    public void The_identity_foundation_registers_the_refusing_default_as_a_singleton()
    {
        var services = new ServiceCollection();
        LearnStack.Modules.Identity.Infrastructure.Bindings.IdentityFoundationRegistration.AddIdentityContextualFoundation(services);
        var descriptor = services.Single(registered => registered.ServiceType == typeof(IIdentityProviderAccountRemoval));
        descriptor.ImplementationType.Should().Be<RefusingIdentityProviderAccountRemoval>();
        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public async Task Without_the_client_secret_the_port_keeps_its_refusing_default()
    {
        await using var provider = Build(new Dictionary<string, string?> { ["Authentication:Tenant:Issuer"] = Issuer });
        var port = provider.GetRequiredService<IIdentityProviderAccountRemoval>();
        port.Should().BeOfType<RefusingIdentityProviderAccountRemoval>();
        (await port.RemoveAsync("0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d")).Should().Be(AccountRemovalOutcome.Refused);
    }

    [Fact]
    public async Task With_the_client_secret_the_confined_adapter_is_selected_under_its_own_resilience_pipeline()
    {
        await using var provider = Build(new Dictionary<string, string?>
        {
            ["AccountRemoval:ClientSecret"] = "account-removal-secret",
            ["Authentication:Tenant:Issuer"] = Issuer,
        });
        provider.GetRequiredService<IIdentityProviderAccountRemoval>().GetType().Name.Should().Be("KeycloakAccountRemoval");
        provider.GetRequiredService<IProviderResilience<IIdentityProviderAccountRemoval>>().PortName.Should().Be("accountRemoval");
    }

    [Theory]
    [InlineData("Federation:ClientSecret")]
    [InlineData("IdentityEvents:ClientSecret")]
    public async Task Another_clients_secret_never_selects_the_adapter(string key)
    {
        await using var provider = Build(new Dictionary<string, string?> { [key] = "another-clients-secret", ["Authentication:Tenant:Issuer"] = Issuer });
        provider.GetRequiredService<IIdentityProviderAccountRemoval>().Should().BeOfType<RefusingIdentityProviderAccountRemoval>();
    }

    [Fact]
    public void Plain_http_issuer_is_admitted_only_for_development_loopback()
    {
        var configuration = new Dictionary<string, string?>
        {
            ["AccountRemoval:ClientSecret"] = "account-removal-secret",
            ["Authentication:Tenant:Issuer"] = "http://localhost:8080/realms/learnstack",
        };
        using (var development = Build(configuration, Environments.Development))
            development.GetRequiredService<IIdentityProviderAccountRemoval>().GetType().Name.Should().Be("KeycloakAccountRemoval");
        using var production = Build(configuration, Environments.Production);
        FluentActions.Invoking(() => production.GetRequiredService<IIdentityProviderAccountRemoval>()).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("Resilience:accountRemoval:Retry:Enabled", "true")]
    [InlineData("Resilience:accountRemoval:Retry:MaxRetryAttempts", "1")]
    [InlineData("Resilience:accountRemoval:Timeout:Enabled", "false")]
    [InlineData("Resilience:accountRemoval:Timeout:TotalSeconds", "30")]
    [InlineData("Resilience:accountRemoval:Timeout:TotalSeconds", "2.5")]
    [InlineData("Resilience:accountRemoval:CircuitBreaker:Enabled", "false")]
    [InlineData("Resilience:accountRemoval:Bulkhead:MaxConcurrency", "2")]
    [InlineData("Resilience:accountRemoval:Bulkhead:MaxConcurrency", "0")]
    [InlineData("Resilience:accountRemoval:Bulkhead:QueueLength", "1")]
    public void A_retry_an_unbounded_timeout_a_disabled_breaker_or_another_bulkhead_refuses_at_composition(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();
        FluentActions.Invoking(() => new ServiceCollection().AddLearnStackAccountRemovalAdapter(configuration)).Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("Resilience:accountRemoval:CircuitBreaker:FailureRatio", "0")]
    [InlineData("Resilience:accountRemoval:CircuitBreaker:FailureRatio", "1.01")]
    [InlineData("Resilience:accountRemoval:CircuitBreaker:MinimumThroughput", "1")]
    [InlineData("Resilience:accountRemoval:CircuitBreaker:SamplingDurationSeconds", "61")]
    [InlineData("Resilience:accountRemoval:CircuitBreaker:BreakDurationSeconds", "301")]
    public void An_enabled_breaker_outside_its_fixed_range_refuses_at_composition(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();
        FluentActions.Invoking(() => new ServiceCollection().AddLearnStackAccountRemovalAdapter(configuration)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void The_federation_pipeline_values_do_not_configure_this_port_and_the_fixed_shape_is_the_dirty_control()
    {
        // The federation section is another port's: its bulkhead of four does not leak into this one.
        var other = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Resilience:federation:Bulkhead:MaxConcurrency"] = "4", ["Resilience:federation:Timeout:TotalSeconds"] = "2.5" }).Build();
        FluentActions.Invoking(() => new ServiceCollection().AddLearnStackAccountRemovalAdapter(other)).Should().NotThrow();

        var fixedShape = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Resilience:accountRemoval:Timeout:TotalSeconds"] = "5",
            ["Resilience:accountRemoval:Bulkhead:MaxConcurrency"] = "1",
            ["Resilience:accountRemoval:Retry:Enabled"] = "false",
        }).Build();
        FluentActions.Invoking(() => new ServiceCollection().AddLearnStackAccountRemovalAdapter(fixedShape)).Should().NotThrow();
    }

    private static ServiceProvider Build(Dictionary<string, string?> values, string environment = "Production")
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<ISecretProvider>(new ConfigurationSecrets(configuration));
        services.AddSingleton<IHostEnvironment>(new Environment(environment));
        services.AddLogging();
        // The module's default is registered first, exactly as the Identity foundation does.
        services.TryAddSingleton<IIdentityProviderAccountRemoval, RefusingIdentityProviderAccountRemoval>();
        services.AddLearnStackAccountRemovalAdapter(configuration);
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
