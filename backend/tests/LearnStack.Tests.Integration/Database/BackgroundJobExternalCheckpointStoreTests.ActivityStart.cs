using System.Diagnostics;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admission_and_prepare_retain_the_actual_activity_when_start_throws_before_returning(bool preparing)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var trace = ActivityTraceId.CreateRandom();
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            preparing ? BackgroundJobRuntimeFixture.ExternalKey : BackgroundJobRuntimeFixture.TenantKey,
            capacityGroup: preparing ? BackgroundJobRuntimeFixture.CapacityGroup : null,
            profile: preparing ? "external" : "transactional", traceparent: $"00-{trace}-{ActivitySpanId.CreateRandom()}-01");
        await using var host = preparing ? RecoveryHost(database, preparing: true)
            : Fixture.Host(database, new BackgroundJobFixtureProbe());
        var executor = host.Services.GetRequiredService<JobExecutor>();
        ConfirmedStartReceipt? original = preparing
            ? await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job)) : null;
        var claim = preparing ? null : await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, 1);
        if (!preparing) claim.Should().NotBeNull();
        Activity? captured = null;
        var stopped = false;
        var throws = 0;
        var name = preparing ? "job.execution" : "job.admission";
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BackgroundJobObservation.InstrumentationName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (ReferenceEquals(activity, captured)) stopped = true;
            },
        };
        EventHandler<ActivityChangedEventArgs> observer = (_, change) =>
        {
            if (change.Current is not { } activity || activity.OperationName != name || activity.TraceId != trace
                || Interlocked.CompareExchange(ref throws, 1, 0) != 0) return;
            captured = activity;
            throw new InvalidOperationException("Explicit asynchronous-entry Activity start fault.");
        };
        ActivitySource.AddActivityListener(listener);
        var previous = Activity.Current;
        Activity.CurrentChanged += observer;
        try
        {
            if (original is not null)
            {
                var result = await executor.ExecuteBusinessAsync(original, original.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare));
                result.CommitStarted.Should().BeFalse();
                result.CommitAcknowledged.Should().BeFalse();
                result.CleanupFailed.Should().BeFalse();
                result.RetainedCleanup.Should().BeEmpty();
            }
            else
            {
                var result = await executor.ExecuteAsync(claim!, BackgroundJobTestRun.Create());
                result.Receipt.Should().BeNull();
                result.CleanupFailed.Should().BeFalse();
                result.RetainedCleanup.Should().BeEmpty();
                result.SourceOwner.CleanupConfirmed.Should().BeTrue();
            }
        }
        finally { Activity.CurrentChanged -= observer; }
        throws.Should().Be(1);
        captured.Should().NotBeNull();
        stopped.Should().BeTrue("the exact Activity must remain owned even if Start throws before returning it");
        Activity.Current.Should().BeSameAs(previous);
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(preparing ? 1 : 0);
        var row = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
        row.Phase.Should().Be(preparing ? "preparing" : "pending");
    }
}
