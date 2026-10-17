using LearnStack.Application.Jobs;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Modules.Tenancy.Application.Contracts.IntegrationEvents;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Persistence;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobCapacityAdmissionTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("audit")]
    [InlineData("release")]
    public async Task Business_effects_MUST_completion_and_exact_reservation_release_share_one_commit(string ending)
    {
        await using var database = await DatabaseAsync();
        if (ending != "success")
        {
            await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
            await SchemaQueries.ExecuteAsync(owner, null, ending == "audit"
                ? "ALTER TABLE audit_log ADD CONSTRAINT reject_capacity_business_audit CHECK (operation <> 'tenancy.organization.create')"
                : "ALTER TABLE background_job_reservations ADD CONSTRAINT reject_capacity_business_release CHECK (released_at IS NULL)");
        }
        var organization = Guid.CreateVersion7();
        var outgoing = Guid.CreateVersion7();
        LearnStack.SharedKernel.Jobs.BackgroundJobId? child = null;
        var probe = new BackgroundJobFixtureProbe
        {
            Body = async (_, services, token) =>
            {
                var unit = services.GetRequiredService<IUnitOfWork>();
                await SchemaQueries.AssertApplicationRoleAsync(unit.Connection, unit.Transaction);
                (await services.GetRequiredService<ISender>().Send(new CreateOrganizationCommand(
                    OrganizationId.From(organization), "capacity-" + organization.ToString("N")[..12], "Capacity atomicity"), token))
                    .IsSuccess.Should().BeTrue();
                child = await Fixture.EnqueueAsync(services, dueAt: DateTimeOffset.UtcNow.AddHours(1));
                await services.GetRequiredService<IOutbox>().EnqueueAsync(new TenantCreatedIntegrationEventV1
                {
                    EventId = outgoing,
                    OccurredAt = DateTimeOffset.UtcNow,
                    TenantId = SchemaFixture.TenantA,
                }, token);
                return Result.Ok(None.Value);
            },
        };
        await using var host = CapacityHost(database, probe, new(-1));
        var job = await CapacityJobAsync(database);
        var admission = await StartAsync(host.Services, job);
        admission.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        var completed = await host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(admission.Receipt!, admission.Receipt!.Run.OpenBoundary(BackgroundJobCheckpointKind.Transactional));
        var committed = ending == "success";
        completed.CommitAcknowledged.Should().Be(committed);
        completed.KnownRollback.Should().Be(!committed);
        completed.DurableOutcomeUncertain.Should().BeFalse();
        completed.CleanupFailed.Should().BeFalse();
        completed.SourceOwner.Should().BeSameAs(admission.SourceOwner);
        (await OccupiedAsync(database)).Should().Be(committed ? 0 : 1);

        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(connection, transaction, SchemaFixture.TenantA, null);
        await SchemaQueries.AssertApplicationRoleAsync(connection, transaction);
        await using var count = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM organizations WHERE id=@organization),
                (SELECT count(*) FROM outbox_messages WHERE id=@outgoing),
                (SELECT count(*) FROM background_jobs WHERE id=@child),
                (SELECT count(*) FROM audit_log WHERE entity_id=@subject AND operation='tenancy.organization.create' AND outcome='success'),
                (SELECT count(*) FROM background_jobs WHERE id=@job AND phase='completed' AND processed_at IS NOT NULL),
                (SELECT count(*) FROM background_job_reservations WHERE job_id=@job AND attempt_id=@attempt
                    AND released_at IS NOT NULL AND release_reason='completed')
            """, connection, transaction);
        count.Parameters.AddWithValue("organization", organization);
        count.Parameters.AddWithValue("outgoing", outgoing);
        count.Parameters.AddWithValue("child", child!.Value.Value);
        count.Parameters.AddWithValue("subject", organization.ToString());
        count.Parameters.AddWithValue("job", job.Value);
        count.Parameters.AddWithValue("attempt", admission.Receipt!.AttemptId.Value);
        await using var row = await count.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        for (var index = 0; index < 6; index++) row.GetInt64(index).Should().Be(committed ? 1 : 0,
            "business, outbox, child, MUST, completion and reservation release have one durability boundary");
    }
}
