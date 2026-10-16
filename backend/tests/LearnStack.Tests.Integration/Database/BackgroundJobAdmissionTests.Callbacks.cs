using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DTO_construction_and_attempt_ID_factory_cannot_open_ordinary_or_privileged_work(bool factoryCallback)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var probe = new BackgroundJobFixtureProbe();
        var gate = new PermissiveGate();
        var ids = new CallbackGuidFactory();
        var callbacks = 0;
        var constructor = Fixture.ParameterType.GetField("OnConstruction")!;
        Action For(IServiceProvider services) => () =>
        {
            callbacks++;
            // Both callbacks run synchronously inside the actual production
            // serializer/identifier seam. They must finish before SQL proceeds.
            AssertFrameworkCeilingAsync(services, probe).GetAwaiter().GetResult();
        };
        var observation = new BackgroundJobAdmissionObservation
        {
            OnRead = (_, services) =>
            {
                if (!factoryCallback) constructor.SetValue(null, For(services));
                return Task.CompletedTask;
            },
            OnStart = (_, services) =>
            {
                if (factoryCallback) ids.OnMint = For(services);
                return Task.CompletedTask;
            },
        };
        await using var host = Fixture.Host(database, probe, configure: services =>
        {
            observation.Configure(services);
            services.AddSingleton<IGuidFactory>(ids);
            services.AddSingleton<IPlatformAdminGate>(gate);
        });
        try
        {
            var claim = (await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, 1))!;
            var result = await host.Services.GetRequiredService<JobExecutor>().ExecuteAsync(claim);
            result.Outcome.Should().Be(JobStartExecutionOutcome.Started);
            callbacks.Should().BeGreaterThan(0, "the callback must actually run in the production DTO/factory path");
            probe.Constructions.Should().BeEmpty();
            gate.Calls.Should().Be(0, "privileged refusal precedes the deliberately permissive gate");
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
        }
        finally
        {
            constructor.SetValue(null, null);
            ids.OnMint = null;
        }
    }

    private sealed class CallbackGuidFactory : IGuidFactory
    {
        internal Action? OnMint { get; set; }
        public Guid NewUuidV7() { OnMint?.Invoke(); return Guid.CreateVersion7(); }
        public Guid NewUuidV4() => Guid.NewGuid();
    }
}
