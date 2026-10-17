using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(6, true)]
    public async Task Actual_terminal_control_joins_no_start_then_waits_real_lease_before_one_atomic_audit(int ordinal, bool emptyDefinitions)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var setupHost = NoPlanRefusalHost(database);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var bounds = TerminalOwnerBounds();
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, bounds, new SystemGuidFactory(), runtime);
        var normal = await RequestedTerminalGuardFixtureAsync(database, setupHost.Services, ordinal, store, runtime);
        await using var host = NoPlanRefusalHost(database, emptyDefinitions);
        var registry = host.Services.GetRequiredService<BackgroundJobDefinitionRegistry>();
        var control = new BackgroundJobControl(source, registry, host.Services.GetRequiredService<JobExecutor>(),
            runtime, bounds, new SystemGuidFactory(), TimeProvider.System);
        control.Start();
        try
        {
            await WaitTerminalOwnerConditionAsync(async () =>
            {
                await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
                await using var transaction = await app.BeginTransactionAsync();
                await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
                await EducationSchemaSeed.AnnounceAsync(app, transaction, normal.Envelope.TenantId.Value, null);
                await using var count = new NpgsqlCommand("""
                    SELECT count(*) FROM background_job_reservations WHERE job_id=@id
                        AND released_at IS NOT NULL AND release_reason='never_started'
                    """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
                count.Parameters.AddWithValue("id", normal.Envelope.JobId.Value);
                return (long)(await count.ExecuteScalarAsync())! == ordinal;
            }, TimeSpan.FromSeconds(15));
            var cleaned = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, normal.Envelope.JobId))!;
            cleaned.OwnershipToken.Should().NotBeNull();
            cleaned.OwnershipUntil.Should().NotBeNull();
            var dbNow = await RetryDatabaseClockAsync(database);
            dbNow.Should().BeBefore(cleaned.OwnershipUntil!.Value,
                "actual app no-start/release finishes before its remaining scheduling lease; no fixture expiry is used");
            (await ReadNoPlanTerminalAuditAsync(database, normal)).Should().BeNull();
            (await ReadNoPlanTerminalStateAsync(database, normal)).Should().BeFalse();
            await WaitTerminalOwnerConditionAsync(() => ReadNoPlanTerminalStateAsync(database, normal), TimeSpan.FromSeconds(30));
            (await RetryDatabaseClockAsync(database)).Should().BeOnOrAfter(cleaned.OwnershipUntil.Value);
            var metadata = await ReadNoPlanTerminalAuditAsync(database, normal);
            metadata.Should().NotBeNull();
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, normal.Envelope.JobId)).Should().Be(ordinal);
        }
        finally
        {
            control.RequestStop();
            await control.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        }
        control.Snapshot.CleanupConfirmed.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Held_actual_terminal_app_scope_does_not_stop_independent_terminal_audit_and_is_joined_at_shutdown(bool cleanupFault)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var setupHost = NoPlanRefusalHost(database);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var bounds = TerminalOwnerBounds();
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, bounds, new SystemGuidFactory(), runtime);
        var normal = await RequestedTerminalGuardFixtureAsync(database, setupHost.Services, 1, store, runtime);
        var probe = new ReadDriverCleanupProbe(cleanupFault);
        await using var host = NoPlanRefusalHost(database).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(probe);
            services.AddScoped<ReadDriverHeldCleanup>();
            services.AddTransient<IPipelineBehavior<ReadBackgroundJobExternalStateQuery, Result<BackgroundJobExternalSnapshot>>,
                ReadDriverResolveCleanup>();
        }));
        var control = new BackgroundJobControl(source, Fixture.Registry, host.Services.GetRequiredService<JobExecutor>(),
            runtime, bounds, new SystemGuidFactory(), TimeProvider.System);
        BackgroundJobTerminalNoStartOperation? actualOwner = null;
        control.Start();
        try
        {
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            actualOwner = (BackgroundJobTerminalNoStartOperation?)typeof(BackgroundJobControl)
                .GetField("_terminalNoStart", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(control);
            actualOwner.Should().NotBeNull();
            actualOwner!.Source.Should().BeSameAs(probe.Request!.Authority.SourceOwner);
            probe.Request!.CommitAcknowledged.Should().BeTrue();
            probe.Request.Authority.SourceOwner.CleanupConfirmed.Should().BeFalse();
            control.CanRoute(normal.Envelope.JobId).Should().BeFalse();
            var other = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString, BackgroundJobRuntimeFixture.TenantKey);
            await using (var sql = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
                await SchemaQueries.ExecuteAsync(sql, null,
                    "UPDATE background_jobs SET phase='terminal_pending',terminal_reason='invalid_envelope' WHERE id=@id", ("id", other.Value));
            await WaitTerminalOwnerConditionAsync(async () =>
            {
                await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
                await using var transaction = await app.BeginTransactionAsync();
                await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
                await EducationSchemaSeed.AnnounceAsync(app, transaction, normal.Envelope.TenantId.Value, null);
                await using var count = new NpgsqlCommand("""
                    SELECT count(*) FROM audit_log WHERE metadata->>'job_id'=@id AND operation='platform.job.dead_letter'
                    """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
                count.Parameters.AddWithValue("id", other.Value.ToString("D"));
                return (long)(await count.ExecuteScalarAsync())! == 1;
            }, TimeSpan.FromSeconds(15));
            probe.Release.Task.IsCompleted.Should().BeFalse("the independent MUST audit commits while the actual app scope is held");
            control.CanRoute(normal.Envelope.JobId).Should().BeFalse();
            (await ReadNoPlanTerminalAuditAsync(database, normal)).Should().BeNull();
            control.RequestStop();
            control.Completion.IsCompleted.Should().BeFalse("host stop must still join the actual held scope");
        }
        finally
        {
            control.RequestStop();
            probe.Release.TrySetResult();
            if (!cleanupFault) await control.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            else if (actualOwner is not null)
            {
                (await actualOwner.Completion.WaitAsync(TimeSpan.FromSeconds(15))).Should().BeFalse();
                probe.Connection!.State.Should().Be(System.Data.ConnectionState.Closed,
                    "provider state is inspected only after the original task and actual cleanup have returned");
            }
        }
        control.Snapshot.CleanupConfirmed.Should().Be(!cleanupFault);
        control.Completion.IsCompletedSuccessfully.Should().Be(!cleanupFault);
        (await ReadNoPlanTerminalAuditAsync(database, normal)).Should().BeNull();
    }

    private static BackgroundJobRuntimeOptions TerminalOwnerBounds() => new()
    {
        SqlTimeout = TimeSpan.FromSeconds(3),
        ProbeTimeout = TimeSpan.FromSeconds(3),
        LeaseDuration = TimeSpan.FromSeconds(15),
        RenewalInterval = TimeSpan.FromSeconds(1),
        ProgressTimeout = TimeSpan.FromSeconds(30),
        ControlPoll = TimeSpan.FromMilliseconds(50),
    };

    private static async Task WaitTerminalOwnerConditionAsync(Func<Task<bool>> condition, TimeSpan budget)
    {
        using var timeout = new CancellationTokenSource(budget);
        while (!await condition()) await Task.Delay(20, timeout.Token);
    }
}
