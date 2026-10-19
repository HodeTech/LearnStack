using FluentAssertions;
using FluentAssertions.Execution;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_Reconcile_entry_requires_its_fresh_recovery_window_after_an_expired_original_and_held_constructor(bool expire)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var resource = Guid.CreateVersion7();
        await using var originalHost = Fixture.Host(database, provider.Settings, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(new BackgroundJobRuntimeOptions { ExternalWaitTimeout = TimeSpan.FromSeconds(4) });
        });
        var original = await AdmitPrepareAndArmAsync(database, originalHost, resource,
            OrganizationId.From(Guid.CreateVersion7()), OrganizationId.From(Guid.CreateVersion7()));
        var executed = await original.Executor.ExecuteExternalAsync(original.Read, original.Arm, CancellationToken.None);
        executed.Status.Should().Be(JobExternalExecutionStatus.Reported);
        executed.CleanupFailed.Should().BeFalse();
        executed.Observation.Should().NotBeNull();
        // Deliberately omit Outcome. The independent provider has the completed
        // effect; fresh recovery must keep the original operation/attempt/deadline.
        original.Receipt.Run.Close();
        await WaitForDatabaseLeaseAsync(database, original.Receipt.Deadline);
        await using (var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
            await SchemaQueries.ExecuteAsync(connection, null, """
                UPDATE background_jobs SET phase='reconciling',ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL,probe_after=clock_timestamp()
                WHERE id=@id AND ownership_until<=clock_timestamp()
                """, ("id", original.Receipt.Envelope.JobId.Value));

        var options = new BackgroundJobRuntimeOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(15),
            RenewalInterval = TimeSpan.FromSeconds(1),
            SqlTimeout = TimeSpan.FromSeconds(1),
            ProgressTimeout = TimeSpan.FromSeconds(30),
        };
        options.Validate();
        await using var controlSource = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(controlSource, Fixture.Registry, options, new SystemGuidFactory(), runtime);
        var acquired = await store.AcquireExternalRecoveryAsync(original.Receipt.Envelope.JobId,
            await GenerationAsync(database, original.Receipt.Envelope.JobId), original.Receipt.Envelope.Queue, CancellationToken.None);
        acquired.ThrowIfFailed();
        acquired.CommitAcknowledged.Should().BeTrue();
        acquired.CleanupConfirmed.Should().BeTrue();
        acquired.Value.Should().NotBeNull();
        var receipt = acquired.Value!;
        receipt.Deadline.Should().BeBefore(receipt.DbNow);
        receipt.AttemptId.Should().Be(original.Receipt.AttemptId);
        receipt.AdmissionToken.Should().Be(original.Receipt.Claim.Token);
        receipt.RecoveryToken.Should().NotBe(receipt.AdmissionToken);
        BackgroundJobExternalRecoveryBinding recovery;
        using (JobApplicationSourceOwner.Create().Enter())
            recovery = receipt.Bind(new BackgroundJobRunGate(runtime.RunStop));
        var construction = new BackgroundJobPositiveProviderConstructionProbe(hold: true);
        await using var recoveryHost = Fixture.Host(database, provider.Settings, construction);
        var executor = recoveryHost.Services.GetRequiredService<JobExecutor>();
        var read = await executor.ReadExternalStateAsync(recovery, CancellationToken.None);
        read.Handle.Should().NotBeNull();
        read.Handle!.Snapshot.ExecuteIntentRecorded.Should().BeTrue();
        read.Handle.Snapshot.OperationId.Should().Be(original.Operation);
        Task<JobExternalExecutionResult>? call = null;
        try
        {
            call = Task.Run(() => executor.ReconcileExternalAsync(recovery, read.Handle, CancellationToken.None));
            await construction.Entered.WaitAsync(TimeSpan.FromSeconds(20));
            call.IsCompleted.Should().BeFalse();
            (await provider.CountsAsync(original.Operation)).Should().Be((1L, 0L, 1L));
            if (expire) await WaitForDatabaseLeaseAsync(database, receipt.OwnershipUntil);
            recovery.Run.IsClosed.Should().BeFalse("no timer or cancellation hides the final fresh-window check");
            construction.Release();
            var result = await call.WaitAsync(TimeSpan.FromSeconds(20));
            using var assertions = new AssertionScope();
            result.Status.Should().Be(expire ? JobExternalExecutionStatus.EntryRefused : JobExternalExecutionStatus.Reported);
            result.CleanupFailed.Should().BeFalse();
            (result.Observation is not null).Should().Be(!expire);
            (await provider.CountsAsync(original.Operation)).Should().Be(expire ? (1L, 0L, 1L) : (1L, 1L, 1L));
            acquired.CommitAcknowledged.Should().BeTrue("fresh lease expiry never rewrites physical acquisition history");
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, receipt.JobId)).Should().Be(1);
            if (expire)
            {
                recovery.Run.IsClosed.Should().BeTrue("elapsed recovery authority closes the invocation permanently");
                recovery.Run.TryIssueExternalReconcile(recovery).Should().BeFalse();
            }
        }
        finally
        {
            construction.Release();
            if (call is not null) await call;
        }
    }
}
