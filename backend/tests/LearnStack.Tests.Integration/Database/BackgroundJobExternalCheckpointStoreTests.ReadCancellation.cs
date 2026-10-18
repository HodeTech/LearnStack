using System.Diagnostics;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_cancellation_closes_the_run_even_before_issuance_or_during_actual_activity_cleanup(bool alreadyCancelled)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var trace = ActivityTraceId.CreateRandom();
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external",
            traceparent: $"00-{trace}-{ActivitySpanId.CreateRandom()}-01");
        await using var host = RecoveryHost(database, preparing: true);
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BackgroundJobObservation.InstrumentationName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (alreadyCancelled || activity.OperationName != "job.external_read" || activity.TraceId != trace) return;
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            },
        };
        ActivitySource.AddActivityListener(listener);
        using var cancellation = new CancellationTokenSource();
        if (alreadyCancelled) await cancellation.CancelAsync();
        // Own the task before a fully synchronous prefix can reach the deliberately
        // blocking ActivityStopped observer. The release remains in this test flow.
        var actual = Task.Run(() => host.Services.GetRequiredService<JobExecutor>().ReadExternalStateAsync(original, cancellation.Token));
        Exception? preconditionFailure = null;
        try
        {
            if (!alreadyCancelled)
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                actual.IsCompleted.Should().BeFalse("the actual Activity cleanup still belongs to this read");
                original.SourceOwner.CleanupConfirmed.Should().BeTrue("the main unit cleaned before the held Activity observer");
                await cancellation.CancelAsync();
            }
            else
            {
                await actual.WaitAsync(TimeSpan.FromSeconds(15));
            }
            original.Run.IsClosed.Should().BeTrue("cancellation cannot miss the read's actual cleanup lifetime");
        }
        catch (Exception failure) { preconditionFailure = failure; }
        finally { release.TrySetResult(); }
        var result = await actual.WaitAsync(TimeSpan.FromSeconds(15));
        preconditionFailure.Should().BeNull();
        result.CleanupFailed.Should().BeFalse();
        result.RetainedCleanup.Should().BeEmpty();
        original.Run.IsClosed.Should().BeTrue();
        result.CommitAcknowledged.Should().Be(!alreadyCancelled);
        if (alreadyCancelled)
        {
            result.Status.Should().Be(JobExternalReadStatus.Refused);
            result.Handle.Should().BeNull();
            entered.Task.IsCompleted.Should().BeFalse();
        }
        else
        {
            result.Status.Should().Be(JobExternalReadStatus.Completed);
            result.Handle.Should().NotBeNull("a real ACK and clean read can still describe history after stop");
            Action continueStopped = () => _ = result.Handle!.Require(original);
            continueStopped.Should().Throw<InvalidOperationException>("a historical read cannot reopen its stopped run");
        }
    }
}
