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
    [InlineData("Resilience:federation:CircuitBreaker:Enabled", "false")]
    public void Write_retries_unbounded_timeouts_and_a_disabled_breaker_refuse_at_composition(string key, string value)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();
        FluentActions.Invoking(() => services.AddLearnStackFederationAdapter(configuration)).Should().Throw<InvalidOperationException>();
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
