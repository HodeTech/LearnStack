using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobControlTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_renewal_owner_counts_physical_ACK_separately_from_original_locked_history_without_reopening_permission(bool loseAcknowledgement)
    {
        await using var database = await DatabaseAsync();
        var bounds = new BackgroundJobRuntimeOptions
        {
            LeaseDuration = TimeSpan.FromMinutes(2),
            RenewalInterval = TimeSpan.FromSeconds(30),
            ProgressTimeout = TimeSpan.FromMinutes(3),
            ControlPoll = TimeSpan.FromMilliseconds(50),
        };
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe(), configure: services => services.AddSingleton(bounds));
        await using var proxy = new CommitResponseDroppingProxy(database.OutboxConnectionString,
            commitsToPass: int.MaxValue, lifetime: TimeSpan.FromMinutes(1));
        proxy.HoldNextRenewalCommitResponse(loseAcknowledgement);
        await using var source = Source(proxy.ConnectionString);
        var gate = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var generation = new HangfireGenerationWork(Fixture.Registry.Infrastructure.Pools["transactional"], gate);
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var controller = new BackgroundJobControl(source, Fixture.Registry, executor, gate, bounds,
            new SystemGuidFactory(), TimeProvider.System);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var claim = (await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, 1))!;
        var work = generation.TryBegin()!;
        var execution = controller.TryBegin(claim, work)!;
        work.HoldExecution(execution);
        var admission = executor.ExecutePublishedAsync(claim, execution.Publication, execution.AdmissionGate, null, work.Token);
        work.HoldAdmission(admission);
        execution.HoldAdmission(admission);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            (await admission.WaitAsync(deadline.Token)).CleanupFailed.Should().BeFalse();
            await execution.WaitForPublicationAsync().WaitAsync(deadline.Token);
            execution.RenewalPermitted.Should().BeTrue();
            controller.Start();
            await proxy.RenewalCommitHeld.WaitAsync(deadline.Token);
            controller.Snapshot.Renewals.Should().Be(0);
            controller.Snapshot.RenewalsConfirmed.Should().Be(0);
            var flight = (BackgroundJobRenewalOperation)typeof(BackgroundJobControl)
                .GetField("_renewalFlight", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(controller)!;
            flight.Completion.IsCompleted.Should().BeFalse();
            proxy.ReleaseRenewalCommitResponse();
            var actual = await flight.Completion.WaitAsync(deadline.Token);
            actual.CleanupConfirmed.Should().BeTrue();
            actual.CommitAcknowledged.Should().Be(!loseAcknowledgement);
            actual.CommitUncertain.Should().Be(loseAcknowledgement);
            actual.Probes.Should().ContainSingle().Which.Boundary.Should().Be(BackgroundJobCommitBoundary.Renewal);
            while (controller.Snapshot.Renewals != (loseAcknowledgement ? 0 : 1)
                || controller.Snapshot.RenewalsConfirmed != (loseAcknowledgement ? 1 : 0)
                || controller.Snapshot.PendingOutcomes != 0) await Task.Delay(10, deadline.Token);
            execution.RenewalPermitted.Should().Be(!loseAcknowledgement,
                "a clean history receipt cannot restore the private permission closed by an uncertain renewal");
            execution.RunGate.IsClosed.Should().Be(loseAcknowledgement);
            controller.Snapshot.Retries.Should().Be(0);
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
        }
        finally
        {
            proxy.ReleaseRenewalCommitResponse();
            gate.StopStarts();
            controller.RequestStop();
            await controller.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            var joined = await execution.JoinAsync(operationCleanupConfirmed: true);
            await work.CompleteAsync(joined);
            generation.CloseStarts();
            await generation.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

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
            host.Services.GetRequiredService<JobExecutor>(), gate, bounds, new SystemGuidFactory(), TimeProvider.System);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var claim = (await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, 1))!;
        var work = generation.TryBegin()!;
        var execution = controller.TryBegin(claim, work)!;
        work.HoldExecution(execution);
        var admission = host.Services.GetRequiredService<JobExecutor>().ExecutePublishedAsync(claim, execution.Publication, execution.AdmissionGate, null, work.Token);
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
