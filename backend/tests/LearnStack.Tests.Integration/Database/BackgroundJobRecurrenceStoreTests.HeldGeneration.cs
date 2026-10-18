using System.Globalization;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRecurrenceStoreTests
{
    [Fact]
    public async Task Durable_recurrence_progresses_before_ticks_while_the_actual_control_generation_remains_drained()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
            await SeedLifecycleTenantAsync(app, SchemaFixture.TenantA, SchemaFixture.OrgA1, "Active", false, true);
        var today = await TodayAsync(database);
        var manifest = Fixture.Manifest(today.AddDays(-61), today.AddDays(-61), period: TimeSpan.FromDays(30));
        var registry = Fixture.Recurrences(manifest);
        var executions = 0;
        var probe = new BackgroundJobFixtureProbe
        {
            Body = (_, _, _) => { Interlocked.Increment(ref executions); return Task.FromResult(Result.Ok(None.Value)); },
        };
        await using var host = RecipientHost(database, probe,
            configure: services => services.AddSingleton<IAuditCatalogSource>(new RecurrenceFixtureAuditSource()));
        await using var source = Source(database);
        using var globals = new RecurrenceGlobalFilters();
        var store = Store(source);
        await AssertRegistrationAsync(store, manifest, BackgroundJobRecurrenceRegistrationResult.Registered);
        await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        NpgsqlTransaction? blocked = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("""
            SELECT recurrence_key FROM background_job_recurrences WHERE recurrence_key=@key FOR UPDATE
            """, blocker, blocked) { CommandTimeout = 5 })
        {
            command.Parameters.AddWithValue("key", manifest.Facts.Key);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await command.ExecuteNonQueryAsync(deadline.Token);
        }
        var runtime = RecurrenceRuntime(host.Services, source, registry,
            new() { ControlPoll = TimeSpan.FromMilliseconds(50) });
        var application = host.Services.GetRequiredService<ApplicationDataSourceLifetime>();
        HangfireGenerationWork.Invocation? held = null;
        try
        {
            await runtime.StartAsync();
            await RecurrenceEventuallyAsync(() => Task.FromResult(runtime.Snapshot.Pools.Any(pool => pool.Pool == "control" && pool.State == "Running")));
            HangfirePoolOwner pool;
            lock (NativeHoldField<object>(runtime, "_sync"))
                pool = NativeHoldField<List<HangfirePoolOwner>>(runtime, "_pools").Single(item => item.Snapshot.Pool == "control");
            var original = NativeHoldField<HangfireGeneration>(pool, "_generation");
            // Hold the actual generation's owned invocation; it is an explicit
            // synthetic retention barrier, not a claimed business/network fault.
            held = original.TryBeginWork() ?? throw new InvalidOperationException("The ready control generation refused its hold.");
            held.MarkRetained();
            original.RequestDrain(HangfireGenerationReason.HeartbeatTransient);
            await RecurrenceEventuallyAsync(() => Task.FromResult(original.Snapshot.NormalChildrenJoined));
            original.Completion.IsCompleted.Should().BeFalse();
            await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            (await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_job_occurrences")).Should().Be(0,
                "the actual manifest row lock must precede the first due occurrence");
            (await ReadAsync<long>(dispatcher, "SELECT count(*) FROM public.job")).Should().Be(0,
                "no native tick or business wakeup was issued before the generation drained");

            // The original control children are positively joined before the
            // row lock permits any occurrence/fan-out progress.
            await blocked.RollbackAsync();
            await blocked.DisposeAsync();
            blocked = null;
            await RecurrenceEventuallyAsync(async () =>
                await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_jobs WHERE phase='completed'") == 1);
            original.Completion.IsCompleted.Should().BeFalse();
            NativeHoldField<HangfireGeneration>(pool, "_generation").Should().BeSameAs(original);
            (await ReadAsync<long>(dispatcher, "SELECT skipped_slots FROM background_job_occurrences")).Should().Be(2);
            (await ReadAsync<DateTimeOffset>(dispatcher, "SELECT scheduled_at FROM background_job_occurrences"))
                .Should().Be(today.AddDays(-1));
            await AssertHeldGenerationRecurrenceReceiptAsync(dispatcher);
            Volatile.Read(ref executions).Should().Be(1);

            // These actual opaque stock wakeups remain queued because the
            // original generation has no running normal children/replacement.
            var ticks = QueueHeldGenerationDuplicateTicks(source, manifest);
            (await ReadAsync<long>(dispatcher,
                "SELECT count(*) FROM public.job WHERE id::text=ANY(@ticks) AND statename='Enqueued'", ("ticks", ticks))).Should().Be(ticks.Length);
            for (var duplicate = 0; duplicate < 3; duplicate++)
            {
                var quantum = await store.MaterializeRecurrenceAsync(registry);
                quantum.ThrowIfFailed();
                quantum.CleanupConfirmed.Should().BeTrue();
                quantum.Value.Should().BeNull("a repeat quantum cannot spend another logical slot");
            }
            original.Completion.IsCompleted.Should().BeFalse();
            await AssertHeldGenerationRecurrenceReceiptAsync(dispatcher);

            await held.CompleteAsync(cleanupConfirmed: true);
            held = null;
            await original.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            await RecurrenceEventuallyAsync(async () =>
                await ReadAsync<long>(dispatcher,
                    "SELECT count(*) FROM public.job WHERE id::text=ANY(@ticks) AND statename='Succeeded'", ("ticks", ticks)) == ticks.Length);
            NativeHoldField<HangfireGeneration>(pool, "_generation").Should().NotBeSameAs(original);
            await AssertHeldGenerationRecurrenceReceiptAsync(dispatcher);
            Volatile.Read(ref executions).Should().Be(1, "late duplicate native ticks cannot execute the same logical occurrence again");
        }
        finally
        {
            if (blocked is not null) { await blocked.RollbackAsync(); await blocked.DisposeAsync(); }
            if (held is not null) await held.CompleteAsync(cleanupConfirmed: true);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await runtime.StopAsync(deadline.Token);
            await runtime.Completion.WaitAsync(deadline.Token);
        }
        runtime.Snapshot.CleanupConfirmed.Should().BeTrue();
        source.Dispose();
        application.Dispose();
        await RecurrenceEventuallyAsync(() => Task.FromResult(source.IsDisposalComplete && application.IsDisposalComplete));
        source.HasUnconfirmedCleanup.Should().BeFalse();
        application.HasUnconfirmedCleanup.Should().BeFalse();
    }

    private static async Task AssertHeldGenerationRecurrenceReceiptAsync(System.Data.Common.DbConnection connection)
    {
        (await ReadAsync<long>(connection, "SELECT count(*) FROM background_job_occurrences")).Should().Be(1);
        (await ReadAsync<long>(connection, "SELECT count(*) FROM background_job_recipients")).Should().Be(1);
        (await ReadAsync<long>(connection, "SELECT count(*) FROM background_jobs")).Should().Be(1);
        (await ReadAsync<long>(connection, "SELECT count(*) FROM background_job_attempts")).Should().Be(1);
        (await ReadAsync<long>(connection, """
            SELECT count(*) FROM background_job_recipients r JOIN background_jobs j
                ON j.id=r.accepted_job_id AND j.tenant_id=r.tenant_id AND j.recipient_id=r.id
            WHERE r.accepted_at IS NOT NULL AND r.ownership_token IS NULL AND j.normal_attempts=1 AND j.phase='completed'
            """)).Should().Be(1);
    }

    private static string[] QueueHeldGenerationDuplicateTicks(LearnStack.Infrastructure.Persistence.PrivilegedDataSource source,
        BackgroundJobRecurrenceDescriptor manifest)
    {
        var client = new BackgroundJobClient(NativeStorage(source), HangfireRuntimeFilters.Create());
        var tick = new Job(typeof(HangfireRecurrenceTick), HangfireInvocationContract.TickMethod,
            [manifest.Facts.Key, manifest.Facts.Revision], "control");
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            return Enumerable.Range(0, 3).Select(_ => client.Create(tick, new EnqueuedState("control"))).ToArray();
        }
        finally { CultureInfo.CurrentCulture = culture; CultureInfo.CurrentUICulture = uiCulture; }
    }
}
