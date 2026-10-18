using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Infrastructure.Jobs;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRecurrenceStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_runtime_fans_out_to_both_scopes_and_duplicate_native_ticks_create_no_extra_children(bool organization)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
        {
            await SeedLifecycleTenantAsync(app, SchemaFixture.TenantA, SchemaFixture.OrgA1, "Active", false, true);
            await SeedLifecycleTenantAsync(app, SchemaFixture.TenantB, SchemaFixture.OrgB1, "Active", false, true);
        }
        var today = await TodayAsync(database);
        var manifest = Fixture.Manifest(today.AddDays(-1), today.AddDays(-1), period: TimeSpan.FromDays(30),
            target: organization ? "jobsrecurrencefixture.organization.v1" : "jobsrecurrencefixture.run.v1",
            policy: organization ? BackgroundJobRecipientPolicy.ServingOrganizations : BackgroundJobRecipientPolicy.ServingTenants);
        var executed = new ConcurrentQueue<(Guid Tenant, Guid? Organization)>();
        var probe = new BackgroundJobFixtureProbe
        {
            Body = (_, services, _) =>
            {
                var context = services.GetRequiredService<ITenantContextAccessor>().Current
                    ?? throw new InvalidOperationException("The actual recurrence target has no tenant context.");
                executed.Enqueue((context.TenantId.Value, context.OrganizationId?.Value));
                return Task.FromResult(Result.Ok(None.Value));
            },
        };
        await using var host = RecipientHost(database, probe,
            configure: services => services.AddSingleton<IAuditCatalogSource>(new RecurrenceFixtureAuditSource()));
        await using var source = Source(database);
        using var globals = new RecurrenceGlobalFilters();
        var bounds = new BackgroundJobRuntimeOptions { ControlPoll = TimeSpan.FromMilliseconds(50) };
        var runtime = RecurrenceRuntime(host.Services, source, Fixture.Recurrences(manifest), bounds);
        try
        {
            await runtime.StartAsync();
            await RecurrenceEventuallyAsync(async () =>
            {
                await using var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
                return await ReadAsync<long>(connection, "SELECT count(*) FROM background_jobs WHERE phase='completed'") == 2;
            });
            var expected = new[]
            {
                (SchemaFixture.TenantA, organization ? (Guid?)SchemaFixture.OrgA1 : null),
                (SchemaFixture.TenantB, organization ? (Guid?)SchemaFixture.OrgB1 : null),
            };
            executed.ToArray().Should().BeEquivalentTo(expected);

            // Actual stock workers invoke the opaque tick wrapper. Each tick
            // must succeed while the durable occurrence remains the authority.
            var client = new BackgroundJobClient(NativeStorage(source), HangfireRuntimeFilters.Create());
            var tick = new Job(typeof(HangfireRecurrenceTick), HangfireInvocationContract.TickMethod,
                [manifest.Facts.Key, manifest.Facts.Revision], "control");
            string[] ticks;
            var culture = CultureInfo.CurrentCulture;
            var uiCulture = CultureInfo.CurrentUICulture;
            try
            {
                // Match the production native owner's invariant, payload-free
                // transport boundary; ambient test culture is not job metadata.
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
                ticks = Enumerable.Range(0, 3).Select(_ => client.Create(tick, new EnqueuedState("control"))).ToArray();
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = uiCulture;
            }
            await RecurrenceEventuallyAsync(async () =>
            {
                await using var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
                return await ReadAsync<long>(connection,
                    "SELECT count(*) FROM public.job WHERE id::text=ANY(@ticks) AND statename='Succeeded'", ("ticks", ticks)) == ticks.Length;
            });
            await RecurrenceEventuallyAsync(() => Task.FromResult(runtime.Snapshot.IsHealthy));
            await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            (await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_job_occurrences")).Should().Be(1);
            (await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_job_recipients")).Should().Be(2);
            (await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_jobs")).Should().Be(2);
            (await ReadAsync<long>(dispatcher, """
                SELECT count(*) FROM background_job_recipients r JOIN background_jobs j
                    ON j.id=r.accepted_job_id AND j.tenant_id=r.tenant_id AND j.recipient_id=r.id
                WHERE r.accepted_at IS NOT NULL AND r.ownership_token IS NULL
                    AND j.normal_attempts=1 AND j.phase='completed'
                    AND j.organization_id IS NOT DISTINCT FROM r.organization_id
                """)).Should().Be(2);
            executed.ToArray().Should().BeEquivalentTo(expected);
            runtime.Snapshot.Control!.Recurrence!.RetainedApplications.Should().Be(0);
        }
        finally
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await runtime.StopAsync(deadline.Token);
            await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        }
        runtime.Snapshot.CleanupConfirmed.Should().BeTrue();
        runtime.Snapshot.Control!.Recurrence!.CleanupConfirmed.Should().BeTrue();
    }

    private static BackgroundJobRuntime RecurrenceRuntime(IServiceProvider services, PrivilegedDataSource source,
        BackgroundJobRecurrenceRegistry recurrences, BackgroundJobRuntimeOptions bounds,
        BackgroundJobDefinitionRegistry? definitions = null)
    {
        definitions ??= Fixture.Definitions();
        var gate = new BackgroundJobRuntimeGate(definitions.Infrastructure);
        var ids = new SystemGuidFactory();
        var routing = new PostgresBackgroundJobRoutingStore(source, definitions, bounds, ids, gate);
        var runtime = new BackgroundJobRuntime(source, definitions, recurrences,
            services.GetRequiredService<JobExecutor>(), routing, gate, bounds, ids, TimeProvider.System,
            new(true, 100), new HangfireBoundedLogProvider(), services.GetRequiredService<ApplicationDataSourceLifetime>(),
            BackgroundJobLifecycleProjectionDeclaration.Create());
        // This construction-only host issues neither messaging worker. These
        // completed tasks are no-issued-work facts, not simulated worker joins.
        runtime.BindMessagingLifetime(Task.CompletedTask, Task.CompletedTask);
        return runtime;
    }

    private static async Task RecurrenceEventuallyAsync(Func<Task<bool>> predicate)
    {
        var elapsed = Stopwatch.StartNew();
        while (!await predicate())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException("The actual recurrence runtime did not reach the required state.");
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
    }

    private sealed class RecurrenceFixtureAuditSource : IAuditCatalogSource
    {
        public string ModuleName => "jobsrecurrencefixture";
        public void Describe(IAuditCatalogBuilder builder)
        {
            foreach (var request in Fixture.Module.Application.GetTypes().Where(type => type.GetInterfaces().Any(contract =>
                         contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IRequest<>))))
                typeof(IAuditCatalogBuilder).GetMethod(nameof(IAuditCatalogBuilder.Off))!
                    .MakeGenericMethod(request).Invoke(builder, null);
        }
    }

    private sealed class RecurrenceGlobalFilters : IDisposable
    {
        private readonly JobFilter[] _original = GlobalJobFilters.Filters.ToArray();
        public void Dispose()
        {
            GlobalJobFilters.Filters.Clear();
            foreach (var filter in _original) GlobalJobFilters.Filters.Add(filter.Instance, filter.Order);
        }
    }
}
