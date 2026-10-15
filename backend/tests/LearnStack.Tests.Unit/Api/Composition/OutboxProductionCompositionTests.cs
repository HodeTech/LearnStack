using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Api.Messaging;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Modules.Customization.Application.IntegrationEvents;
using LearnStack.Modules.Tenancy.Application.Contracts.IntegrationEvents;
using LearnStack.SharedKernel.Hosting;
using LearnStack.SharedKernel.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Composition;

public sealed class OutboxProductionCompositionTests
{
    [Fact]
    public void Production_registers_exactly_one_mandatory_worker_and_the_unmapped_outbox_health_check()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddLearnStackOutboxDispatcher(configuration);
        services.AddLearnStackOutboxDispatcher(configuration);

        services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(OutboxProcessor))
            .Should().ContainSingle("mandatory execution must not depend on a configuration toggle");
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Where(registration => registration.Name == OutboxHealthCheck.Name).Should().ContainSingle();
    }

    [Fact]
    public void Real_provisioning_contract_activates_both_distinct_default_consumers_with_exact_handler_bindings()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Configuration.Sources.Clear();
        builder.AddLearnStackCrossCuttingFoundation(DeploymentMode.Development,
            typeof(LearnStack.Modules.Tenancy.Application.AssemblyMarker).Assembly,
            typeof(LearnStack.Modules.Customization.Application.AssemblyMarker).Assembly,
            typeof(LearnStack.Modules.Education.Application.AssemblyMarker).Assembly);
        using var provider = builder.Services.BuildServiceProvider();
        var handlers = provider.GetRequiredService<IntegrationEventHandlerRegistry>();
        var registry = provider.GetRequiredService<OutboxEventRegistry>();
        provider.GetRequiredService<IEventBus>().Should().BeOfType<InProcessEventBus>(
            "the construction-free foundation resolves the transport without database registration");
        var identity = typeof(TenantCreatedIntegrationEventV1).AssemblyQualifiedName!;

        registry.PendingIdentities.Should().BeEmpty();
        var resolution = registry.Resolve(identity);
        resolution.EventType.Should().Be<TenantCreatedIntegrationEventV1>();
        resolution.Disposition.Should().Be(OutboxEventDisposition.Active);
        resolution.Subscriptions.Select(subscription => subscription.Id).Should().BeEquivalentTo(
            ["customization.initialize-content-type-default.v1", "customization.initialize-taxonomy-default.v1"]);
        resolution.Subscriptions.Should().OnlyContain(subscription =>
            subscription.Readiness == OutboxSubscriptionReadiness.Active);
        resolution.Subscriptions.Single(subscription => subscription.Id == "customization.initialize-content-type-default.v1")
            .HandlerType.Should().Be<InitializeContentTypeDefaultHandler>();
        resolution.Subscriptions.Single(subscription => subscription.Id == "customization.initialize-taxonomy-default.v1")
            .HandlerType.Should().Be<InitializeTaxonomyDefaultHandler>();
        handlers.All.Should().HaveCount(2, "both production consumers must be discovered");
        handlers.For(typeof(TenantCreatedIntegrationEventV1)).Select(subscription => subscription.HandlerType)
            .Should().BeEquivalentTo([typeof(InitializeContentTypeDefaultHandler), typeof(InitializeTaxonomyDefaultHandler)]);
    }
}
