using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobControlTests
{
    [Fact]
    public async Task Prebound_renewal_wait_expiry_retains_the_actual_held_COMMIT_and_late_unknown_cannot_reopen_permission()
    {
        await using var database = await DatabaseAsync();
        var bounds = new BackgroundJobRuntimeOptions { TransactionTimeout = TimeSpan.FromMinutes(2), RenewalInterval = TimeSpan.FromSeconds(1) };
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe(), configure: services => services.AddSingleton(bounds));
        await using var healthy = Source(database.OutboxConnectionString);
        var gate = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var generation = new HangfireGenerationWork(Fixture.Registry.Infrastructure.Pools["transactional"], gate);
        var controller = new BackgroundJobControl(healthy, Fixture.Registry,
            host.Services.GetRequiredService<IServiceScopeFactory>(), gate, bounds, new SystemGuidFactory(), TimeProvider.System);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var claim = (await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, 1))!;
        var work = generation.TryBegin()!;
        var execution = controller.TryBegin(claim, work)!;
        work.HoldExecution(execution);
        var admission = host.Services.GetRequiredService<JobExecutor>().ExecutePublishedAsync(claim, execution.Publication, null, work.Token);
        work.HoldAdmission(admission);
        execution.HoldAdmission(admission);
        var started = await admission;
        started.CleanupFailed.Should().BeFalse();
        await execution.WaitForPublicationAsync();
        execution.Identity.Should().NotBeNull();
        execution.RenewalPermitted.Should().BeTrue();

        await using var proxy = new CommitResponseDroppingProxy(database.OutboxConnectionString, holdCommitResponse: true);
        await using var faulty = Source(proxy.ConnectionString);
        var operation = new BackgroundJobRenewalOperation(Control(faulty, options: bounds), [execution], bounds, TimeProvider.System, CancellationToken.None);
        operation.Start();
        try
        {
            await proxy.CommitResponseHeld.WaitAsync(TimeSpan.FromSeconds(5));
            operation.Completion.IsCompleted.Should().BeFalse("the real server response is still held behind the proxy");
            (await operation.TimelyReturn.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeFalse();
            operation.Completion.IsCompleted.Should().BeFalse(
                "the finite observation ends before Npgsql's independently bounded cancellation/actual connection cleanup");
            // This is the production renewal loop's fixed-memory action after
            // TimelyReturn=false; observing the wait does not dispose its task.
            execution.RefuseRenewal();
            execution.Gate.IsClosed.Should().BeTrue();
            execution.RenewalPermitted.Should().BeFalse();
            proxy.ReleaseHeldResponse();
            var actual = await operation.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            actual.CommitUncertain.Should().BeTrue();
            actual.CleanupConfirmed.Should().BeTrue();
            actual.Value.Should().BeNull("a late lost COMMIT acknowledgement publishes no renewal authority");
            var resolved = await Control(healthy).ProbeCommitAsync(actual.Probes.Single());
            resolved.ThrowIfFailed();
            resolved.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.Renewed);
            execution.RenewalPermitted.Should().BeFalse("durable reconciliation cannot reopen a closed private gate");

            // Only now is the original actual operation positively cleaned; a
            // sequential replacement needs the same single physical lane.
            var replacement = new BackgroundJobRenewalOperation(Control(healthy, options: bounds), [execution], bounds, TimeProvider.System, CancellationToken.None);
            replacement.Start();
            (await replacement.TimelyReturn.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
            var replacementResult = await replacement.Completion;
            replacementResult.ThrowIfFailed();
            replacementResult.CleanupConfirmed.Should().BeTrue();
            execution.Gate.IsClosed.Should().BeTrue();
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
        }
        finally
        {
            proxy.ReleaseHeldResponse();
            try { await operation.Completion.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally
            {
                execution.RefuseRenewal();
                var joined = await execution.JoinAsync(operationCleanupConfirmed: true);
                await work.CompleteAsync(joined);
                generation.CloseStarts();
                gate.StopStarts();
                controller.RequestStop();
                await controller.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                joined.Should().BeTrue();
                await generation.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
