using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    [Fact]
    public async Task External_capacity_refusal_is_durably_deferred_by_the_actual_control_owner_without_spending_an_attempt()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var bounds = new BackgroundJobRuntimeOptions { RecoveryProbeInterval = TimeSpan.FromSeconds(5) };
        await using var host = Fixture.Host(database, provider.Settings, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
        });
        var first = await AdmitPrepareAndArmAsync(database, host, Guid.CreateVersion7(),
            OrganizationId.From(Guid.CreateVersion7()), OrganizationId.From(Guid.CreateVersion7()));
        var submitted = await Fixture.SubmitAsync(host.Services, Fixture.Parameters(Guid.CreateVersion7(),
            OrganizationId.From(Guid.CreateVersion7()), OrganizationId.From(Guid.CreateVersion7())));
        submitted.IsSuccess.Should().BeTrue();
        var job = submitted.Value;
        var routing = host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>();
        var claim = await routing.TryClaimAsync(job, await GenerationAsync(database, job));
        claim.Should().NotBeNull();
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var controller = new BackgroundJobControl(source, Fixture.Registry, first.Executor, runtime, bounds,
            new SystemGuidFactory(), TimeProvider.System);
        var generation = new HangfireGenerationWork(Fixture.Registry.Infrastructure.Pools["external"], runtime);
        var work = generation.TryBegin()!;
        var execution = controller.TryBegin(claim!, work)!;
        work.HoldExecution(execution);
        try
        {
            var admission = first.Executor.ExecutePublishedAsync(claim!, execution.Publication,
                execution.AdmissionGate, null, work.Token);
            work.HoldAdmission(admission);
            execution.HoldAdmission(admission);
            var refused = await admission;
            refused.Outcome.Should().Be(JobStartExecutionOutcome.CapacityDeferred);
            refused.CleanupFailed.Should().BeFalse();
            refused.Receipt.Should().BeNull();
            await execution.WaitForPublicationAsync();
            execution.Identity.Should().BeNull();
            var before = await CapacityDeferralFactsAsync(database, job);
            before.Owner.Should().Be(claim!.Token);
            before.Attempts.Should().Be(0);
            before.Reservations.Should().Be(0);
            var completion = controller.FinishAdmissionAsync(execution, refused);
            controller.Start();
            (await completion.WaitAsync(TimeSpan.FromSeconds(15))).Should().BeTrue();
            var deferred = await CapacityDeferralFactsAsync(database, job);
            deferred.Owner.Should().BeNull("the actual capacity disposition must release this exact routing lease");
            deferred.OwnerKind.Should().BeNull();
            deferred.OwnerUntil.Should().BeNull();
            deferred.Generation.Should().Be(before.Generation);
            deferred.Attempts.Should().Be(0);
            deferred.Reservations.Should().Be(0);
            deferred.NormalAttempts.Should().Be(0);
            deferred.Error.Should().Be("capacity_unavailable");
            deferred.AvailableAfter.Should().BeOnOrAfter(before.DbNow + bounds.RecoveryProbeInterval);
            deferred.AvailableAfter.Should().BeOnOrBefore(deferred.DbNow + bounds.RecoveryProbeInterval);
            deferred.HandoffCheck.Should().NotBeNull();
            (await routing.TryClaimAsync(job, deferred.Generation)).Should().BeNull("the recorded capacity wait is still live");
            (await provider.CountsAsync(first.Operation)).Should().Be((0L, 0L, 0L));

            // Capacity is freed by the actual provider report and app-role Outcome,
            // without editing the reservation or spending a second first-job attempt.
            var report = await first.Executor.ExecuteExternalAsync(first.Read, first.Arm, CancellationToken.None);
            report.CleanupFailed.Should().BeFalse();
            var recorded = await first.Executor.RecordExternalOutcomeAsync(first.Receipt, first.Read,
                first.Receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Outcome), report.Observation!, CancellationToken.None);
            recorded.CommitAcknowledged.Should().BeTrue();
            recorded.CleanupFailed.Should().BeFalse();
            using var due = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            BackgroundJobRoutingClaim? next;
            do
            {
                next = await routing.TryClaimAsync(job, deferred.Generation, due.Token);
                if (next is null) await Task.Delay(20, due.Token);
            } while (next is null);
            next.DbNow.Should().BeOnOrAfter(deferred.AvailableAfter);
            next.DbNow.Should().BeBefore(claim.OwnershipUntil,
                "the former routing lease must not postpone this configured capacity wait");
            next.Token.Should().NotBe(claim.Token);

            // A stale original token cannot clear the newly acquired route.
            var stale = await new PostgresBackgroundJobControlStore(source, Fixture.Registry, bounds,
                new SystemGuidFactory(), runtime).DeferCapacityAsync(claim, CancellationToken.None);
            stale.ThrowIfFailed();
            stale.Value.Should().BeNull();
            stale.CommitAcknowledged.Should().BeFalse();
            (await CapacityDeferralFactsAsync(database, job)).Owner.Should().Be(next.Token);
            var admitted = await first.Executor.ExecuteAsync(next, BackgroundJobTestRun.Create());
            admitted.Outcome.Should().Be(JobStartExecutionOutcome.Started);
            admitted.CleanupFailed.Should().BeFalse();
            admitted.Receipt!.NormalAttemptOrdinal.Should().Be(1);
            var after = await CapacityDeferralFactsAsync(database, job);
            after.Attempts.Should().Be(1);
            after.Reservations.Should().Be(1);
            after.NormalAttempts.Should().Be(1);
            admitted.Receipt.Run.Close();
            admitted.SourceOwner.CloseIssuance();
        }
        finally
        {
            first.Receipt.Run.Close();
            first.Receipt.SourceOwner.CloseIssuance();
            var joined = await execution.JoinAsync(operationCleanupConfirmed: true);
            await work.CompleteAsync(joined);
            generation.CloseStarts();
            controller.RequestStop();
            await controller.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            joined.Should().BeTrue();
            await generation.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        }
        controller.Snapshot.Executions.Should().Be(0);
    }

    private sealed record CapacityDeferralFacts(long Generation, Guid? Owner, string? OwnerKind,
        DateTimeOffset? OwnerUntil, DateTimeOffset AvailableAfter, DateTimeOffset? HandoffCheck,
        string? Error, int NormalAttempts, long Attempts, long Reservations, DateTimeOffset DbNow);

    private static async Task<CapacityDeferralFacts> CapacityDeferralFactsAsync(DisposableSchemaDatabase database,
        LearnStack.SharedKernel.Jobs.BackgroundJobId job)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("""
            SELECT j.wake_generation,j.ownership_token,j.ownership_kind,j.ownership_until,j.available_after,
                j.handoff_check_at,j.last_error,j.normal_attempts,
                (SELECT count(*) FROM background_job_attempts a WHERE a.job_id=j.id),
                (SELECT count(*) FROM background_job_reservations r WHERE r.job_id=j.id),clock_timestamp()
            FROM background_jobs j WHERE j.id=@id
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", job.Value);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return new(row.GetInt64(0), row.IsDBNull(1) ? null : row.GetGuid(1), row.IsDBNull(2) ? null : row.GetString(2),
            row.IsDBNull(3) ? null : row.GetFieldValue<DateTimeOffset>(3), row.GetFieldValue<DateTimeOffset>(4),
            row.IsDBNull(5) ? null : row.GetFieldValue<DateTimeOffset>(5), row.IsDBNull(6) ? null : row.GetString(6),
            row.GetInt32(7), row.GetInt64(8), row.GetInt64(9), row.GetFieldValue<DateTimeOffset>(10));
    }
}
