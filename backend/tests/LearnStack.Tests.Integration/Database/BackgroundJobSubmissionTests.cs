using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Application.Messaging;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Actual API pipeline submission on the real announced application-role transaction.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class BackgroundJobSubmissionTests(SchemaFixture schema)
{
    private static BackgroundJobRuntimeFixture Fixture => BackgroundJobRuntimeFixture.Shared;
    private static readonly Error Refusal = new(new LocalizedMessage("lockey_business_rule_violation"));

    [Fact]
    public async Task Typed_Pending_submission_commits_backlog_but_cannot_route_or_spend_an_attempt()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var probe = new BackgroundJobFixtureProbe();
        BackgroundJobId? job = null;
        await using var host = Fixture.Host(database, probe);
        await using var scope = host.Services.CreateAsyncScope();
        probe.Body = async (_, services, _) =>
        {
            job = await Fixture.EnqueueAsync(services, BackgroundJobRuntimeFixture.PendingKey,
                dueAt: BackgroundJobSchemaSeed.Slot);
            (await BackgroundJobRuntimeRows.ReadInUnitAsync(services.GetRequiredService<IUnitOfWork>(), job.Value))
                .Should().NotBeNull("declared Pending work still inserts immediately on its producer transaction");
            return Result.Ok(None.Value);
        };
        (await Fixture.SendAsync(scope.ServiceProvider)).IsSuccess.Should().BeTrue();
        AssertClean(scope.ServiceProvider);
        var row = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job!.Value))!;
        row.Definition.Should().Be(BackgroundJobRuntimeFixture.PendingKey);
        row.OriginalDue.Should().Be(BackgroundJobSchemaSeed.Slot);
        row.Phase.Should().Be("pending");
        row.NormalAttempts.Should().Be(0);
        (await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job.Value, 1)).Should().BeNull();
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job.Value)).Should().Be(0);
        probe.Constructions.Should().ContainSingle().Which.Should().Be("Submit");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Typed_submission_is_visible_immediately_and_commits_with_business_and_MUST_audit(bool organizationScoped)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var context = new BackgroundJobFixtureContext(organizationScoped ? SchemaFixture.OrgA1 : null);
        var probe = new BackgroundJobFixtureProbe();
        var business = Guid.CreateVersion7();
        var resource = Guid.CreateVersion7();
        var due = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.FromHours(3)).AddTicks(97);
        BackgroundJobId? job = null;
        string? traceparent = null;
        var previousActivity = Activity.Current;
        Activity.Current = null;
        try
        {
            await using var host = Fixture.Host(database, probe, context);
            await using var scope = host.Services.CreateAsyncScope();
            probe.Body = async (_, services, _) =>
            {
                var unit = services.GetRequiredService<IUnitOfWork>();
                var connection = unit.Connection;
                var transaction = unit.Transaction;
                (await BackgroundJobRuntimeRows.ScalarInUnitAsync(unit, "SELECT current_user")).Should().Be("learnstack_app");
                (await BackgroundJobRuntimeRows.ScalarInUnitAsync(unit,
                    "SELECT (rolsuper OR rolbypassrls)::text FROM pg_roles WHERE rolname=current_user")).Should().Be("false");
                (await services.GetRequiredService<ISender>().Send(new CreateOrganizationCommand(
                    OrganizationId.From(business), "job-proof-" + business.ToString("N")[..12], "Job proof"), CancellationToken.None)).IsSuccess.Should().BeTrue();
                traceparent = Activity.Current!.Id;
                // Trusted enrollment must survive mutations to this ambient object.
                context.OrganizationId = OrganizationId.From(SchemaFixture.OrgA2);
                context.UserId = UserId.SystemActor;
                job = await Fixture.EnqueueAsync(services,
                    organizationScoped ? BackgroundJobRuntimeFixture.OrganizationKey : BackgroundJobRuntimeFixture.TenantKey,
                    Fixture.Parameters(resource), due);
                unit.Connection.Should().BeSameAs(connection);
                unit.Transaction.Should().BeSameAs(transaction);
                var pending = (await BackgroundJobRuntimeRows.ReadInUnitAsync(unit, job.Value))!;
                pending.Should().NotBeNull("submission INSERTs before any completion flush");
                pending.NormalAttempts.Should().Be(0);
                pending.Phase.Should().Be("pending");
                pending.CurrentAttempt.Should().BeNull();
                (await BackgroundJobRuntimeRows.ScalarInUnitAsync(unit, "SELECT count(*) FROM audit_log")).Should().Be(0L);
                (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job!.Value,
                    organizationScoped ? SchemaFixture.OrgA1 : null)).Should().BeNull("another physical connection cannot see uncommitted intent");
                return Result.Ok(None.Value);
            };
            (await Fixture.SendAsync(scope.ServiceProvider, organizationScoped ? "SubmitOrganization" : "Submit")).IsSuccess.Should().BeTrue();
            AssertClean(scope.ServiceProvider);
            Activity.Current.Should().BeNull("the no-listener producer Activity is actually stopped");
        }
        finally { Activity.Current = previousActivity; }

        var row = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job!.Value,
            organizationScoped ? SchemaFixture.OrgA1 : null))!;
        row.TenantId.Should().Be(SchemaFixture.TenantA);
        row.OrganizationId.Should().Be(organizationScoped ? SchemaFixture.OrgA1 : null);
        row.CausalActor.Should().Be(BackgroundJobRuntimeFixture.Human);
        row.IncomingEvent.Should().BeNull();
        row.ParentJob.Should().BeNull();
        row.Traceparent.Should().Be(traceparent);
        ActivityContext.TryParse(row.Traceparent, null, out _).Should().BeTrue();
        row.OriginalDue.Should().Be(BackgroundJobSchemaSeed.Microseconds(due));
        row.AvailableAfter.Should().Be(row.OriginalDue);
        using var parameters = JsonDocument.Parse(row.Parameters);
        parameters.RootElement.GetProperty("ResourceId").GetGuid().Should().Be(resource);
        (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, business)).Should().Be(1);
        await AssertBusinessAuditAsync(database.AppConnectionString, business, "success",
            organizationScoped ? SchemaFixture.OrgA1 : null);
        (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job!.Value,
            tenant: SchemaFixture.TenantB)).Should().BeNull("app-role RLS independently hides another tenant's job");
        if (organizationScoped)
            (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job!.Value,
                SchemaFixture.OrgA2)).Should().BeNull("app-role RLS independently hides a sibling organization's job");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_handler_refusal_or_exception_rolls_back_observed_job_business_and_audit(bool throws)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var probe = new BackgroundJobFixtureProbe();
        var business = Guid.CreateVersion7();
        BackgroundJobId? job = null;
        var fault = new InvalidOperationException("Deliberate failure after immediate job INSERT");
        await using var host = Fixture.Host(database, probe);
        await using var scope = host.Services.CreateAsyncScope();
        probe.Body = async (_, services, _) =>
        {
            (await services.GetRequiredService<ISender>().Send(new CreateOrganizationCommand(
                OrganizationId.From(business), "job-rollback-" + business.ToString("N")[..12], "Job rollback"), CancellationToken.None)).IsSuccess.Should().BeTrue();
            job = await Fixture.EnqueueAsync(services);
            (await BackgroundJobRuntimeRows.ReadInUnitAsync(services.GetRequiredService<IUnitOfWork>(), job.Value)).Should().NotBeNull();
            if (throws) throw fault;
            return Result<None>.Fail(Refusal);
        };
        if (throws)
            (await FluentActions.Awaiting(() => Fixture.SendAsync(scope.ServiceProvider)).Should()
                .ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(fault);
        else
            (await Fixture.SendAsync(scope.ServiceProvider)).IsFailure.Should().BeTrue();
        (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job!.Value)).Should().BeNull();
        (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, business)).Should().Be(0);
        await AssertBusinessAuditAsync(database.AppConnectionString, business, "failed");
        AssertClean(scope.ServiceProvider);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_absorbed_descendant_refusal_poison_depends_on_actual_produced_work(bool produce)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var probe = new BackgroundJobFixtureProbe();
        BackgroundJobId? job = null;
        var business = Guid.CreateVersion7();
        var observed = false;
        await using var host = Fixture.Host(database, probe);
        await using var scope = host.Services.CreateAsyncScope();
        probe.Body = async (request, services, _) =>
        {
            if (request == "Nested")
            {
                if (produce)
                {
                    job = await Fixture.EnqueueAsync(services);
                    (await BackgroundJobRuntimeRows.ReadInUnitAsync(services.GetRequiredService<IUnitOfWork>(), job.Value)).Should().NotBeNull();
                }
                return Result<None>.Fail(Refusal);
            }
            (await services.GetRequiredService<ISender>().Send(new CreateOrganizationCommand(
                OrganizationId.From(business), "job-absorbed-" + business.ToString("N")[..12], "Absorbed refusal"), CancellationToken.None)).IsSuccess.Should().BeTrue();
            (await Fixture.SendAsync(services, "Nested")).Error.Should().BeSameAs(Refusal);
            services.GetRequiredService<IUnitOfWork>().IsRollbackOnly.Should().Be(produce);
            observed = true;
            return Result.Ok(None.Value);
        };
        if (produce)
            await FluentActions.Awaiting(() => Fixture.SendAsync(scope.ServiceProvider)).Should()
                .ThrowAsync<InvalidOperationException>().WithMessage("*rollback-only*");
        else
            (await Fixture.SendAsync(scope.ServiceProvider)).IsSuccess.Should().BeTrue();
        observed.Should().BeTrue();
        (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, business)).Should().Be(produce ? 0 : 1);
        if (produce) (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job!.Value)).Should().BeNull();
        AssertClean(scope.ServiceProvider);
    }

    [Theory]
    [InlineData("Foreign", null, BackgroundJobRuntimeFixture.TenantKey)]
    [InlineData("Submit", null, BackgroundJobRuntimeFixture.OrganizationKey)]
    [InlineData("SubmitOrganization", "a", BackgroundJobRuntimeFixture.TenantKey)]
    [InlineData("PublicSubmit", null, BackgroundJobRuntimeFixture.TenantKey)]
    public async Task Foreign_module_wrong_scope_and_physical_readonly_submission_are_refused(string request, string? organization, string key)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var probe = new BackgroundJobFixtureProbe();
        var called = false;
        ITenantContext context = request == "PublicSubmit"
            ? TenantContextFactory.Create(new TenantResolutionAttempt { HostTenantId = TenantId.From(SchemaFixture.TenantA) }).Value!
            : new BackgroundJobFixtureContext(organization is null ? null : SchemaFixture.OrgA1);
        await using var host = Fixture.Host(database, probe, context);
        await using var scope = host.Services.CreateAsyncScope();
        probe.Body = async (_, services, _) =>
        {
            if (request == "PublicSubmit")
                (await BackgroundJobRuntimeRows.ScalarInUnitAsync(services.GetRequiredService<IUnitOfWork>(),
                    "SHOW transaction_read_only")).Should().Be("on");
            await FluentActions.Awaiting(() => Fixture.EnqueueAsync(services, key)).Should().ThrowAsync<InvalidOperationException>();
            CurrentInvocation(services).HasProducedWork.Should().BeFalse();
            called = true;
            return Result.Ok(None.Value);
        };
        (await Fixture.SendAsync(scope.ServiceProvider, request)).IsSuccess.Should().BeTrue();
        called.Should().BeTrue();
        AssertClean(scope.ServiceProvider);
    }

    [Fact]
    public async Task A_raw_or_orphan_announced_transaction_cannot_lend_producer_authority()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var probe = new BackgroundJobFixtureProbe();
        await using var host = Fixture.Host(database, probe);
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await FluentActions.Awaiting(() => Fixture.EnqueueAsync(services)).Should().ThrowAsync<InvalidOperationException>();
        var unit = services.GetRequiredService<IUnitOfWork>();
        await using var raw = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(new BackgroundJobFixtureContext());
        probe.Body = async (_, nested, _) =>
        {
            await FluentActions.Awaiting(() => Fixture.EnqueueAsync(nested)).Should().ThrowAsync<InvalidOperationException>();
            CurrentInvocation(nested).HasProducedWork.Should().BeFalse();
            return Result.Ok(None.Value);
        };
        (await Fixture.SendAsync(services)).IsSuccess.Should().BeTrue();
        unit.HasActiveTransaction.Should().BeTrue("the raw owner remains outside the pipeline");
        await raw.FailAsync();
        AssertClean(services);
    }

    [Fact]
    public async Task A_real_SQL_fault_poisons_before_absorption_and_records_no_false_receipt()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
            await SchemaQueries.ExecuteAsync(owner, null,
                "ALTER TABLE background_jobs ADD CONSTRAINT reject_submission_probe CHECK (definition_key <> 'jobssubmissionfixture.run.v1')");
        var probe = new BackgroundJobFixtureProbe();
        await using var host = Fixture.Host(database, probe);
        await using var scope = host.Services.CreateAsyncScope();
        var observed = false;
        probe.Body = async (_, services, _) =>
        {
            var fault = (await FluentActions.Awaiting(() => Fixture.EnqueueAsync(services)).Should()
                .ThrowAsync<PostgresException>()).Which;
            fault.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            services.GetRequiredService<IUnitOfWork>().IsRollbackOnly.Should().BeTrue();
            CurrentInvocation(services).HasProducedWork.Should().BeFalse("only successful INSERT creates a receipt");
            observed = true;
            return Result.Ok(None.Value);
        };
        await FluentActions.Awaiting(() => Fixture.SendAsync(scope.ServiceProvider)).Should()
            .ThrowAsync<InvalidOperationException>().WithMessage("*rollback-only*");
        observed.Should().BeTrue();
        AssertClean(scope.ServiceProvider);
    }

    private static ProducerExecution.Invocation CurrentInvocation(IServiceProvider services) =>
        (ProducerExecution.Invocation)typeof(ProducerExecution).GetField("_current", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(services.GetRequiredService<ProducerExecution>())!;

    private static void AssertClean(IServiceProvider services)
    {
        var unit = services.GetRequiredService<IUnitOfWork>();
        unit.Transaction.Should().BeNull();
        unit.GetTenantContextAnnouncement(unit.Transaction).Should().BeNull();
        services.GetRequiredService<ProducerExecution>().HasInvocation.Should().BeFalse();
        var capture = services.GetRequiredService<IAuditStateCapture>();
        capture.HasOpenFrame.Should().BeFalse();
        capture.Intents.Should().BeEmpty();
    }

    private static async Task AssertBusinessAuditAsync(string connectionString, Guid id, string expectedOutcome, Guid? organization = null)
    {
        await using var connection = await PostgresFixture.OpenAsync(connectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(connection, transaction, SchemaFixture.TenantA, organization);
        await using var read = new NpgsqlCommand("SELECT outcome FROM audit_log WHERE entity_id = @id AND operation = 'tenancy.organization.create'",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        read.Parameters.AddWithValue("id", id.ToString());
        var outcomes = new List<string>();
        await using var rows = await read.ExecuteReaderAsync();
        while (await rows.ReadAsync()) outcomes.Add(rows.GetString(0));
        outcomes.Should().ContainSingle().Which.Should().Be(expectedOutcome,
            "a failed business transaction retains its standalone failure audit; success requires the same committed write");
    }
}
