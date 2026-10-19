using System.Diagnostics;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData("arm", false)]
    [InlineData("arm", true)]
    [InlineData("outcome", false)]
    [InlineData("outcome", true)]
    [InlineData("recovery", false)]
    [InlineData("recovery", true)]
    public async Task Post_handler_commit_admission_cannot_borrow_an_expired_database_lease(string checkpoint, bool expire)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        var pause = new CheckpointCommitPause(checkpoint == "arm" ? typeof(RecordBackgroundJobArmCommand)
            : typeof(RecordBackgroundJobOutcomeCommand));
        await using var host = RecoveryHost(database, preparing: true, pause);
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        var prepared = await host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(original,
            original.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare));
        prepared.CommitAcknowledged.Should().BeTrue();
        var fingerprint = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("{\"ArtifactId\":\"" + RecoveryArtifact.ToString("D") + "\"}")));
        var operation = BackgroundJobExternalIdentity.OperationId(job, fingerprint);
        if (checkpoint != "arm")
        {
            var arm = original.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm);
            (await SendCheckpointAsync(host.Services, original,
                new RecordBackgroundJobArmCommand(original, arm, fingerprint, operation), arm)).IsSuccess.Should().BeTrue();
        }
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        BackgroundJobExternalRecoveryBinding? recovery = null;
        if (checkpoint == "recovery")
        {
            original.Run.Close();
            await using (var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
                await SchemaQueries.ExecuteAsync(dispatcher, null, """
                    UPDATE background_jobs SET ownership_until=clock_timestamp() WHERE id=@id;
                    UPDATE background_jobs SET phase='reconciling',ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL,probe_after=clock_timestamp()
                        WHERE id=@id AND ownership_until<=clock_timestamp()
                    """, ("id", job.Value));
            var acquired = await store.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job),
                original.Envelope.Queue, CancellationToken.None);
            acquired.ThrowIfFailed();
            recovery = BindRecovery(acquired, runtime);
        }
        if (expire)
        {
            await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            await SchemaQueries.ExecuteAsync(dispatcher, null,
                "UPDATE background_jobs SET ownership_until=clock_timestamp()+interval '5 seconds' WHERE id=@id", ("id", job.Value));
        }
        var run = recovery?.Run ?? original.Run;
        var gate = run.OpenBoundary(checkpoint == "arm" ? BackgroundJobCheckpointKind.Arm : BackgroundJobCheckpointKind.Outcome);
        var outcome = "{\"ArtifactId\":\"" + Guid.NewGuid().ToString("D") + "\"}";
        pause.Enabled = true;
        Task<Result<None>> running;
        if (checkpoint == "arm")
            running = SendCheckpointAsync(host.Services, original,
                new RecordBackgroundJobArmCommand(original, gate, fingerprint, operation), gate);
        else if (recovery is not null)
            running = SendRecoveryAsync(host.Services, recovery, new RecordBackgroundJobOutcomeCommand(recovery, gate,
                fingerprint, operation, outcome, BackgroundJobExternalState.Succeeded, null,
                Guid.NewGuid(), BackgroundJobQuiescenceKind.ProviderFenced), gate);
        else
            running = SendCheckpointAsync(host.Services, original, new RecordBackgroundJobOutcomeCommand(original, gate,
                fingerprint, operation, outcome, BackgroundJobExternalState.Succeeded, null,
                Guid.NewGuid(), BackgroundJobQuiescenceKind.ProviderFenced), gate);
        Exception? preconditionFailure = null;
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            running.IsCompleted.Should().BeFalse("the actual owner is parked after its handler, before COMMIT admission");
            if (expire) await WaitForStoredLeaseExpiryAsync(database, job);
        }
        catch (Exception failure) { preconditionFailure = failure; }
        finally { pause.Release.TrySetResult(); }
        Result<None>? result = null;
        var completionFailure = await Record.ExceptionAsync(async () => result = await running.WaitAsync(TimeSpan.FromSeconds(15)));
        preconditionFailure.Should().BeNull();
        running.IsCompleted.Should().BeTrue();
        if (expire) completionFailure.Should().BeOfType<OperationCanceledException>();
        else
        {
            completionFailure.Should().BeNull();
            result!.IsSuccess.Should().BeTrue();
        }
        gate.CommitAcknowledged.Should().Be(!expire);
        gate.CommitStarted.Should().Be(!expire);
        gate.KnownRollback.Should().Be(expire);
        run.IsClosed.Should().Be(expire, "lease expiry closes permission without any cancellation token signal");
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("""
            SELECT j.phase,j.normal_attempts,j.finalize_attempts,a.execute_intent_at IS NOT NULL,
                a.outcome,r.released_at IS NOT NULL
            FROM background_jobs j JOIN background_job_attempts a ON a.id=j.current_attempt_id
                JOIN background_job_reservations r ON r.attempt_id=a.id WHERE j.id=@id
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("id", job.Value);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        row.GetString(0).Should().Be(checkpoint == "arm" ? expire ? "prepared" : "executing"
            : expire ? recovery is null ? "executing" : "reconciling" : "finalize_pending");
        row.GetInt32(1).Should().Be(1);
        row.GetInt32(2).Should().Be(0);
        row.GetBoolean(3).Should().Be(checkpoint != "arm" || !expire);
        if (checkpoint == "arm" || expire) row.IsDBNull(4).Should().BeTrue();
        else row.GetString(4).Should().Be(outcome);
        row.GetBoolean(5).Should().Be(checkpoint != "arm" && !expire);
        (recovery?.SourceOwner ?? original.SourceOwner).CleanupConfirmed.Should().BeTrue();
    }

    private static async Task WaitForStoredLeaseExpiryAsync(DisposableSchemaDatabase database, BackgroundJobId job)
    {
        var started = Stopwatch.StartNew();
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        while (true)
        {
            await using var command = new NpgsqlCommand("SELECT clock_timestamp()>=ownership_until FROM background_jobs WHERE id=@id",
                (NpgsqlConnection)app, (NpgsqlTransaction)transaction)
            { CommandTimeout = 5 };
            command.Parameters.AddWithValue("id", job.Value);
            if (await command.ExecuteScalarAsync() is true) return;
            if (started.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("The actual checkpoint lease did not expire.");
            await Task.Delay(10);
        }
    }

    private sealed class CheckpointCommitPause(Type target)
    {
        internal Type Target { get; } = target;
        internal volatile bool Enabled;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class AfterCheckpointHandler<TRequest, TResponse>(CheckpointCommitPause pause)
        : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
    {
        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            var response = await next();
            if (pause.Enabled && typeof(TRequest) == pause.Target)
            {
                pause.Entered.TrySetResult();
                await pause.Release.Task;
            }
            return response;
        }
    }
}
