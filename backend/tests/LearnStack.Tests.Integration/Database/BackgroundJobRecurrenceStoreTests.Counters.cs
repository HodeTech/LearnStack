using System.Reflection;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRecurrenceStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Original_recurrence_COMMIT_with_lost_ACK_never_becomes_acknowledged_progress_from_a_returned_value(bool page)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var direct = Source(database);
        var bounds = new BackgroundJobRuntimeOptions();
        var definitions = Fixture.Definitions();
        var gate = new BackgroundJobRuntimeGate(definitions.Infrastructure);
        var ids = new SystemGuidFactory();
        var today = await TodayAsync(database);
        var registry = Fixture.Recurrences(Fixture.Manifest(today.AddDays(-61), today.AddDays(-61), period: TimeSpan.FromDays(30)));
        var manifest = registry.Keys.Single();
        registry.TryGet(manifest, out var descriptor).Should().BeTrue();
        var setup = new PostgresBackgroundJobControlStore(direct, definitions, bounds, ids, gate);
        (await setup.RegisterRecurrenceAsync(descriptor!)).CommitAcknowledged.Should().BeTrue();
        if (page) (await setup.MaterializeRecurrenceAsync(registry)).CommitAcknowledged.Should().BeTrue();
        await using var host = RecipientHost(database, new());
        await using var proxy = new CommitResponseDroppingProxy(database.OutboxConnectionString, holdCommitResponse: true);
        await using var source = new LearnStack.Infrastructure.Persistence.PrivilegedDataSource(
            () => OutboxDispatcherDataSource.Build(proxy.ConnectionString));
        var projection = LearnStack.Modules.Tenancy.Infrastructure.Jobs.BackgroundJobLifecycleProjectionDeclaration.Create();
        var owner = new BackgroundJobRecurrenceControl(source, definitions, registry, projection,
            host.Services.GetRequiredService<JobExecutor>(), gate, bounds, ids, TimeProvider.System);
        var store = new PostgresBackgroundJobControlStore(source, definitions, bounds, ids, gate);
        Task? observed = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (page)
                observed = ObserveAsync(store.MaterializeRecipientPageAsync(registry, projection));
            else
                observed = ObserveAsync(store.MaterializeRecurrenceAsync(registry));
            await proxy.CommitResponseHeld.WaitAsync(deadline.Token);
            observed.IsCompleted.Should().BeFalse();
            await using (var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
            {
                (await ReadAsync<long>(connection, page
                    ? "SELECT count(*) FROM background_job_occurrences WHERE completed_at IS NOT NULL"
                    : "SELECT count(*) FROM background_job_occurrences")).Should().Be(1);
            }
            NoAcknowledgedProgress();
            proxy.ReleaseHeldResponse();
            await observed.WaitAsync(deadline.Token);
            proxy.DroppedCommitResponses.Should().Be(1);
            NoAcknowledgedProgress();
            // A later real no-op query cannot backfill the missing physical ACK.
            if (page) await ObserveAsync(setup.MaterializeRecipientPageAsync(registry, projection), uncertain: false);
            else await ObserveAsync(setup.MaterializeRecurrenceAsync(registry), uncertain: false);
            NoAcknowledgedProgress();
        }
        finally
        {
            proxy.ReleaseHeldResponse();
            try { if (observed is not null) await observed.WaitAsync(TimeSpan.FromSeconds(15)); }
            finally
            {
                gate.StopStarts();
                owner.RequestStop();
                await owner.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        async Task ObserveAsync<T>(Task<OwnedBackgroundJobControl<T>> flight, bool uncertain = true)
        {
            // Isolate the actual SQL owner hook; no native recurrence/worker
            // lane is started and no competing COMMIT selects the proxy fault.
            var method = typeof(BackgroundJobRecurrenceControl).GetMethod("ObserveSqlAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .MakeGenericMethod(typeof(T));
            var returned = await (Task<OwnedBackgroundJobControl<T>?>)method.Invoke(owner, [flight, page ? 2 : 1])!;
            var original = await flight;
            original.CommitAcknowledged.Should().BeFalse();
            original.CommitUncertain.Should().Be(uncertain);
            original.CleanupConfirmed.Should().BeTrue();
            if (uncertain)
            {
                original.Value.Should().NotBeNull("the real value was assigned before the server committed and lost its ACK");
                returned.Should().BeNull();
            }
            else original.Value.Should().BeNull();
        }

        void NoAcknowledgedProgress()
        {
            var snapshot = owner.Snapshot;
            new[] { snapshot.OccurrencesAcknowledged, snapshot.SkippedSlotsAcknowledged, snapshot.PagesAcknowledged,
                snapshot.RoutesExaminedAcknowledged, snapshot.RecipientsInsertedAcknowledged, snapshot.FanoutsCompletedAcknowledged }
                .Should().OnlyContain(value => value == 0);
        }
    }
}
