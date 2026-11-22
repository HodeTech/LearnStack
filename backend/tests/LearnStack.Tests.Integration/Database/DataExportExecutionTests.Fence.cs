using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.DataRequestSchemaSeed;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The export job's order of work (ADR-0107 § 2, § 5): the bundle is built and the retained references counted with
/// no fence held, and only then is the shared tenant fence taken and the request re-read before anything is written.
/// </summary>
public sealed partial class DataExportExecutionTests
{
    /// <summary>
    /// While a participant is still building the bundle, another transaction takes the Identity tenant fence
    /// exclusively within the two-second lock timeout every fenced operation runs under: the job holds no fence yet.
    /// The export then completes.
    /// </summary>
    [Fact]
    public async Task The_bundle_is_built_with_no_tenant_fence_held_so_an_exclusive_fence_is_granted_meanwhile()
    {
        await using var database = await OpenDatabaseAsync();
        var gate = new GateParticipant();
        await using var host = Host(services => services.AddSingleton<IUserDataParticipant>(gate));
        var request = await FileExportAsync(SchemaFixture.TenantA, null, PersonA, LearnerMembershipA);
        var job = await SubmitAsync(SchemaFixture.TenantA, null, request);

        var run = RunAsync(host.Services, job);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
            await using var app = await OpenAppAsync();
            await using var transaction = await app.BeginTransactionAsync();
            await ScopeAsync(app, transaction, SchemaFixture.TenantA);
            await ExecuteAsync(app, transaction, "SET LOCAL lock_timeout = '2s'");
            var waited = Stopwatch.StartNew();
            await LockAsync(app, transaction, SchemaFixture.TenantA, null, exclusive: true);
            waited.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "the job holds no tenant fence while it builds");
            run.IsCompleted.Should().BeFalse("the export is still building its bundle");
            (await ScalarAsync<string>(app, transaction, "SELECT status FROM public.data_subject_requests WHERE id=@id", ("id", request)))
                .Should().Be("requested", "nothing is written before the fence");
            await transaction.RollbackAsync();
        }
        finally
        {
            gate.Release.TrySetResult();
        }

        var result = await run;
        result.CommitAcknowledged.Should().BeTrue("disposition {0}, code {1}", result.Decision.Disposition, result.Decision.ErrorCode);
        (await RequestAsync(SchemaFixture.TenantA, null, request)).Status.Should().Be("completed");
    }

    /// <summary>
    /// A request cancelled while its bundle is being built is re-read under the fence and found cancelled: the run is a
    /// recorded no-op, the built bundle is discarded, nothing is stored and the earlier artifact stays available.
    /// </summary>
    [Fact]
    public async Task A_request_cancelled_while_its_bundle_is_built_is_a_recorded_no_op_and_the_bundle_is_discarded()
    {
        await using var database = await OpenDatabaseAsync();
        var gate = new GateParticipant();
        await using var host = Host(services => services.AddSingleton<IUserDataParticipant>(gate));
        var request = await FileExportAsync(SchemaFixture.TenantA, null, PersonA, LearnerMembershipA);
        var job = await SubmitAsync(SchemaFixture.TenantA, null, request);
        var before = await ArtifactsAsync(SchemaFixture.TenantA, null, PersonA);

        var run = RunAsync(host.Services, job);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
            await using var app = await OpenAppAsync();
            await using var transaction = await app.BeginTransactionAsync();
            await FenceAsync(app, transaction, SchemaFixture.TenantA, null, null);
            await StepAsync(app, transaction, request, "status='cancelled'");
            await transaction.CommitAsync();
        }
        finally
        {
            gate.Release.TrySetResult();
        }

        var result = await run;
        result.CommitAcknowledged.Should().BeTrue("disposition {0}, code {1}", result.Decision.Disposition, result.Decision.ErrorCode);
        (await RequestAsync(SchemaFixture.TenantA, null, request)).Status.Should().Be("cancelled");
        var after = await ArtifactsAsync(SchemaFixture.TenantA, null, PersonA);
        after.Should().BeEquivalentTo(before, "the built bundle is discarded: no artifact, no chunk, and no earlier artifact withdrawn");
        after.Single(artifact => artifact.Id == TenantWideA.Artifact!.Value).Status.Should().Be("available");
        (await AuditAsync(SchemaFixture.TenantA, null, "identity.data_request.fulfill_export", request.ToString("D")))
            .Should().Be(1, "the no-op still writes its MUST row");
    }

    /// <summary>
    /// A worker whose clock lags the host that filed the request still completes the export: its transitions are
    /// stamped no earlier than the request's own creation, which the aggregate and the request guard both require.
    /// </summary>
    [Fact]
    public async Task A_worker_clock_behind_the_request_still_completes_the_export()
    {
        await using var database = await OpenDatabaseAsync();
        await using var host = Host(services => services.AddSingleton<IClock>(new LaggingClock(TimeSpan.FromMinutes(3))));
        var request = await FileExportAsync(SchemaFixture.TenantA, null, PersonA, LearnerMembershipA);

        var result = await RunAsync(host.Services, await SubmitAsync(SchemaFixture.TenantA, null, request));

        result.CommitAcknowledged.Should().BeTrue("disposition {0}, code {1}", result.Decision.Disposition, result.Decision.ErrorCode);
        (await RequestAsync(SchemaFixture.TenantA, null, request)).Status.Should().Be("completed");
    }

    /// <summary>
    /// A participant read that fails in the database is the retryable <c>dependency_unavailable</c> the job runtime
    /// records, not an unexplained fault; the retry then completes.
    /// </summary>
    [Fact]
    public async Task A_participant_database_failure_is_retried_as_dependency_unavailable()
    {
        await using var database = await OpenDatabaseAsync();
        await using var host = Host(services => services.AddSingleton<IUserDataParticipant>(new UnavailableParticipant()));
        var request = await FileExportAsync(SchemaFixture.TenantA, null, PersonA, LearnerMembershipA);

        var failed = await RunAsync(host.Services, await SubmitAsync(SchemaFixture.TenantA, null, request));

        failed.CommitAcknowledged.Should().BeFalse();
        failed.Decision.ErrorCode.Should().Be("dependency_unavailable");
        (await RunAsync(host.Services, await SubmitAsync(SchemaFixture.TenantA, null, request))).CommitAcknowledged.Should().BeTrue();
        (await RequestAsync(SchemaFixture.TenantA, null, request)).Status.Should().Be("completed");
    }

    /// <summary>
    /// Standard 10: a purge attempt that throws is counted <c>failed</c> on the purge counter before its transaction
    /// rolls back for the runtime's retry.
    /// </summary>
    [Fact]
    public async Task A_purge_attempt_that_throws_is_counted_failed()
    {
        await using var database = await OpenDatabaseAsync();
        var measurements = new ConcurrentQueue<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meter) =>
            {
                if (instrument.Meter.Name == DataExportTelemetry.MeterName && instrument.Name == DataExportTelemetry.PurgeCounterName)
                {
                    meter.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                measurements.Enqueue(tag.Key + "=" + tag.Value);
            }
        });
        listener.Start();
        await using var host = Host(services => services.AddScoped<IDataExportArtifactStore, UnavailableArtifactStore>());
        var purge = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            "identity.data_export_purge_tenant.v1", tenant: SchemaFixture.TenantA, parameters: BackgroundJobPayloadContract.Serialize(
                new DataExportPurgeParameters(TenantWideA.Artifact!.Value), typeof(DataExportPurgeParameters), BackgroundJobPayloadKind.Parameters));

        var result = await RunAsync(host.Services, purge);

        result.CommitAcknowledged.Should().BeFalse();
        result.Decision.ErrorCode.Should().Be("dependency_unavailable");
        measurements.Should().Contain("outcome=failed");
    }

    /// <summary>Blocks inside the bundle build until the test releases it.</summary>
    private sealed class GateParticipant : IUserDataParticipant
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Key => "gate";

        public async Task ExportAsync(UserDataSubject subject, IUserDataExportWriter writer, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
        }

        public Task<IReadOnlyCollection<RetainedReferenceCount>> DescribeRetainedAsync(UserDataSubject subject,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyCollection<RetainedReferenceCount>>([]);
    }

    /// <summary>Fails its first export in the database driver, as a dropped connection or a statement timeout does.</summary>
    private sealed class UnavailableParticipant : IUserDataParticipant
    {
        private int _calls;

        public string Key => "unavailable";

        public Task ExportAsync(UserDataSubject subject, IUserDataExportWriter writer, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _calls) == 1
                ? throw new NpgsqlException("Synthetic participant read failure.")
                : Task.CompletedTask;

        public Task<IReadOnlyCollection<RetainedReferenceCount>> DescribeRetainedAsync(UserDataSubject subject,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyCollection<RetainedReferenceCount>>([]);
    }

    /// <summary>An artifact store whose every call is a storage failure.</summary>
    private sealed class UnavailableArtifactStore : IDataExportArtifactStore
    {
        public bool CanOpen(string keyVersion) => false;

        public Task<DataExportArtifactMetadata> PutAsync(DataExportArtifactPut put, IAsyncEnumerable<ReadOnlyMemory<byte>> plaintextChunks,
            CancellationToken cancellationToken) => throw Unavailable();

        public Task<DataExportArtifactMetadata?> ReadMetadataAsync(DataExportArtifactLocator artifact, CancellationToken cancellationToken) =>
            throw Unavailable();

        public Task<DataExportPlaintextChunk?> ReadChunkAsync(DataExportArtifactLocator artifact, int index, CancellationToken cancellationToken) =>
            throw Unavailable();

        public Task<DataExportArtifactStatus?> PurgeAsync(DataExportArtifactLocator artifact, DataExportArtifactStatus outcome,
            CancellationToken cancellationToken) => throw Unavailable();

        private static LearnStack.SharedKernel.Errors.InfrastructureException Unavailable() => new("Synthetic artifact storage outage.");
    }

    private sealed class LaggingClock(TimeSpan lag) : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow - lag;
    }
}
