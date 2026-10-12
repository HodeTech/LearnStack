using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Modules.Tenancy.Application.Contracts.IntegrationEvents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Composition;

public sealed class OutboxProductionCompositionTests
{
    [Fact]
    public void Real_provisioning_contract_remains_pending_for_both_distinct_default_consumers()
    {
        var handlers = IntegrationEventHandlerRegistry.Discover(
            typeof(LearnStack.Modules.Tenancy.Application.AssemblyMarker).Assembly,
            typeof(LearnStack.Modules.Customization.Application.AssemblyMarker).Assembly,
            typeof(LearnStack.Modules.Education.Application.AssemblyMarker).Assembly);
        var services = new ServiceCollection();
        services.AddSingleton(handlers);
        services.AddLearnStackPersistence(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<OutboxEventRegistry>();
        var identity = typeof(TenantCreatedIntegrationEventV1).AssemblyQualifiedName!;

        registry.PendingIdentities.Should().ContainSingle().Which.Should().Be(identity);
        var resolution = registry.Resolve(identity);
        resolution.EventType.Should().Be<TenantCreatedIntegrationEventV1>();
        resolution.Disposition.Should().Be(OutboxEventDisposition.Pending);
        resolution.Subscriptions.Select(subscription => subscription.Id).Should().BeEquivalentTo(
            ["customization.initialize-content-type-default.v1", "customization.initialize-taxonomy-default.v1"]);
        resolution.Subscriptions.Should().OnlyContain(subscription =>
            subscription.Readiness == OutboxSubscriptionReadiness.Pending && subscription.HandlerType == null);
        handlers.For(typeof(TenantCreatedIntegrationEventV1)).Should().BeEmpty(
            "P02b-3 activates real consumers; P02b-2 must preserve their facts");
    }
}
