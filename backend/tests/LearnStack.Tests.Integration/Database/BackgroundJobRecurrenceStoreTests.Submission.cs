using System.Text.Json;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Infrastructure.Jobs;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRecurrenceStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recipient_submission_commits_one_fixed_occurrence_child_and_reciprocal_receipt_without_constructing_feature_work(bool organization)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var manifest = await PrepareClaimRouteAsync(database, Store(source), organization);
        var claim = await ClaimRecipientAsync(source, manifest);
        var probe = new BackgroundJobFixtureProbe();
        var constructions = Fixture.RootConstructions;
        await using var host = RecipientHost(database, probe);
        var gate = BackgroundJobTestRun.Create().OpenBoundary(BackgroundJobCheckpointKind.RecipientSubmission);

        var result = await host.Services.GetRequiredService<JobExecutor>().ExecuteRecipientAsync(claim,
            BackgroundJobLifecycleProjectionDeclaration.Create(), gate, CancellationToken.None);

        result.Failure.Should().BeNull();
        result.Status.Should().Be(JobRecipientSubmissionStatus.Accepted);
        result.CleanupFailed.Should().BeFalse();
        result.SourceOwner.CleanupConfirmed.Should().BeTrue();
        result.RetainedCleanup.Should().BeEmpty();
        gate.CommitAcknowledged.Should().BeTrue();
        gate.KnownRollback.Should().BeFalse();
        result.Command!.AcceptedChild.Should().NotBeNull();
        var child = result.Command.AcceptedChild!.Value;
        child.Value.Version.Should().Be(7);
        AssertRecipientTargetUnconstructed(constructions, probe);

        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, claim.TenantId.Value, claim.OrganizationId?.Value);
        await using (var command = new NpgsqlCommand("""
            SELECT j.parameters,j.id=@child AND j.tenant_id=@tenant
                AND j.organization_id IS NOT DISTINCT FROM @organization AND j.recipient_id=@recipient
                AND j.original_due_at=@due AND j.definition_key=@definition
                AND j.parent_job_id IS NULL AND j.causation_event_id IS NULL AND j.causal_actor_user_id IS NULL
                AND r.accepted_job_id=j.id AND r.accepted_at IS NOT NULL
                AND r.ownership_token IS NULL AND r.ownership_until IS NULL
            FROM background_jobs j JOIN background_job_recipients r ON r.tenant_id=j.tenant_id AND r.id=j.recipient_id
            """, (NpgsqlConnection?)app, (NpgsqlTransaction?)transaction))
        {
            command.Parameters.AddWithValue("child", child.Value);
            command.Parameters.AddWithValue("tenant", claim.TenantId.Value);
            command.Parameters.AddWithValue("organization", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)claim.OrganizationId?.Value ?? DBNull.Value);
            command.Parameters.AddWithValue("recipient", claim.RecipientId.Value);
            command.Parameters.AddWithValue("due", claim.DueAt);
            command.Parameters.AddWithValue("definition", claim.DefinitionKey);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetBoolean(1).Should().BeTrue();
            using var payload = JsonDocument.Parse(reader.GetString(0));
            payload.RootElement.EnumerateObject().Should().ContainSingle().Which.Name.Should().Be("OccurrenceId");
            payload.RootElement.GetProperty("OccurrenceId").GetGuid().Should().Be(claim.OccurrenceId.Value);
            (await reader.ReadAsync()).Should().BeFalse();
        }
        await transaction.RollbackAsync();

        var replay = await host.Services.GetRequiredService<JobExecutor>().ExecuteRecipientAsync(claim,
            BackgroundJobLifecycleProjectionDeclaration.Create(),
            BackgroundJobTestRun.Create().OpenBoundary(BackgroundJobCheckpointKind.RecipientSubmission), CancellationToken.None);
        replay.Status.Should().NotBe(JobRecipientSubmissionStatus.Accepted, "the in-memory routing capability is single-use");
        (replay.Command?.AcceptedChild).Should().BeNull();
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_jobs")).Should().Be(1);
        (await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_job_attempts")).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_same_no_construction_assertion_detects_actual_target_request_and_handler_construction(bool organization)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var probe = new BackgroundJobFixtureProbe();
        await using var host = RecipientHost(database, probe);
        Fixture.Definitions().TryGet(organization ? "jobsrecurrencefixture.organization.v1" : "jobsrecurrencefixture.run.v1",
            out var definition).Should().BeTrue();
        var root = definition!.ProfileRegistration.RootRequestType;
        var baseline = Fixture.RootConstructions;
        AssertRecipientTargetUnconstructed(baseline, probe);
        _ = Activator.CreateInstance(root, new BackgroundJobOccurrenceParameters(BackgroundJobOccurrenceId.From(Guid.CreateVersion7())));
        Action sameAssertion = () => AssertRecipientTargetUnconstructed(baseline, probe);
        sameAssertion.Should().Throw<Xunit.Sdk.XunitException>("constructing the actual selected target must fail the same observation");

        baseline = Fixture.RootConstructions;
        AssertRecipientTargetUnconstructed(baseline, probe);
        await using var scope = host.Services.CreateAsyncScope();
        var handler = typeof(IRequestHandler<,>).MakeGenericType(root, typeof(Result<None>));
        _ = scope.ServiceProvider.GetRequiredService(handler);
        probe.Constructions.Should().Equal(organization ? "recurrence:organization" : "recurrence:tenant");
        sameAssertion.Should().Throw<Xunit.Sdk.XunitException>("resolving the actual target handler must fail that same observation independently");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recipient_submission_rechecks_current_tenant_or_organization_eligibility_before_creating_a_child(bool organization)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var manifest = await PrepareClaimRouteAsync(database, Store(source), organization);
        var claim = await ClaimRecipientAsync(source, manifest);
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await SchemaQueries.ExecuteAsync(app, transaction, organization
                ? "UPDATE organizations SET deleted_at=clock_timestamp(),deleted_by=@actor WHERE id=@id"
                : "UPDATE tenants SET deleted_at=clock_timestamp(),deleted_by=@actor WHERE id=@id",
                ("actor", SchemaFixture.Actor), ("id", organization ? SchemaFixture.OrgA1 : SchemaFixture.TenantA));
            await transaction.CommitAsync();
        }
        await using var host = RecipientHost(database, new());
        var gate = BackgroundJobTestRun.Create().OpenBoundary(BackgroundJobCheckpointKind.RecipientSubmission);
        var result = await host.Services.GetRequiredService<JobExecutor>().ExecuteRecipientAsync(claim,
            BackgroundJobLifecycleProjectionDeclaration.Create(), gate, CancellationToken.None);

        result.Failure.Should().BeNull();
        result.Status.Should().Be(JobRecipientSubmissionStatus.Ineligible);
        gate.CommitAcknowledged.Should().BeFalse();
        gate.KnownRollback.Should().BeTrue();
        result.SourceOwner.CleanupConfirmed.Should().BeTrue();
        result.Command!.ProvisionalChild.Should().BeNull();
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_jobs")).Should().Be(0);
        (await ReadAsync<bool>(dispatcher, "SELECT accepted_job_id IS NULL AND skipped_at IS NULL FROM background_job_recipients"))
            .Should().BeTrue("the app role cannot impersonate the later confined skip owner");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Child_insert_or_final_receipt_refusal_rolls_back_both_writes(bool receipt)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var manifest = await PrepareClaimRouteAsync(database, Store(source), organization: false);
        var claim = await ClaimRecipientAsync(source, manifest);
        await using var migration = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await SchemaQueries.ExecuteAsync(migration, null, receipt
            ? "REVOKE UPDATE (accepted_job_id) ON background_job_recipients FROM learnstack_app"
            : "REVOKE INSERT (id) ON background_jobs FROM learnstack_app");
        try
        {
            await using var host = RecipientHost(database, new());
            var gate = BackgroundJobTestRun.Create().OpenBoundary(BackgroundJobCheckpointKind.RecipientSubmission);
            var result = await host.Services.GetRequiredService<JobExecutor>().ExecuteRecipientAsync(claim,
                BackgroundJobLifecycleProjectionDeclaration.Create(), gate, CancellationToken.None);
            result.Status.Should().NotBe(JobRecipientSubmissionStatus.Accepted);
            result.Failure.Should().NotBeNull();
            gate.CommitAcknowledged.Should().BeFalse();
            gate.KnownRollback.Should().BeTrue();
            result.CleanupFailed.Should().BeFalse();
            result.SourceOwner.CleanupConfirmed.Should().BeTrue();
            result.Command!.AcceptedChild.Should().BeNull();
            result.Command.ProvisionalChild.HasValue.Should().Be(receipt, "only the final receipt control reaches a successful INSERT");
            await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            (await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_jobs")).Should().Be(0);
            (await ReadAsync<bool>(dispatcher, "SELECT accepted_job_id IS NULL AND accepted_at IS NULL FROM background_job_recipients"))
                .Should().BeTrue();
        }
        finally
        {
            await SchemaQueries.ExecuteAsync(migration, null, receipt
                ? "GRANT UPDATE (accepted_job_id) ON background_job_recipients TO learnstack_app"
                : "GRANT INSERT (id) ON background_jobs TO learnstack_app");
        }
    }

    [Fact]
    public async Task Closed_recipient_gate_never_opens_an_application_owner_or_creates_a_child()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var manifest = await PrepareClaimRouteAsync(database, Store(source), organization: false);
        var claim = await ClaimRecipientAsync(source, manifest);
        await using var host = RecipientHost(database, new());
        var gate = BackgroundJobTestRun.Create().OpenBoundary(BackgroundJobCheckpointKind.RecipientSubmission);
        gate.Close();
        var result = await host.Services.GetRequiredService<JobExecutor>().ExecuteRecipientAsync(claim,
            BackgroundJobLifecycleProjectionDeclaration.Create(), gate, CancellationToken.None);
        result.Status.Should().Be(JobRecipientSubmissionStatus.Canceled);
        result.Command.Should().BeNull();
        result.SourceOwner.CleanupConfirmed.Should().BeTrue();
        gate.CommitStarted.Should().BeFalse();
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_jobs")).Should().Be(0);
    }

    private static WebApplicationFactory<Program> RecipientHost(DisposableSchemaDatabase database, BackgroundJobFixtureProbe probe) =>
        BackgroundJobRuntimeFixture.Shared.Host(database, probe, configure: services =>
        {
            services.RemoveAll<BackgroundJobDefinitionRegistry>();
            services.AddSingleton(Fixture.Definitions());
            foreach (var type in Fixture.Module.Application.GetTypes())
                foreach (var contract in type.GetInterfaces().Where(candidate => candidate.IsGenericType
                             && candidate.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)))
                    services.AddTransient(contract, type);
        });

    private static void AssertRecipientTargetUnconstructed(int baseline, BackgroundJobFixtureProbe probe)
    {
        Fixture.RootConstructions.Should().Be(baseline, "fan-out submits the real target without constructing its feature request");
        probe.Constructions.Should().BeEmpty("fan-out constructs neither the actual target handler nor another module's services");
    }

    private static async Task<BackgroundJobRecipientClaim> ClaimRecipientAsync(PrivilegedDataSource source,
        BackgroundJobRecurrenceDescriptor manifest)
    {
        var result = await Store(source).TryClaimRecipientAsync(Fixture.Recurrences(manifest));
        result.ThrowIfFailed();
        result.CommitAcknowledged.Should().BeTrue();
        result.CleanupConfirmed.Should().BeTrue();
        result.Value.Should().NotBeNull();
        return result.Value!;
    }
}
