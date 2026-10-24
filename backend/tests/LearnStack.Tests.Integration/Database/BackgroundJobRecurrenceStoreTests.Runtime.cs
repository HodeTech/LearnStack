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
using Microsoft.Extensions.DependencyInjection.Extensions;
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
            await SeedLifecycleTenantAsync(app, RouteId(9000), RouteId(9001), "Suspended", false, true);
        }
        var today = await TodayAsync(database);
        var manifest = Fixture.Manifest(today.AddDays(-61), today.AddDays(-61), period: TimeSpan.FromDays(30),
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
        // Keep a lost native hint's legal repair observable inside this case's
        // unchanged 30-second wait. The actual control and DI executor share
        // this validated case-owned configuration; production defaults stay intact.
        var bounds = new BackgroundJobRuntimeOptions
        {
            ControlPoll = TimeSpan.FromMilliseconds(50),
            FanoutPageSize = 2,
            HandoffRepairGrace = TimeSpan.FromSeconds(15),
        };
        bounds.Validate();
        await using var host = RecipientHost(database, probe, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
            services.AddSingleton<IAuditCatalogSource>(new RecurrenceFixtureAuditSource());
        });
        await using var source = Source(database);
        using var globals = new RecurrenceGlobalFilters();
        var runtime = RecurrenceRuntime(host.Services, source, Fixture.Recurrences(manifest), bounds);
        using var metrics = new BackgroundJobMetricProbe(host.Services.GetRequiredService<BackgroundJobObservation>());
        long? completed = null;
        var boundary = "start";
        try
        {
            await runtime.StartAsync();
            boundary = "children";
            await RecurrenceEventuallyAsync(async () =>
            {
                await using var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
                completed = await ReadAsync<long>(connection, "SELECT count(*) FROM background_jobs WHERE phase='completed'");
                return completed == 2;
            });
            var expected = new[]
            {
                (SchemaFixture.TenantA, organization ? (Guid?)SchemaFixture.OrgA1 : null),
                (SchemaFixture.TenantB, organization ? (Guid?)SchemaFixture.OrgB1 : null),
            };
            executed.ToArray().Should().BeEquivalentTo(expected);
            boundary = "fanout";
            await RecurrenceEventuallyAsync(() => Task.FromResult(runtime.Snapshot.Control!.Recurrence!.FanoutsCompletedAcknowledged == 1));
            AssertProgress();
            AssertProgress();

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
            boundary = "ticks";
            await RecurrenceEventuallyAsync(async () =>
            {
                await using var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
                return await ReadAsync<long>(connection,
                    "SELECT count(*) FROM public.job WHERE id::text=ANY(@ticks) AND statename='Succeeded'", ("ticks", ticks)) == ticks.Length;
            });
            boundary = "health";
            await RecurrenceEventuallyAsync(() => Task.FromResult(runtime.Snapshot.IsHealthy));
            boundary = "assertions";
            await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            (await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_job_occurrences")).Should().Be(1);
            (await ReadAsync<long>(dispatcher, "SELECT skipped_slots FROM background_job_occurrences")).Should().Be(2);
            (await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_job_occurrences WHERE completed_at IS NOT NULL")).Should().Be(1);
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
            AssertProgress();
        }
        catch (Exception)
        {
            // Observe before stop changes the state. No diagnostic I/O, new
            // polling or deadline extension may hide the original test failure.
            try
            {
                var snapshot = runtime.Snapshot;
                var recurrence = snapshot.Control?.Recurrence;
                output.WriteLine("Recurrence runtime failure;boundary=" + boundary
                    + ";runtime=" + snapshot.State
                    + ";completed=" + Count(completed) + ";executed=" + Count(executed.Count)
                    + ";occurrences=" + Count(recurrence?.OccurrencesAcknowledged)
                    + ";recipients=" + Count(recurrence?.RecipientsInsertedAcknowledged)
                    + ";fanouts=" + Count(recurrence?.FanoutsCompletedAcknowledged)
                    + ";applications=" + Count(recurrence?.ApplicationOwners)
                    + ";retainedApplications=" + Count(recurrence?.RetainedApplications)
                    + ";bridgeOutstanding=" + Count(snapshot.Bridge?.Outstanding)
                    + ";bridgeRetained=" + Count(snapshot.Bridge?.Retained)
                    + ";bridgeFailed=" + Count(snapshot.Bridge?.Failed)
                    + ";executions=" + Count(snapshot.Control?.Executions)
                    + ";renewalRefused=" + Count(snapshot.Control?.LeaseLossRenewalRefused)
                    + ";leaseElapsed=" + Count(snapshot.Control?.LeaseLossElapsed) + ".");
            }
            catch (Exception) { } // Observation must not replace the original fault.
            throw;
        }
        finally
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await runtime.StopAsync(deadline.Token);
            await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        }
        runtime.Snapshot.CleanupConfirmed.Should().BeTrue();
        runtime.Snapshot.Control!.Recurrence!.CleanupConfirmed.Should().BeTrue();

        static string Count(long? value) => value is >= 0 and <= 10000
            ? value.Value.ToString(CultureInfo.InvariantCulture) : "unavailable";

        void AssertProgress()
        {
            var totals = runtime.Snapshot.Control!.Recurrence!;
            totals.OccurrencesAcknowledged.Should().Be(1);
            totals.SkippedSlotsAcknowledged.Should().Be(2);
            totals.PagesAcknowledged.Should().Be(2);
            totals.RoutesExaminedAcknowledged.Should().Be(3, "raw routes include the suspended tenant/organization");
            totals.RecipientsInsertedAcknowledged.Should().Be(2);
            totals.FanoutsCompletedAcknowledged.Should().Be(1);
            var points = metrics.Read();
            foreach (var (operation, value) in new (string, long)[]
                     {
                         ("occurrence_acknowledged", 1), ("skipped_slot_acknowledged", 2), ("fanout_page_acknowledged", 2),
                         ("fanout_route_examined_acknowledged", 3), ("fanout_recipient_inserted_acknowledged", 2),
                         ("fanout_completed_acknowledged", 1),
                     })
                BackgroundJobMetricProbe.One(points, "learnstack_job_control_total", ("operation", operation), ("pool", "none"))
                    .Should().Be(value);
            points.GroupBy(point => point.Name + ":" + string.Join(";", point.Tags.OrderBy(tag => tag.Key, StringComparer.Ordinal)
                    .Select(tag => tag.Key + "=" + tag.Value)), StringComparer.Ordinal)
                .Should().OnlyContain(group => group.Count() == 1);
        }
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
