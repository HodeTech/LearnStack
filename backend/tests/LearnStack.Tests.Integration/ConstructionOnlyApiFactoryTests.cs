using FluentAssertions;
using LearnStack.Api.Messaging;
using LearnStack.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration;

public sealed class ConstructionOnlyApiFactoryTests
{
    [Fact]
    public void Request_only_test_composition_removes_exactly_both_dispatchers_and_preserves_other_workers()
    {
        var services = new ServiceCollection();
        services.AddHostedService<OutboxProcessor>();
        services.AddHostedService<DeliveryProcessor>();
        services.AddHostedService<OtherWorker>();
        services.Count(descriptor => descriptor.ServiceType == typeof(IHostedService)).Should().Be(3);

        ConstructionOnlyApiFactory.RemoveDispatcher(services);

        services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Should().ContainSingle().Which.ImplementationType.Should().Be<OtherWorker>();
    }

    [Fact]
    public async Task Construction_only_host_leaves_the_dispatcher_source_unresolved()
    {
        await using var host = new ConstructionOnlyApiFactory();
        using var client = host.CreateClient();

        host.Services.GetServices<IHostedService>().Should().NotContain(service => service is OutboxProcessor || service is DeliveryProcessor);
        host.Services.GetRequiredKeyedService<Lazy<NpgsqlDataSource>>(OutboxDispatcherDataSource.Key)
            .IsValueCreated.Should().BeFalse();
        (await client.GetAsync("/healthz")).IsSuccessStatusCode.Should().BeTrue();
    }

    private sealed class OtherWorker : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
