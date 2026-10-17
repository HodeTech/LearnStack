using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData("actual", false)]
    [InlineData("missing", false)]
    [InlineData("clone", false)]
    [InlineData("actual", true)]
    [InlineData("missing", true)]
    [InlineData("clone", true)]
    public async Task Read_acknowledgement_requires_the_exact_candidate_recorded_by_its_actual_read_only_owner(string substitution, bool driver)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        var probe = new ReadCandidateSubstitution(substitution);
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe(), configure: services =>
        {
            services.AddSingleton(new FixtureAdmissionWindow(TimeSpan.FromMilliseconds(500)));
            services.RemoveAll<IRequestHandler<AdmitBackgroundJobAttemptCommand, Result<None>>>();
            services.AddTransient<IRequestHandler<AdmitBackgroundJobAttemptCommand, Result<None>>, FixtureAdmissionHandler>();
            services.AddSingleton(probe);
            services.AddTransient<IPipelineBehavior<ReadBackgroundJobExternalStateQuery, Result<BackgroundJobExternalSnapshot>>,
                SubstituteReadCandidate>();
        });
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        probe.Envelope = original.Envelope;
        await ParkExpiredExternalAsync(database, original);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var acquired = await store.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job),
            original.Envelope.Queue, CancellationToken.None);
        acquired.ThrowIfFailed();
        var recovery = BindRecovery(acquired, runtime);
        if (driver)
        {
            var result = await host.Services.GetRequiredService<JobExecutor>().ReadExternalStateAsync(recovery, CancellationToken.None);
            result.CleanupFailed.Should().BeFalse();
            result.RetainedCleanup.Should().BeEmpty();
            if (substitution == "actual")
            {
                result.Status.Should().Be(JobExternalReadStatus.Completed);
                result.Handle.Should().NotBeNull();
                result.Handle!.Require(recovery).Should().BeSameAs(result.Request.RecordedSnapshot);
                result.Handle.Snapshot.Envelope.Should().Be(original.Envelope);
                result.CommitAcknowledged.Should().BeTrue();
            }
            else
            {
                result.Status.Should().Be(JobExternalReadStatus.Failed);
                result.CommitAcknowledged.Should().BeFalse();
                result.Handle.Should().BeNull();
            }
        }
        else
        {
            var request = new ReadBackgroundJobExternalStateQuery(recovery);
            var execute = () => SendRecoveryAsync(host.Services, recovery, request, null);
            if (substitution == "actual")
            {
                var response = await execute();
                response.IsSuccess.Should().BeTrue();
                response.Value.Should().BeSameAs(request.RecordedSnapshot);
                request.RecordedSnapshot!.Envelope.Should().Be(original.Envelope);
                request.CommitAcknowledged.Should().BeTrue();
            }
            else
            {
                await execute.Should().ThrowAsync<InvalidOperationException>();
                request.CommitAcknowledged.Should().BeFalse("an unrelated Result.Ok cannot mint a positive read acknowledgement");
                if (substitution == "missing") request.RecordedSnapshot.Should().BeNull();
                else request.RecordedSnapshot.Should().NotBeNull("the real candidate exists, but the returned clone is a different object");
            }
        }
        probe.ReadOnlyVerified.Should().BeTrue();
        recovery.SourceOwner.CleanupConfirmed.Should().BeTrue();
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
    }

    private sealed class ReadCandidateSubstitution(string mode)
    {
        internal string Mode { get; } = mode;
        internal BackgroundJobEnvelope? Envelope { get; set; }
        internal bool ReadOnlyVerified { get; set; }
    }

    private sealed class SubstituteReadCandidate(ReadCandidateSubstitution probe, IUnitOfWork unit)
        : IPipelineBehavior<ReadBackgroundJobExternalStateQuery, Result<BackgroundJobExternalSnapshot>>
    {
        public async Task<Result<BackgroundJobExternalSnapshot>> Handle(ReadBackgroundJobExternalStateQuery request,
            RequestHandlerDelegate<Result<BackgroundJobExternalSnapshot>> next, CancellationToken cancellationToken)
        {
            unit.Mode.Should().Be(TransactionMode.ReadOnly);
            await SchemaQueries.AssertApplicationRoleAsync(unit.Connection, unit.Transaction!);
            await using (var command = new NpgsqlCommand("SHOW transaction_read_only", (NpgsqlConnection)unit.Connection,
                (NpgsqlTransaction)unit.Transaction!))
                (await command.ExecuteScalarAsync(cancellationToken)).Should().Be("on");
            probe.ReadOnlyVerified = true;
            if (probe.Mode == "missing")
                return Result<BackgroundJobExternalSnapshot>.Ok(new(null, null, null, null, null, null) { Envelope = probe.Envelope });
            var response = await next();
            response.IsSuccess.Should().BeTrue();
            return probe.Mode == "clone" ? Result<BackgroundJobExternalSnapshot>.Ok(response.Value! with { }) : response;
        }
    }
}
