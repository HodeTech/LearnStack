using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Api.Identity;
using LearnStack.Infrastructure.Identity.ProviderEvents;
using LearnStack.SharedKernel.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Composition;

public sealed class ProviderEventCompositionTests
{
    [Fact]
    public async Task Private_health_exposes_retirement_backlog_and_recovers_only_after_it_drains()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        await using var provider = services.BuildServiceProvider();
        var health = new ProviderEventMirrorHealth(provider.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());
        var now = DateTimeOffset.UtcNow;
        var current = new ProviderEventHealthState(false, false, now, now, []);
        health.Observe(current with { RetirableReceipts = 105, RetirableProblems = 205, OldestRetirementOverdueSeconds = 123 }, true);
        var overdue = await health.CheckHealthAsync(new());
        overdue.Status.Should().Be(HealthStatus.Unhealthy);
        overdue.Data["retirement_receipts_count"].Should().Be(105L);
        overdue.Data["retirement_problems_count"].Should().Be(205L);
        overdue.Data["retirement_overdue_seconds"].Should().Be(123d);
        health.Observe(current, true);
        (await health.CheckHealthAsync(new())).Status.Should().Be(HealthStatus.Healthy);
        health.ProductionFailed();
        health.Observe(current, true);
        (await health.CheckHealthAsync(new())).Status.Should().Be(HealthStatus.Unhealthy,
            "retirement success cannot conceal an unrelated production ownership failure");
    }

    [Fact]
    public async Task Machine_source_is_one_lazy_owned_singleton_and_publication_uses_fresh_scopes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var connection = new NpgsqlConnectionStringBuilder
        { Host = "192.0.2.1", Database = "owned_not_opened", Username = "learnstack_identity_ingress", Password = Guid.NewGuid().ToString("N") };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:IdentityIngress"] = connection.ConnectionString }).Build();
        services.AddLearnStackProviderEventMirror(configuration);
        services.AddLearnStackProviderEventMirror(configuration);
        services.Single(descriptor => descriptor.ServiceType == typeof(ProviderEventIntakeSource)).Lifetime.Should().Be(ServiceLifetime.Singleton);
        services.Single(descriptor => descriptor.ServiceType == typeof(IIdentityEventProvider)).Lifetime.Should().Be(ServiceLifetime.Singleton);
        services.Single(descriptor => descriptor.ServiceType == typeof(ProviderEventProductionBinding)).Lifetime.Should().Be(ServiceLifetime.Scoped);
        services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(ProviderEventMirrorService)).Should().ContainSingle();
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var source = provider.GetRequiredService<ProviderEventIntakeSource>();
        provider.GetRequiredService<ProviderEventIntakeSource>().Should().BeSameAs(source);
        source.IsQualified.Should().BeFalse("construction creates no connector or authority qualification");
        provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Where(registration => registration.Name == ProviderEventMirrorHealth.Name).Should().ContainSingle();
    }
}
