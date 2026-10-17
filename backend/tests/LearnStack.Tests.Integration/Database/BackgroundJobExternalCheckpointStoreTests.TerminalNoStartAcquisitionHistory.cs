using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData("request_lost")]
    [InlineData("expired")]
    [InlineData("replaced")]
    public async Task Terminal_acquisition_history_settles_exact_clean_never_bound_ownership_without_publishing_permission(string mode)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var host = NoPlanRefusalHost(database);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var options = new BackgroundJobRuntimeOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(20),
            RenewalInterval = TimeSpan.FromSeconds(1),
            ProgressTimeout = TimeSpan.FromSeconds(35),
        };
        options.Validate();
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, options, new SystemGuidFactory(), runtime);
        var normal = await NoPlanOrdinalAsync(database, host.Services, 1, store, runtime);
        var prepared = await host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(normal,
            normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare));
        prepared.KnownRollback.Should().BeTrue();
        prepared.CleanupFailed.Should().BeFalse();
        var request = await store.RequestNoPlanExternalTerminalAsync(BackgroundJobExecutionIdentity.FromReceipt(normal),
            BackgroundJobTerminalReason.PermanentFailure, CancellationToken.None);
        request.ThrowIfFailed();
        request.CommitAcknowledged.Should().BeTrue();
        normal.Run.Close();
        normal.SourceOwner.CloseIssuance();
        normal.SourceOwner.CleanupConfirmed.Should().BeTrue();

        await using var proxy = new CommitResponseDroppingProxy(database.OutboxConnectionString,
            dropCommitRequest: mode == "request_lost");
        await using var brokenSource = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(proxy.ConnectionString));
        var broken = new PostgresBackgroundJobControlStore(brokenSource, Fixture.Registry, options, new SystemGuidFactory(), runtime);
        var acquired = await broken.AcquireExternalTerminalNoStartAsync([], CancellationToken.None);
        acquired.CommitUncertain.Should().BeTrue();
        acquired.CommitAcknowledged.Should().BeFalse();
        acquired.CleanupConfirmed.Should().BeTrue();
        acquired.Value.Should().BeNull();
        var descriptor = acquired.Probes.Should().ContainSingle().Subject;
        if (mode != "request_lost")
        {
            var stored = await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, normal.Envelope.JobId);
            stored!.OwnershipToken.Should().Be(descriptor.RecoveryToken);
            await WaitForRecoveryClockAsync(database, stored.OwnershipUntil!.Value);
        }
        if (mode == "replaced")
        {
            var replacement = await store.AcquireExternalTerminalNoStartAsync([], CancellationToken.None);
            replacement.ThrowIfFailed();
            replacement.CleanupConfirmed.Should().BeTrue();
            replacement.Value!.RecoveryToken.Should().NotBe(descriptor.RecoveryToken!.Value);
            // Even this separate legitimate receipt is never bound in this test.
        }

        var live = await store.ProbeExternalRecoveryAsync(acquired, descriptor, CancellationToken.None);
        live.ThrowIfFailed();
        live.CleanupConfirmed.Should().BeTrue();
        live.Value.Should().BeNull();
        var before = await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, normal.Envelope.JobId);
        var history = await store.ProbeExternalTerminalNoStartAcquisitionAsync(acquired, descriptor, CancellationToken.None);
        history.ThrowIfFailed();
        history.CleanupConfirmed.Should().BeTrue();
        history.Value!.Disposition.Should().Be(mode == "expired"
            ? BackgroundJobProbeDisposition.ControlCommitted : BackgroundJobProbeDisposition.ChangedOwner);
        history.Value.CurrentRecoveryAuthority.Should().BeFalse();
        history.Value.RecoveryReceipt.Should().BeNull();
        history.CommitAcknowledged.Should().BeFalse();
        acquired.CommitAcknowledged.Should().BeFalse();
        acquired.Value.Should().BeNull("historical status does not mint another bindable receipt");
        (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, normal.Envelope.JobId)).Should().Be(before);
        Func<Task> copiedDescriptor = () => store.ProbeExternalTerminalNoStartAcquisitionAsync(acquired,
            descriptor with { }, CancellationToken.None);
        await copiedDescriptor.Should().ThrowAsync<ArgumentException>();
    }
}
