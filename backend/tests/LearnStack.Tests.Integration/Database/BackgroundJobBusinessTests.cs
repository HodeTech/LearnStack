using System.Data;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Application.Audit;
using LearnStack.Application.Jobs;
using LearnStack.Application.Persistence;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Audit;
using LearnStack.Modules.Tenancy.Application.Contracts.IntegrationEvents;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>The real ordinary owner, app-role preflight and atomic final job fence.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class BackgroundJobBusinessTests(SchemaFixture schema)
{
    private static BackgroundJobRuntimeFixture Fixture => BackgroundJobRuntimeFixture.Shared;

    [Theory]
    [InlineData("success")]
    [InlineData("refusal")]
    [InlineData("exception")]
    [InlineData("closed_gate")]
    [InlineData("leaked_joiner")]
    [InlineData("domain_failure")]
    [InlineData("audit_failure")]
    [InlineData("ignored_cancellation")]
    public async Task Business_aggregate_child_job_outbox_and_MUST_audit_share_the_final_completion(string ending)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        if (ending == "audit_failure")
        {
            await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
            await SchemaQueries.ExecuteAsync(owner, null,
                "ALTER TABLE audit_log ADD CONSTRAINT reject_job_business_audit CHECK (operation <> 'tenancy.organization.create')");
        }
        var incoming = Guid.CreateVersion7();
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            causalActor: BackgroundJobRuntimeFixture.Human, incomingEvent: incoming);
        var probe = new BackgroundJobFixtureProbe();
        var business = Guid.CreateVersion7();
        var outgoing = Guid.CreateVersion7();
        var fact = new BusinessFact(Guid.CreateVersion7(), DateTimeOffset.UtcNow, outgoing, ending == "domain_failure");
        Organization? aggregate = null;
        var domain = new DomainDispatchProbe();
        BackgroundJobId? child = null;
        IUnitOfWorkScope? leaked = null;
        var gate = new BackgroundJobCommitGate();
        using var ownedStop = new CancellationTokenSource();
        var observed = false;
        var logs = new BusinessLogs();
        await using var host = Fixture.Host(database, probe, configure: services =>
        {
            services.AddSingleton(domain);
            services.AddScoped<INotificationHandler<BusinessFact>, BusinessFactHandler>();
            services.AddSingleton<ILogger<AuditExecutionCoordinator>>(new BusinessLogger<AuditExecutionCoordinator>(logs));
            services.AddSingleton<ILogger<PostgresAuditStore>>(new BusinessLogger<PostgresAuditStore>(logs));
        });
        probe.Body = async (request, services, cancellation) =>
        {
            request.Should().Be("TenantRoot");
            var unit = services.GetRequiredService<IUnitOfWork>();
            var connection = unit.Connection;
            var transaction = unit.Transaction;
            var context = services.GetRequiredService<ITenantContext>();
            context.Should().BeOfType<JobTenantContext>();
            context.UserId.Should().Be(UserId.SystemActor);
            context.CausalActorUserId.Should().Be(UserId.From(BackgroundJobRuntimeFixture.Human));
            (await BackgroundJobRuntimeRows.ScalarInUnitAsync(unit, "SELECT current_user")).Should().Be("learnstack_app");
            (await services.GetRequiredService<ISender>().Send(new CreateOrganizationCommand(
                OrganizationId.From(business), "job-business-" + business.ToString("N")[..12], "Job business"), cancellation))
                .IsSuccess.Should().BeTrue();
            child = await Fixture.EnqueueAsync(services);
            var db = services.GetRequiredService<TenancyDbContext>();
            aggregate = db.Organizations.Local.Single(entity => entity.Id.Value == business);
            typeof(Entity<OrganizationId>).GetMethod("RaiseDomainEvent", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(aggregate, [fact]);
            aggregate.Rename("Job domain event", services.GetRequiredService<IClock>(), UserId.SystemActor);
            (await db.SaveChangesAsync(cancellation)).Should().BeGreaterThan(0);
            aggregate.DomainEvents.Should().ContainSingle();
            domain.Calls.Should().Be(0, "successful saves stage facts but only the physical owner drains them");
            unit.Connection.Should().BeSameAs(connection);
            unit.Transaction.Should().BeSameAs(transaction);
            (await BackgroundJobRuntimeRows.ReadInUnitAsync(unit, child.Value)).Should().NotBeNull();
            (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, child.Value)).Should().BeNull();
            observed = true;
            if (ending == "exception") throw new InvalidOperationException("Untrusted business details");
            if (ending == "refusal") return Result<None>.Fail(new Error(new LocalizedMessage("lockey_business_rule_violation")));
            if (ending == "closed_gate") gate.Close();
            if (ending == "ignored_cancellation") await ownedStop.CancelAsync();
            if (ending == "leaked_joiner") leaked = await unit.BeginTransactionAsync(cancellation);
            return Result.Ok(None.Value);
        };
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var admission = await AdmitAsync(host.Services, job);
        JobBusinessExecutionResult result;
        try { result = await executor.ExecuteBusinessAsync(admission.Receipt!, gate, ownedStop.Token); }
        finally { if (leaked is not null) await leaked.DisposeAsync(); }
        observed.Should().BeTrue();
        aggregate!.DomainEvents.Should().BeEmpty("owner cleanup clears the saved source after either commit or rollback");
        domain.Calls.Should().Be(ending is "refusal" or "exception" or "ignored_cancellation" ? 0 : 1);
        var success = ending == "success";
        result.CommitAcknowledged.Should().Be(success);
        result.DurableOutcomeUncertain.Should().BeFalse();
        result.CleanupFailed.Should().BeFalse();
        result.SourceOwner.Should().BeSameAs(admission.SourceOwner);
        result.SourceOwner.CleanupConfirmed.Should().BeTrue();
        result.KnownRollback.Should().Be(!success);
        result.Decision.Disposition.Should().Be(ending switch
        {
            "success" => BackgroundJobFailureDisposition.Succeeded,
            "refusal" => BackgroundJobFailureDisposition.Terminal,
            "closed_gate" or "ignored_cancellation" => BackgroundJobFailureDisposition.RetainOwnership,
            _ => BackgroundJobFailureDisposition.Retry,
        });
        if (ending == "leaked_joiner") result.CommitStarted.Should().BeTrue("the gate won before Complete refused a live leaked joiner");
        if (ending == "audit_failure") result.Decision.ErrorCode.Should().Be("audit_unavailable");
        var row = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
        row.Phase.Should().Be(success ? "completed" : "executing");
        row.NormalAttempts.Should().Be(1);
        (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, business)).Should().Be(success ? 1 : 0);
        var childRow = await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, child!.Value);
        if (success)
        {
            childRow.Should().NotBeNull();
            childRow!.ParentJob.Should().Be(job.Value);
            childRow.IncomingEvent.Should().Be(incoming);
            childRow.CausalActor.Should().Be(BackgroundJobRuntimeFixture.Human);
        }
        else childRow.Should().BeNull();
        await using var read = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var tx = await read.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(read, tx, SchemaFixture.TenantA, null);
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM outbox_messages WHERE id=@id", (NpgsqlConnection)read, (NpgsqlTransaction)tx))
        {
            count.Parameters.AddWithValue("id", outgoing);
            ((long)(await count.ExecuteScalarAsync())!).Should().Be(success ? 1 : 0);
        }
        await using var audit = new NpgsqlCommand("SELECT outcome, actor_user_id, metadata FROM audit_log WHERE entity_id=@id AND operation='tenancy.organization.create'",
            (NpgsqlConnection)read, (NpgsqlTransaction)tx);
        audit.Parameters.AddWithValue("id", business.ToString());
        await using var reader = await audit.ExecuteReaderAsync();
        if (ending == "audit_failure")
        {
            (await reader.ReadAsync()).Should().BeFalse("both transactional and standalone writes meet the real database refusal");
            logs.Entries.Should().Contain(entry => entry.Category == typeof(PostgresAuditStore));
            logs.Entries.Should().Contain(entry => entry.Category == typeof(AuditExecutionCoordinator));
            logs.Entries.Should().OnlyContain(entry => entry.Exception == null,
                "the real nested audit failure must never expose a provider exception from job execution");
            string.Join('\n', logs.Entries.Select(entry => entry.Text)).Should()
                .NotContain("reject_job_business_audit").And.NotContain("23514");
            return;
        }
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be(success ? "success" : "failed");
        reader.GetGuid(1).Should().Be(UserId.SystemActor.Value);
        using var metadata = JsonDocument.Parse(reader.GetString(2));
        metadata.RootElement.GetProperty("job_id").GetGuid().Should().Be(job.Value);
        metadata.RootElement.GetProperty("causation_id").GetGuid().Should().Be(incoming);
        metadata.RootElement.GetProperty("causal_actor_user_id").GetGuid().Should().Be(BackgroundJobRuntimeFixture.Human);
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_transaction_reannouncement_invalidates_the_bound_completion(bool retarget)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var probe = new BackgroundJobFixtureProbe();
        await using var host = Fixture.Host(database, probe);
        probe.Body = async (_, services, cancellation) =>
        {
            var unit = services.GetRequiredService<IUnitOfWork>();
            var connection = unit.Connection;
            var transaction = unit.Transaction;
            var original = unit.GetTenantContextAnnouncement(transaction)!;
            var context = services.GetRequiredService<ITenantContext>();
            await unit.SetTenantContextAsync(retarget
                ? new BackgroundJobFixtureContext(SchemaFixture.OrgA1) { UserId = UserId.SystemActor }
                : context, cancellation);
            unit.Connection.Should().BeSameAs(connection);
            unit.Transaction.Should().BeSameAs(transaction);
            var changed = unit.GetTenantContextAnnouncement(transaction)!;
            changed.Generation.Should().NotBe(original.Generation);
            changed.OrganizationId.Should().Be(retarget ? OrganizationId.From(SchemaFixture.OrgA1) : null);
            services.GetRequiredService<ITenantContext>().Should().BeSameAs(context);
            return Result.Ok(None.Value);
        };
        var admission = await AdmitAsync(host.Services, job);
        var result = await host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(admission.Receipt!, new());
        result.CommitStarted.Should().BeFalse();
        result.CommitAcknowledged.Should().BeFalse();
        result.KnownRollback.Should().BeTrue();
        result.DurableOutcomeUncertain.Should().BeFalse();
        result.CleanupFailed.Should().BeFalse();
        probe.Constructions.Should().Equal("TenantRoot");
        (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.Phase.Should().Be("executing");
    }

    [Theory]
    [InlineData("lease")]
    [InlineData("token")]
    [InlineData("generation")]
    public async Task Preflight_refuses_stale_authority_before_constructing_the_handler(string changed)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var probe = new BackgroundJobFixtureProbe();
        await using var host = Fixture.Host(database, probe);
        var admission = await AdmitAsync(host.Services, job);
        await using (var control = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
        {
            await SchemaQueries.ExecuteAsync(control, null,
                "UPDATE background_jobs SET ownership_until=clock_timestamp()-interval '1 microsecond' WHERE id=@id", ("id", job.Value));
            if (changed != "lease")
                await SchemaQueries.ExecuteAsync(control, null, """
                    UPDATE background_jobs SET phase='reconciling', ownership_kind='recovery',
                        ownership_token=gen_random_uuid(), ownership_until=clock_timestamp()+interval '30 seconds' WHERE id=@id
                    """, ("id", job.Value));
            if (changed == "generation")
                await SchemaQueries.ExecuteAsync(control, null,
                    """
                    UPDATE background_jobs SET phase='pending',
                        ownership_token=NULL, ownership_kind=NULL, ownership_until=NULL WHERE id=@id
                    """, ("id", job.Value));
        }
        var result = await host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(admission.Receipt!, new());
        result.CommitAcknowledged.Should().BeFalse();
        result.KnownRollback.Should().BeTrue();
        probe.Constructions.Should().BeEmpty("the unlocked preflight precedes MediatR handler construction");
        (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.Phase.Should().Be(
            changed == "lease" ? "executing" : changed == "token" ? "reconciling" : "pending");
    }

    [Fact]
    public async Task Direct_and_fresh_scope_nested_escapes_refuse_before_any_second_transaction()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var probe = new BackgroundJobFixtureProbe();
        var nested = 0;
        await using var host = Fixture.Host(database, probe);
        probe.Body = async (request, services, _) =>
        {
            if (request == "Nested") { nested++; return Result.Ok(None.Value); }
            var unit = services.GetRequiredService<IUnitOfWork>();
            var physical = unit.Transaction;
            var construct = false;
            var direct = () => services.GetRequiredService<TransactionExecutionCoordinator>().ExecuteAsync(null,
                TransactionMode.ReadWrite, "direct-borrow", () => { construct = true; return Task.FromResult(Result.Ok(None.Value)); });
            await direct.Should().ThrowAsync<InvalidOperationException>();
            construct.Should().BeFalse();
            (await Fixture.SendAsync(services, "Nested")).IsSuccess.Should().BeTrue();
            await using var fresh = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            var other = fresh.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await FluentActions.Awaiting(() => Fixture.SendAsync(fresh.ServiceProvider, "Nested"))
                .Should().ThrowAsync<InvalidOperationException>();
            other.HasActiveTransaction.Should().BeFalse();
            unit.Transaction.Should().BeSameAs(physical);
            var accessor = services.GetRequiredService<ITenantContextAccessor>();
            var saved = accessor.Current;
            accessor.Current = new BackgroundJobFixtureContext { UserId = UserId.SystemActor };
            (await Fixture.SendAsync(services, "Nested")).Error!.Code.Should().Be("forbidden");
            accessor.Current = saved;
            return Result.Ok(None.Value);
        };
        var admission = await AdmitAsync(host.Services, job);
        var result = await host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(admission.Receipt!, new());
        result.CommitAcknowledged.Should().BeTrue();
        nested.Should().Be(1);
        probe.Constructions.Should().Equal("TenantRoot", "Nested");
    }

    private static async Task<JobStartExecutionResult> AdmitAsync(IServiceProvider services, BackgroundJobId job)
    {
        var claim = (await services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, 1))!;
        var result = await services.GetRequiredService<JobExecutor>().ExecuteAsync(claim);
        result.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        result.CleanupFailed.Should().BeFalse();
        result.Receipt!.SourceOwner.Should().BeSameAs(result.SourceOwner);
        return result;
    }

    private sealed record BusinessFact(Guid EventId, DateTimeOffset OccurredAt, Guid OutgoingId, bool Fail) : IDomainEvent;

    private sealed class DomainDispatchProbe
    {
        internal int Calls { get; set; }
    }

    private sealed class BusinessFactHandler(IOutbox outbox, IUnitOfWork unit, DomainDispatchProbe probe)
        : INotificationHandler<BusinessFact>
    {
        public async Task Handle(BusinessFact notification, CancellationToken cancellationToken)
        {
            probe.Calls++;
            (await BackgroundJobRuntimeRows.ScalarInUnitAsync(unit, "SELECT count(*) FROM audit_log")).Should().Be(0L,
                "domain drain precedes the business MUST flush");
            if (notification.Fail) throw new InvalidOperationException("Untrusted domain dispatch details");
            await outbox.EnqueueAsync(new TenantCreatedIntegrationEventV1
            {
                EventId = notification.OutgoingId,
                OccurredAt = notification.OccurredAt,
                TenantId = SchemaFixture.TenantA,
            }, cancellationToken);
        }
    }
}
