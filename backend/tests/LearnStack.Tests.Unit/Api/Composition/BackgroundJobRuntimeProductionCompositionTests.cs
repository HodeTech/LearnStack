using System.Reflection;
using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Api.Jobs;
using LearnStack.Api.Messaging;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Composition;

public sealed class BackgroundJobRuntimeProductionCompositionTests
{
    [Fact]
    public async Task Mandatory_hosted_aliases_bind_the_actual_messaging_instances_and_keep_the_source_lazy()
    {
        var builder = Builder(52);
        builder.Services.AddLearnStackPersistence(builder.Configuration);
        builder.Services.AddLearnStackOutboxDispatcher(builder.Configuration);
        await using var services = builder.Services.BuildServiceProvider();
        var hosted = services.GetServices<IHostedService>().ToArray();
        var outbox = services.GetRequiredService<OutboxProcessor>();
        var delivery = services.GetRequiredService<DeliveryProcessor>();
        var jobs = services.GetRequiredService<BackgroundJobHostedService>();
        var runtime = services.GetRequiredService<BackgroundJobRuntime>();
        var source = services.GetRequiredKeyedService<PrivilegedDataSource>(OutboxDispatcherDataSource.Key);

        hosted.OfType<OutboxProcessor>().Should().ContainSingle().Which.Should().BeSameAs(outbox);
        hosted.OfType<DeliveryProcessor>().Should().ContainSingle().Which.Should().BeSameAs(delivery);
        hosted.OfType<BackgroundJobHostedService>().Should().ContainSingle().Which.Should().BeSameAs(jobs);
        Field(runtime, "_outboxCompletion").Should().BeSameAs(outbox.PrivilegedCompletion);
        Field(runtime, "_deliveryCompletion").Should().BeSameAs(delivery.PrivilegedCompletion);
        source.IsValueCreated.Should().BeFalse("composition and health registration must not connect");

        // This proof issues no startup. Disposal must still establish positive
        // no-issued facts for all three owners, rather than strand the source pin.
        jobs.Dispose();
        outbox.Dispose();
        delivery.Dispose();
        await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await source.DisposeAsync();
        await ((Task)Field(source, "_retentionObserver")!).WaitAsync(TimeSpan.FromSeconds(5));
        source.IsDisposalComplete.Should().BeTrue();
        source.IsValueCreated.Should().BeFalse();
    }

    [Fact]
    public async Task Captured_capacity_refuses_one_below_the_supported_floor_before_source_construction()
    {
        var builder = Builder(51);
        builder.Services.AddLearnStackPersistence(builder.Configuration);
        builder.Configuration["ConnectionStrings:OutboxDispatcher"] = Connection(100);
        await using var services = builder.Services.BuildServiceProvider();
        var source = services.GetRequiredKeyedService<PrivilegedDataSource>(OutboxDispatcherDataSource.Key);
        var resolve = () => services.GetRequiredService<BackgroundJobRuntime>();
        resolve.Should().Throw<ArgumentException>().WithMessage("*supported physical capacity*");
        source.IsValueCreated.Should().BeFalse();
        services.GetRequiredService<BackgroundJobPhysicalPoolSettings>().MaxPoolSize.Should().Be(51,
            "the source and its sanitized settings must use the same immutable registration input");
    }

    [Theory]
    [InlineData(31, false)]
    [InlineData(32, true)]
    public async Task Captured_application_capacity_includes_external_work_before_either_source_is_constructed(
        int maximum, bool accepted)
    {
        var builder = Builder(52);
        builder.Configuration["ConnectionStrings:Default"] = ApplicationConnection(maximum);
        builder.Services.AddLearnStackPersistence(builder.Configuration);
        builder.Services.AddLearnStackOutboxDispatcher(builder.Configuration);
        builder.Configuration["ConnectionStrings:Default"] = ApplicationConnection(100);
        await using var services = builder.Services.BuildServiceProvider();
        var privileged = services.GetRequiredKeyedService<PrivilegedDataSource>(OutboxDispatcherDataSource.Key);
        var application = services.GetRequiredService<ApplicationDataSourceLifetime>();
        application.PoolSettings.Should().Be(new BackgroundJobApplicationPoolSettings(true, maximum));
        if (!accepted)
        {
            var resolve = () => services.GetRequiredService<BackgroundJobRuntime>();
            resolve.Should().Throw<ArgumentException>().WithMessage("*job-only physical capacity*");
        }
        else
        {
            var hosted = services.GetServices<IHostedService>().ToArray();
            var runtime = services.GetRequiredService<BackgroundJobRuntime>();
            runtime.Snapshot.ApplicationCapacity.Should().Be(new BackgroundJobApplicationCapacity(11, 32));
            foreach (var owner in hosted.OfType<IDisposable>()) owner.Dispose();
            await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        privileged.IsValueCreated.Should().BeFalse();
        application.Source.IsValueCreated.Should().BeFalse();
    }

    private static string ApplicationConnection(int maximum) =>
        "Host=localhost;Port=1;Database=learnstack;Username=learnstack_app;Maximum Pool Size="
        + maximum.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static WebApplicationBuilder Builder(int maxPoolSize)
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:OutboxDispatcher"] = Connection(maxPoolSize),
        });
        builder.AddLearnStackCrossCuttingFoundation(DeploymentMode.Development,
            typeof(LearnStack.Modules.Tenancy.Application.AssemblyMarker).Assembly,
            typeof(LearnStack.Modules.Customization.Application.AssemblyMarker).Assembly,
            typeof(LearnStack.Modules.Education.Application.AssemblyMarker).Assembly);
        return builder;
    }

    private static string Connection(int maxPoolSize) =>
        "Host=localhost;Port=1;Database=learnstack;Username=learnstack_outbox_admin;Maximum Pool Size="
        + maxPoolSize.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static object? Field(object owner, string name) => owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);
}
