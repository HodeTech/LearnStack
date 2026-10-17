using System.Diagnostics;
using FluentAssertions;
using Hangfire;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    [Theory]
    [InlineData("succeeded", null)]
    [InlineData("permanent", "permanent_failure")]
    [InlineData("not_repeatable", "external_retry_forbidden")]
    [InlineData("cancelled_not_repeatable", "external_retry_forbidden")]
    public async Task Actual_facade_native_wakeups_run_Prepare_provider_Outcome_and_fresh_Finalize(
        string mode, string? terminal)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var resource = Guid.CreateVersion7();
        var prepared = OrganizationId.From(Guid.CreateVersion7());
        var finalized = OrganizationId.From(Guid.CreateVersion7());
        await InstallTransactionObserverAsync(database, prepared, finalized);
        await provider.SetModeAsync(resource, mode);
        var construction = new BackgroundJobPositiveProviderConstructionProbe();
        await using var host = Fixture.Host(database, provider.Settings, construction, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(new BackgroundJobRuntimeOptions { HandoffRepairGrace = TimeSpan.FromSeconds(15) });
        }, applicationPoolSize: 24);
        var bounds = host.Services.GetRequiredService<BackgroundJobRuntimeOptions>();
        var dispatcherConnection = new NpgsqlConnectionStringBuilder(database.OutboxConnectionString)
        { Pooling = true, MaxPoolSize = 50 }.ConnectionString;
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(dispatcherConnection));
        using var globals = new NativeSdkGlobalFilterSnapshot();
        var gate = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var ids = new SystemGuidFactory();
        var runtime = new BackgroundJobRuntime(source, Fixture.Registry, BackgroundJobRecurrenceRegistry.CreateEmpty(),
            host.Services.GetRequiredService<JobExecutor>(),
            new(source, Fixture.Registry, bounds, ids, gate), gate, bounds, ids, TimeProvider.System,
            new(true, 50), new HangfireBoundedLogProvider(), host.Services.GetRequiredService<ApplicationDataSourceLifetime>());
        // Construction-only host removed both messaging workers; neither issued work.
        runtime.BindMessagingLifetime(Task.CompletedTask, Task.CompletedTask);
        var submitted = await Fixture.SubmitAsync(host.Services, Fixture.Parameters(resource, prepared, finalized));
        submitted.IsSuccess.Should().BeTrue();
        var job = submitted.Value;
        try
        {
            using var start = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await runtime.StartAsync(start.Token);
            runtime.Snapshot.ApplicationCapacity.Should().Be(new BackgroundJobApplicationCapacity(11, 24));
            host.Services.GetRequiredService<ApplicationDataSourceLifetime>().PoolSettings
                .Should().Be(new BackgroundJobApplicationPoolSettings(true, 24));
            await using (var physical = await source.OpenConnectionAsync(start.Token))
            {
                var settings = new NpgsqlConnectionStringBuilder(physical.ConnectionString);
                settings.Pooling.Should().BeTrue();
                settings.MaxPoolSize.Should().Be(50);
            }
            var waiting = Stopwatch.StartNew();
            // A successor hint may finish before the original Outcome cleanup
            // releases routing. Include the accepted same-generation repair
            // grace and one bounded Finalize, rather than assuming first delivery.
            var completionBudget = bounds.HandoffRepairGrace + bounds.TransactionTimeout + bounds.SqlTimeout;
            while (waiting.Elapsed < completionBudget)
            {
                var current = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
                if (current.Phase == (terminal is null ? "completed" : "terminal_pending")) break;
                await Task.Delay(20);
            }
            var row = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
            row.Phase.Should().Be(terminal is null ? "completed" : "terminal_pending",
                "native completion includes bounded handoff repair; runtime={0}", runtime.Snapshot);
            row.NormalAttempts.Should().Be(1);
            construction.Calls.Should().Be(1, "only the original native normal execution calls the real adapter");
            construction.ExternalDenial.Should().BeTrue();
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, prepared.Value)).Should().Be(1);
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, finalized.Value)).Should().Be(1);
            await using var connection = await PostgresFixture.OpenAsync(database.AppConnectionString);
            await using var transaction = await connection.BeginTransactionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(connection, transaction);
            await EducationSchemaSeed.AnnounceAsync(connection, transaction, SchemaFixture.TenantA, null);
            await using var inspect = new NpgsqlCommand("""
                SELECT j.operation_id,j.finalize_attempts,j.terminal_reason,j.business_finalization_incomplete,
                    (SELECT count(*) FROM background_job_attempts a WHERE a.job_id=j.id AND a.stage='normal'),
                    (SELECT count(*) FROM background_job_attempts a WHERE a.job_id=j.id AND a.stage='finalize'),
                    (SELECT count(*) FROM background_job_reservations r WHERE r.job_id=j.id AND r.released_at IS NOT NULL),
                    (SELECT count(*) FROM background_job_reservations r WHERE r.job_id=j.id),
                    (SELECT count(*) FROM audit_log a WHERE a.operation='platform.job.dead_letter' AND a.metadata->>'job_id'=@text)
                FROM background_jobs j WHERE j.id=@id
                """, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
            inspect.Parameters.AddWithValue("id", job.Value);
            inspect.Parameters.AddWithValue("text", job.Value.ToString("D"));
            Guid operation;
            await using (var actual = await inspect.ExecuteReaderAsync())
            {
                (await actual.ReadAsync()).Should().BeTrue();
                operation = actual.GetGuid(0);
                actual.GetInt32(1).Should().Be(1);
                (actual.IsDBNull(2) ? null : actual.GetString(2)).Should().Be(terminal);
                actual.GetBoolean(3).Should().BeFalse();
                actual.GetInt64(4).Should().Be(1);
                actual.GetInt64(5).Should().Be(1);
                actual.GetInt64(6).Should().Be(1);
                actual.GetInt64(7).Should().Be(1);
                actual.GetInt64(8).Should().Be(terminal is null ? 0 : 1);
            }
            (await provider.CountsAsync(operation)).Should().Be((1L, 0L, terminal is null ? 1L : 0L));
            await AssertObservedTransactionsAsync(connection, transaction, terminal is not null);
        }
        finally
        {
            runtime.RequestStop();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await runtime.StopAsync(cleanup.Token);
            await runtime.Completion.WaitAsync(cleanup.Token);
        }
        runtime.Snapshot.CleanupConfirmed.Should().BeTrue();
        runtime.Snapshot.Control!.Executions.Should().Be(0);
        runtime.Snapshot.RetainedInvocations.Should().Be(0);
    }

    private sealed class NativeSdkGlobalFilterSnapshot : IDisposable
    {
        private readonly Hangfire.Common.JobFilter[] _filters = GlobalJobFilters.Filters.ToArray();
        public void Dispose()
        {
            GlobalJobFilters.Filters.Clear();
            foreach (var filter in _filters) GlobalJobFilters.Filters.Add(filter.Instance, filter.Order);
        }
    }
}
