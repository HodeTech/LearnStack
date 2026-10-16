using System.Data;
using System.Diagnostics;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobAdmissionTests
{
    [Fact]
    public async Task A_held_start_COMMIT_acknowledgement_honors_the_SQL_deadline_with_an_unlimited_default()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        await using var proxy = new CommitResponseDroppingProxy(database.AppConnectionString,
            commitsToPass: 1, holdCommitResponse: true);
        var appConnection = new NpgsqlConnectionStringBuilder(proxy.ConnectionString)
        { CommandTimeout = 0, CancellationTimeout = 1000 }.ConnectionString;
        var options = new BackgroundJobRuntimeOptions { SqlTimeout = TimeSpan.FromSeconds(1) };
        options.Validate();
        var probe = new BackgroundJobFixtureProbe();
        var roots = Fixture.RootConstructions;
        var original = new BackgroundJobFixtureContext();
        var observation = new BackgroundJobAdmissionObservation
        {
            OnStart = (_, services) =>
            {
                using var inherited = services.GetRequiredService<LearnStack.SharedKernel.Persistence.IUnitOfWork>()
                    .Connection.CreateCommand();
                inherited.CommandTimeout.Should().Be(0, "the actual admission connection has an unlimited command default");
                return Task.CompletedTask;
            },
        };
        await using var host = Fixture.Host(database, probe, original, services =>
        {
            services.AddSingleton(options);
            observation.Configure(services);
        }, appConnection);
        var claim = (await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, 1))!;
        using var activity = new Activity("held-ack-deadline-proof").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var proofDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var elapsed = Stopwatch.StartNew();
        var running = host.Services.GetRequiredService<JobExecutor>().ExecuteAsync(claim, proofDeadline.Token);
        try
        {
            // The proxy passed the read-only COMMIT and observed the actual start
            // COMMIT. It keeps the session alive instead of manufacturing EOF.
            await proxy.CommitResponseHeld.WaitAsync(TimeSpan.FromSeconds(5));
            proxy.DroppedCommitResponses.Should().Be(1);
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1,
                "the durable start landed before its acknowledgement was held");
            var result = await running.WaitAsync(TimeSpan.FromSeconds(6));
            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(6));
            proofDeadline.IsCancellationRequested.Should().BeFalse("the independent 12-second proof deadline did not end the wait");
            result.Outcome.Should().Be(JobStartExecutionOutcome.Indeterminate);
            result.Receipt.Should().BeNull("SQL deadline expiry cannot acknowledge a physically committed start");
            observation.ReadScopeDisposed.Should().BeTrue();
            observation.StartScopeDisposed.Should().BeTrue();
            observation.ReadConnection!.State.Should().Be(ConnectionState.Closed);
            observation.StartConnection!.State.Should().Be(ConnectionState.Closed);
            var row = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
            row.NormalAttempts.Should().Be(1);
            row.Phase.Should().Be("executing");
            host.Services.GetRequiredService<ITenantContextAccessor>().Current.Should().BeSameAs(original);
            Activity.Current.Should().BeSameAs(activity);
            probe.Constructions.Should().BeEmpty();
            Fixture.RootConstructions.Should().Be(roots);
        }
        finally
        {
            proxy.ReleaseHeldResponse();
            await proofDeadline.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
