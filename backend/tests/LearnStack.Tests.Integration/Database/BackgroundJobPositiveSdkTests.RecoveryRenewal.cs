using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_acknowledged_recovery_renewal_extends_live_method_entry_but_cannot_resurrect_a_spent_window(bool late)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        await using var host = Fixture.Host(database, provider.Settings, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(new BackgroundJobRuntimeOptions { ExternalWaitTimeout = TimeSpan.FromSeconds(4) });
        });
        var flow = await AdmitPrepareAndArmAsync(database, host, Guid.CreateVersion7(),
            OrganizationId.From(Guid.CreateVersion7()), OrganizationId.From(Guid.CreateVersion7()));
        var executed = await flow.Executor.ExecuteExternalAsync(flow.Read, flow.Arm, CancellationToken.None);
        executed.Status.Should().Be(JobExternalExecutionStatus.Reported);
        flow.Receipt.Run.Close();
        await WaitForDatabaseLeaseAsync(database, flow.Receipt.Deadline);
        await using (var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
            await SchemaQueries.ExecuteAsync(connection, null, """
                UPDATE background_jobs SET phase='reconciling',ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL,probe_after=clock_timestamp()
                WHERE id=@id AND ownership_until<=clock_timestamp()
                """, ("id", flow.Receipt.Envelope.JobId.Value));
        var options = new BackgroundJobRuntimeOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(15),
            RenewalInterval = TimeSpan.FromSeconds(1),
            SqlTimeout = TimeSpan.FromSeconds(1),
            ProgressTimeout = TimeSpan.FromSeconds(30),
        };
        options.Validate();
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, options, new SystemGuidFactory(), runtime);
        var acquired = await store.AcquireExternalRecoveryAsync(flow.Receipt.Envelope.JobId,
            await GenerationAsync(database, flow.Receipt.Envelope.JobId), flow.Receipt.Envelope.Queue, CancellationToken.None);
        acquired.ThrowIfFailed();
        acquired.Value.Should().NotBeNull();
        var receipt = acquired.Value!;
        BackgroundJobExternalRecoveryBinding recovery;
        using (JobApplicationSourceOwner.Create().Enter())
            recovery = receipt.Bind(new BackgroundJobRunGate(runtime.RunStop));
        var construction = new BackgroundJobPositiveProviderConstructionProbe(hold: true);
        await using var recoveryHost = Fixture.Host(database, provider.Settings, construction);
        var executor = recoveryHost.Services.GetRequiredService<JobExecutor>();
        var read = await executor.ReadExternalStateAsync(recovery, CancellationToken.None);
        read.Handle.Should().NotBeNull();
        Task<JobExternalExecutionResult>? call = null;
        try
        {
            call = Task.Run(() => executor.ReconcileExternalAsync(recovery, read.Handle!, CancellationToken.None));
            await construction.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForDatabaseLeaseAsync(database, receipt.OwnershipUntil.AddSeconds(-10));
            var renewed = await store.RenewBatchAsync([BackgroundJobExecutionIdentity.FromRecovery(receipt)]);
            renewed.ThrowIfFailed();
            renewed.CleanupConfirmed.Should().BeTrue();
            renewed.CommitAcknowledged.Should().BeTrue();
            renewed.Value.Should().ContainSingle();
            var row = renewed.Value!.Single();
            row.RecoveryLease.Should().NotBeNull();
            row.OwnershipUntil.Should().BeAfter(receipt.OwnershipUntil);
            if (late) await WaitForDatabaseLeaseAsync(database, receipt.OwnershipUntil);
            recovery.Run.TryAcceptExternalRecoveryRenewal(recovery, row.RecoveryLease!).Should().Be(!late);
            await WaitForDatabaseLeaseAsync(database, receipt.OwnershipUntil);
            construction.Release();
            var result = await call.WaitAsync(TimeSpan.FromSeconds(10));
            result.Status.Should().Be(late ? JobExternalExecutionStatus.EntryRefused : JobExternalExecutionStatus.Reported);
            result.CleanupFailed.Should().BeFalse();
            (await provider.CountsAsync(flow.Operation)).Should().Be(late ? (1L, 0L, 1L) : (1L, 1L, 1L));
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, receipt.JobId)).Should().Be(1);
            if (late) recovery.Run.IsClosed.Should().BeTrue();
            acquired.CommitAcknowledged.Should().BeTrue();
            renewed.CommitAcknowledged.Should().BeTrue("rejecting late authority never erases actual durable renewal history");
        }
        finally
        {
            construction.Release();
            if (call is not null) await call;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_recovery_renewal_preserves_original_expired_ledger_and_uses_only_its_fresh_owner(bool loseAck)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        await using var host = Fixture.Host(database, provider.Settings, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(new BackgroundJobRuntimeOptions { ExternalWaitTimeout = TimeSpan.FromSeconds(4) });
        });
        var flow = await AdmitPrepareAndArmAsync(database, host, Guid.CreateVersion7(),
            OrganizationId.From(Guid.CreateVersion7()), OrganizationId.From(Guid.CreateVersion7()));
        var executed = await flow.Executor.ExecuteExternalAsync(flow.Read, flow.Arm, CancellationToken.None);
        executed.Status.Should().Be(JobExternalExecutionStatus.Reported);
        flow.Receipt.Run.Close();
        await WaitForDatabaseLeaseAsync(database, flow.Receipt.Deadline);
        await using (var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
            await SchemaQueries.ExecuteAsync(connection, null, """
                UPDATE background_jobs SET phase='reconciling',ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL,probe_after=clock_timestamp()
                WHERE id=@id AND ownership_until<=clock_timestamp()
                """, ("id", flow.Receipt.Envelope.JobId.Value));
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var options = new BackgroundJobRuntimeOptions();
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, options, new SystemGuidFactory(), runtime);
        var acquired = await store.AcquireExternalRecoveryAsync(flow.Receipt.Envelope.JobId,
            await GenerationAsync(database, flow.Receipt.Envelope.JobId), flow.Receipt.Envelope.Queue, CancellationToken.None);
        acquired.ThrowIfFailed();
        acquired.Value.Should().NotBeNull();
        var receipt = acquired.Value!;
        receipt.Deadline.Should().BeBefore(receipt.DbNow);
        BackgroundJobExternalRecoveryBinding recovery;
        using (JobApplicationSourceOwner.Create().Enter())
            recovery = receipt.Bind(new BackgroundJobRunGate(runtime.RunStop));
        var identity = BackgroundJobExecutionIdentity.FromRecovery(receipt);
        identity.IsRecovery.Should().BeTrue();
        identity.Token.Should().Be(receipt.RecoveryToken).And.NotBe(receipt.AdmissionToken);
        identity.AdmissionToken.Should().Be(flow.Receipt.Claim.Token);
        await using var normalHost = Fixture.Host(database, provider.Settings, configure: services =>
        {
            services.RemoveAll<ITenantContextAccessor>();
            services.AddSingleton<ITenantContextAccessor>(new BackgroundJobFixtureAccessor(new BackgroundJobFixtureContext(tenant: SchemaFixture.TenantB)));
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(new BackgroundJobRuntimeOptions { ExternalWaitTimeout = TimeSpan.FromSeconds(4) });
        });
        var submitted = await Fixture.SubmitAsync(normalHost.Services, Fixture.Parameters(Guid.CreateVersion7(),
            OrganizationId.From(Guid.CreateVersion7()), OrganizationId.From(Guid.CreateVersion7())));
        submitted.IsSuccess.Should().BeTrue();
        var normalClaim = await normalHost.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>()
            .TryClaimAsync(submitted.Value, await GenerationAsync(database, submitted.Value, SchemaFixture.TenantB));
        normalClaim.Should().NotBeNull();
        var normalStart = await normalHost.Services.GetRequiredService<JobExecutor>()
            .ExecuteAsync(normalClaim!, BackgroundJobTestRun.Create());
        normalStart.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        normalStart.CleanupFailed.Should().BeFalse();
        var normal = BackgroundJobExecutionIdentity.FromReceipt(normalStart.Receipt!);
        await using var proxy = loseAck ? new CommitResponseDroppingProxy(database.OutboxConnectionString) : null;
        await using var renewalSource = new PrivilegedDataSource(() =>
            OutboxDispatcherDataSource.Build(proxy?.ConnectionString ?? database.OutboxConnectionString));
        var renewalStore = new PostgresBackgroundJobControlStore(renewalSource, Fixture.Registry, options, new SystemGuidFactory(), runtime);
        var renewed = await renewalStore.RenewBatchAsync([identity, normal]);
        renewed.CleanupConfirmed.Should().BeTrue();
        if (loseAck)
        {
            proxy!.DroppedCommitResponses.Should().Be(1);
            renewed.Failed.Should().BeTrue();
            renewed.CommitUncertain.Should().BeTrue();
            renewed.CommitAcknowledged.Should().BeFalse();
            renewed.Value.Should().BeNull("unknown COMMIT cannot publish a usable recovery renewal witness");
            var historical = await store.ProbeCommitAsync(renewed.Probes.Single(item => item.IsRecovery));
            historical.ThrowIfFailed();
            historical.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.Renewed);
            renewed.Value.Should().BeNull("a status-only durability probe cannot mint the missing lease evidence");
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, receipt.JobId)).Should().Be(1);
            (await provider.CountsAsync(flow.Operation)).Should().Be((1L, 0L, 1L));
            return;
        }
        renewed.ThrowIfFailed();
        renewed.CommitAcknowledged.Should().BeTrue();
        renewed.Value.Should().HaveCount(2, "a mixed batch preserves distinct live execution and recovery policies");
        var rows = renewed.Value!;
        var row = rows.Single(item => ReferenceEquals(item.Identity, identity));
        var ordinary = rows.Single(item => ReferenceEquals(item.Identity, normal));
        ordinary.OwnershipUntil.Should().Be(normal.Deadline, "ordinary execution remains capped by its immutable deadline");
        ordinary.RecoveryLease.Should().BeNull("ordinary data cannot become recovery evidence");
        row.Identity.Should().BeSameAs(identity);
        row.OwnershipUntil.Should().BeAfter(receipt.OwnershipUntil).And.BeAfter(receipt.Deadline);
        row.DbNow.Should().BeAfter(receipt.DbNow);
        var probe = renewed.Probes.Single(item => item.IsRecovery);
        var confirmed = await store.ProbeCommitAsync(probe);
        confirmed.ThrowIfFailed();
        confirmed.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.Renewed);
        foreach (var wrong in new[]
        {
            probe with { IsRecovery = false },
            probe with { OriginalAdmissionToken = probe.Token },
            probe with { OriginalAdmissionToken = Guid.NewGuid() },
            probe with { LedgerStage = BackgroundJobLedgerStage.Finalize },
            probe with { Deadline = probe.Deadline!.Value.AddTicks(10) },
        })
        {
            var rejected = await store.ProbeCommitAsync(wrong);
            rejected.ThrowIfFailed();
            rejected.Value!.Disposition.Should().Be(BackgroundJobProbeDisposition.ChangedOwner);
        }
        row.RecoveryLease.Should().NotBeNull();
        recovery.Run.TryAcceptExternalRecoveryRenewal(recovery, row.RecoveryLease!).Should().BeTrue();
        recovery.Run.TryAcceptExternalRecoveryRenewal(recovery, row.RecoveryLease!).Should().BeFalse("one witness cannot refresh its source observation twice");
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, receipt.JobId)).Should().Be(1);
        (await provider.CountsAsync(flow.Operation)).Should().Be((1L, 0L, 1L));
        recovery.Run.IsClosed.Should().BeFalse();
    }
}
