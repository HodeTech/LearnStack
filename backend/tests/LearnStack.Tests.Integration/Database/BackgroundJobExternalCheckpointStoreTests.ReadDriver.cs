using System.Data;
using System.Data.Common;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Read_driver_retains_actual_scope_cleanup_and_never_publishes_a_handle_from_failed_cleanup(
        bool freshRecovery, bool throwOnCleanup)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        var probe = new ReadDriverCleanupProbe(throwOnCleanup);
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe(), configure: services =>
        {
            services.AddSingleton(new FixtureAdmissionWindow(freshRecovery ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromMinutes(4)));
            services.RemoveAll<IRequestHandler<AdmitBackgroundJobAttemptCommand, Result<None>>>();
            services.AddTransient<IRequestHandler<AdmitBackgroundJobAttemptCommand, Result<None>>, FixtureAdmissionHandler>();
            services.AddSingleton(probe);
            services.AddScoped<ReadDriverHeldCleanup>();
            services.AddTransient<IPipelineBehavior<ReadBackgroundJobExternalStateQuery, Result<BackgroundJobExternalSnapshot>>,
                ReadDriverResolveCleanup>();
        });
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        BackgroundJobExternalRecoveryBinding? recovery = null;
        if (freshRecovery)
        {
            await ParkExpiredExternalAsync(database, original);
            var acquired = await store.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job),
                original.Envelope.Queue, CancellationToken.None);
            acquired.ThrowIfFailed();
            recovery = BindRecovery(acquired, runtime);
        }
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var actual = recovery is null ? executor.ReadExternalStateAsync(original, CancellationToken.None)
            : executor.ReadExternalStateAsync(recovery, CancellationToken.None);
        Exception? preconditionFailure = null;
        try
        {
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            probe.Request.Should().NotBeNull();
            probe.Request!.CommitAcknowledged.Should().BeTrue("the actual read-only COMMIT precedes this real DI scope cleanup");
            probe.Request.RecordedSnapshot.Should().NotBeNull();
            actual.IsCompleted.Should().BeFalse("neither a result nor a handle is published while the actual scope is held");
            probe.Request.Authority.SourceOwner.CleanupConfirmed.Should().BeFalse();
        }
        catch (Exception failure) { preconditionFailure = failure; }
        finally { probe.Release.TrySetResult(); }
        var result = await actual.WaitAsync(TimeSpan.FromSeconds(15));
        preconditionFailure.Should().BeNull();
        result.Request.Should().BeSameAs(probe.Request);
        result.Status.Should().Be(JobExternalReadStatus.Completed);
        result.CommitAcknowledged.Should().BeTrue("a cleanup fault cannot erase the physical read acknowledgement");
        result.CleanupFailed.Should().Be(throwOnCleanup);
        probe.Connection!.State.Should().Be(ConnectionState.Closed, "fallback awaits the exact unit if DI stopped at a throwing service");
        result.SourceOwner.CleanupConfirmed.Should().BeTrue("the actual main unit cleaned, independently of the failed DI scope witness");
        if (throwOnCleanup)
        {
            result.Handle.Should().BeNull();
            result.RetainedCleanup.Should().Contain(owner => ReferenceEquals(owner.Unit, probe.Unit) && owner.Scope != null);
            result.Request.Authority.Run.IsClosed.Should().BeTrue();
            Action reuse = () => _ = result.SourceOwner.Enter();
            reuse.Should().Throw<InvalidOperationException>();
        }
        else
        {
            result.RetainedCleanup.Should().BeEmpty();
            result.Handle.Should().NotBeNull();
            var snapshot = recovery is null ? result.Handle!.Require(original) : result.Handle!.Require(recovery);
            snapshot.Should().BeSameAs(result.Request.RecordedSnapshot);
            snapshot.Envelope.Should().Be(original.Envelope);
        }
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
    }

    private sealed class ReadDriverCleanupProbe(bool throws)
    {
        internal bool Throws { get; } = throws;
        internal ReadBackgroundJobExternalStateQuery? Request { get; set; }
        internal IUnitOfWork? Unit { get; set; }
        internal DbConnection? Connection { get; set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ReadDriverHeldCleanup(ReadDriverCleanupProbe probe) : IAsyncDisposable
    {
        internal void Observe(ReadBackgroundJobExternalStateQuery request, IUnitOfWork unit)
        {
            probe.Request = request;
            probe.Unit = unit;
        }

        public async ValueTask DisposeAsync()
        {
            probe.Connection = probe.Unit!.Connection;
            probe.Entered.TrySetResult();
            await probe.Release.Task;
            if (probe.Throws) throw new InvalidOperationException("Explicit read scope cleanup fault.");
        }
    }

    private sealed class ReadDriverResolveCleanup(ReadDriverHeldCleanup cleanup, IUnitOfWork unit)
        : IPipelineBehavior<ReadBackgroundJobExternalStateQuery, Result<BackgroundJobExternalSnapshot>>
    {
        public Task<Result<BackgroundJobExternalSnapshot>> Handle(ReadBackgroundJobExternalStateQuery request,
            RequestHandlerDelegate<Result<BackgroundJobExternalSnapshot>> next, CancellationToken cancellationToken)
        {
            cleanup.Observe(request, unit);
            return next();
        }
    }
}
