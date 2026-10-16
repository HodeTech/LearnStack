using System.Diagnostics;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Application.Persistence;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Real confined routing, physical app-role pre-read and independently committed start admission.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class BackgroundJobAdmissionTests(SchemaFixture schema)
{
    private static BackgroundJobRuntimeFixture Fixture => BackgroundJobRuntimeFixture.Shared;

    [Fact]
    public async Task A_routing_claim_spends_no_attempt_and_admission_commits_before_any_business_construction()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var probe = new BackgroundJobFixtureProbe();
        var original = new BackgroundJobFixtureContext();
        var roots = Fixture.RootConstructions;
        await using var host = Fixture.Host(database, probe, original);
        var store = host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>();
        var claim = (await store.TryClaimAsync(job, 1))!;
        claim.Should().NotBeNull();
        var routed = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
        routed.OwnershipToken.Should().Be(claim.Token);
        routed.OwnershipKind.Should().Be("routing");
        routed.OwnershipUntil.Should().Be(claim.OwnershipUntil);
        routed.NormalAttempts.Should().Be(0);
        routed.CurrentAttempt.Should().BeNull();
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(0);
        (await store.TryClaimAsync(job, 1)).Should().BeNull("an active routing lease excludes another callback");

        using var activity = new Activity("outer-admission-proof").SetIdFormat(ActivityIdFormat.W3C).Start();
        var result = await host.Services.GetRequiredService<JobExecutor>().ExecuteAsync(claim);
        result.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        result.Receipt.Should().NotBeNull();
        result.Receipt!.Claim.Should().BeSameAs(claim);
        result.CleanupFailed.Should().BeFalse();
        var admitted = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
        admitted.NormalAttempts.Should().Be(1);
        admitted.CurrentAttempt.Should().Be(result.Receipt.AttemptId.Value);
        admitted.OwnershipKind.Should().Be("execution");
        admitted.Phase.Should().Be("executing");
        result.Receipt.NormalAttemptOrdinal.Should().Be(admitted.NormalAttempts);
        result.Receipt.OwnershipUntil.Should().Be(admitted.OwnershipUntil);
        await using (var read = await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await read.BeginTransactionAsync())
        {
            await EducationSchemaSeed.AnnounceAsync(read, transaction, SchemaFixture.TenantA, null);
            await using var exact = new NpgsqlCommand("SELECT admitted_at, deadline FROM background_job_attempts WHERE id=@attempt",
                (NpgsqlConnection)read, (NpgsqlTransaction)transaction);
            exact.Parameters.AddWithValue("attempt", result.Receipt.AttemptId.Value);
            await using var receiptRow = await exact.ExecuteReaderAsync();
            (await receiptRow.ReadAsync()).Should().BeTrue();
            result.Receipt.AdmittedAt.Should().Be(receiptRow.GetFieldValue<DateTimeOffset>(0));
            result.Receipt.Deadline.Should().Be(receiptRow.GetFieldValue<DateTimeOffset>(1),
                "business and runtime receive the actual immutable database deadline, never a new local timeout");
        }
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
        (await store.TryClaimAsync(job, 1)).Should().BeNull("even an execution lease cannot be claimed as routing");
        probe.Constructions.Should().BeEmpty();
        Fixture.RootConstructions.Should().Be(roots);
        host.Services.GetRequiredService<ITenantContextAccessor>().Current.Should().BeSameAs(original);
        Activity.Current.Should().BeSameAs(activity);
    }

    [Fact]
    public async Task The_preread_is_physically_readonly_RLS_scoped_and_framework_authority_is_sticky()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.OrganizationKey, SchemaFixture.OrgA1);
        var sibling = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.OrganizationKey, SchemaFixture.OrgA2);
        var otherTenant = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString, tenant: SchemaFixture.TenantB);
        var probe = new BackgroundJobFixtureProbe();
        var gate = new PermissiveGate();
        var reads = 0;
        var starts = 0;
        var observation = new BackgroundJobAdmissionObservation
        {
            OnRead = async (request, services) =>
            {
                reads++;
                var unit = services.GetRequiredService<IUnitOfWork>();
                (await BackgroundJobRuntimeRows.ReadInUnitAsync(unit, request.Claim.JobId)).Should().NotBeNull();
                (await BackgroundJobRuntimeRows.ReadInUnitAsync(unit, sibling)).Should().BeNull();
                (await BackgroundJobRuntimeRows.ReadInUnitAsync(unit, otherTenant)).Should().BeNull();
                await ProveReadOnlyWriteRefusalAsync(unit, job);
                await AssertFrameworkCeilingAsync(services, probe);
            },
            OnStart = async (_, services) =>
            {
                starts++;
                (await BackgroundJobRuntimeRows.ScalarInUnitAsync(services.GetRequiredService<IUnitOfWork>(),
                    "SHOW transaction_read_only")).Should().Be("off");
                await AssertFrameworkCeilingAsync(services, probe);
            },
        };
        await using var host = Fixture.Host(database, probe, configure: services =>
        {
            observation.Configure(services);
            services.AddSingleton<IPlatformAdminGate>(gate);
        });
        (await gate.IsPermittedAsync("positive-control")).Should().BeTrue();
        var claim = (await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, 1))!;
        (await host.Services.GetRequiredService<JobExecutor>().ExecuteAsync(claim)).Outcome.Should().Be(JobStartExecutionOutcome.Started);
        reads.Should().Be(1);
        starts.Should().Be(1);
        observation.ReadScopeDisposed.Should().BeTrue();
        observation.StartScopeDisposed.Should().BeTrue();
        gate.Calls.Should().Be(1, "the sticky ceiling refuses privileged scope entry before even a permissive gate");
        probe.Constructions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("pending")]
    [InlineData("capacity")]
    [InlineData("external")]
    [InlineData("early")]
    [InlineData("generation")]
    public async Task Unready_early_stale_and_capacity_bound_callbacks_spend_no_attempt(string reason)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var key = reason switch
        {
            "unknown" => "jobssubmissionfixture.unknown.v1",
            "pending" => BackgroundJobRuntimeFixture.PendingKey,
            "capacity" => BackgroundJobRuntimeFixture.CapacityKey,
            "external" => BackgroundJobRuntimeFixture.ExternalKey,
            _ => BackgroundJobRuntimeFixture.TenantKey,
        };
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString, key,
            due: reason == "early" ? DateTimeOffset.UtcNow.AddHours(1) : null,
            capacityGroup: reason is "capacity" or "external" ? BackgroundJobRuntimeFixture.CapacityGroup : null,
            profile: reason == "external" ? "external" : "transactional");
        var probe = new BackgroundJobFixtureProbe();
        await using var host = Fixture.Host(database, probe);
        var claim = await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>()
            .TryClaimAsync(job, reason == "generation" ? 2 : 1);
        claim.Should().BeNull();
        var row = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
        row.NormalAttempts.Should().Be(0);
        row.Phase.Should().Be("pending");
        row.OwnershipToken.Should().BeNull();
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(0);
        probe.Constructions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("extra_member")]
    [InlineData("missing_member")]
    [InlineData("noncanonical")]
    public async Task Invalid_scope_or_typed_envelope_cannot_construct_business_or_spend_an_attempt(string corruption)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var parameters = corruption switch
        {
            "extra_member" => "{\"ResourceId\":\"12345678-1234-7123-8123-123456789abc\",\"TenantId\":\"12345678-1234-7123-8123-123456789abc\"}",
            "missing_member" => "{}",
            "noncanonical" => "{ \"ResourceId\": \"12345678-1234-7123-8123-123456789abc\" }",
            _ => null,
        };
        var organization = corruption == "scope" ? SchemaFixture.OrgA1 : (Guid?)null;
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            organization: organization, parameters: parameters);
        var probe = new BackgroundJobFixtureProbe();
        var roots = Fixture.RootConstructions;
        await using var host = Fixture.Host(database, probe);
        var claim = (await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, 1))!;
        claim.Should().NotBeNull("the routing lane sees opaque metadata, while the app-role pre-read validates the envelope");
        var result = await host.Services.GetRequiredService<JobExecutor>().ExecuteAsync(claim);
        result.Outcome.Should().Be(JobStartExecutionOutcome.InvalidEnvelope);
        result.Receipt.Should().BeNull();
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job, organization)).Should().Be(0);
        probe.Constructions.Should().BeEmpty();
        Fixture.RootConstructions.Should().Be(roots);
    }

    [Fact]
    public async Task Exact_framework_requests_outside_their_capability_are_refused_before_framework_handler_construction()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var probe = new BackgroundJobFixtureProbe();
        var observation = new BackgroundJobAdmissionObservation();
        await using var host = Fixture.Host(database, probe, configure: observation.Configure);
        var claim = (await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, 1))!;
        await using var scope = host.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        (await sender.Send(new ReadBackgroundJobEnvelopeQuery(claim))).IsFailure.Should().BeTrue();
        var envelope = new BackgroundJobEnvelope(job, claim.TenantId, null, claim.DefinitionKey,
            "transactional", claim.Queue, null, "{}", claim.OriginalDueAt,
            BackgroundJobRuntimeFixture.Traceparent, null, null, null, null);
        (await sender.Send(new AdmitBackgroundJobAttemptCommand(claim, envelope))).IsFailure.Should().BeTrue();
        observation.ReadUnit.Should().BeNull();
        observation.StartUnit.Should().BeNull();
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(0);
    }

    private static async Task AssertFrameworkCeilingAsync(IServiceProvider services, BackgroundJobFixtureProbe probe)
    {
        var accessor = services.GetRequiredService<ITenantContextAccessor>();
        var saved = accessor.Current;
        accessor.Current = new BackgroundJobFixtureContext();
        try
        {
            var unit = services.GetRequiredService<IUnitOfWork>();
            var transaction = unit.Transaction;
            var generation = unit.GetTenantContextAnnouncement(transaction);
            var called = false;
            var legacy = () => services.GetRequiredService<TransactionExecutionCoordinator>().ExecuteAsync(
                null, TransactionMode.ReadWrite, "unregistered-framework-child", () =>
                {
                    called = true;
                    return Task.FromResult(Result.Ok(None.Value));
                });
            await legacy.Should().ThrowAsync<InvalidOperationException>().WithMessage("*background-job authority*");
            var provisioning = () => services.GetRequiredService<TransactionExecutionCoordinator>().ExecuteAsync(
                new ProvisioningProbe(), TransactionMode.ReadWrite, "unregistered-provisioning-child",
                () => Task.FromResult(Result.Ok(None.Value)));
            await provisioning.Should().ThrowAsync<InvalidOperationException>().WithMessage("*background-job authority*");
            called.Should().BeFalse();
            unit.Transaction.Should().BeSameAs(transaction, "refusal occurs before any mixed or joined transaction frame");
            unit.GetTenantContextAnnouncement(transaction).Should().Be(generation);
            unit.IsRollbackOnly.Should().BeFalse();
            (await Fixture.SendAsync(services)).IsFailure.Should().BeTrue("accessor replacement cannot remove framework confinement");
            probe.Constructions.Should().BeEmpty("ordinary handler construction follows admission");
            await FluentActions.Awaiting(() => services.GetRequiredService<IPlatformAdminScope>().EnterAsync("framework-proof"))
                .Should().ThrowAsync<InvalidOperationException>().WithMessage("*background-job authority*");
            var privileged = services.GetRequiredKeyedService<PrivilegedDataSource>(OutboxDispatcherDataSource.Key);
            await FluentActions.Awaiting(async () => await privileged.OpenConnectionAsync())
                .Should().ThrowAsync<InvalidOperationException>().WithMessage("*background-job authority*");
        }
        finally { accessor.Current = saved; }
    }

    private static async Task ProveReadOnlyWriteRefusalAsync(IUnitOfWork unit, BackgroundJobId job)
    {
        await SchemaQueries.ExecuteAsync(unit.Connection, unit.Transaction, "SAVEPOINT readonly_probe");
        var write = () => SchemaQueries.ExecuteAsync(unit.Connection, unit.Transaction,
            "UPDATE background_jobs SET phase = phase WHERE id = @id", ("id", job.Value));
        (await write.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.ReadOnlySqlTransaction);
        await SchemaQueries.ExecuteAsync(unit.Connection, unit.Transaction, "ROLLBACK TO SAVEPOINT readonly_probe");
    }

    private sealed class PermissiveGate : IPlatformAdminGate
    {
        internal int Calls { get; private set; }
        public ValueTask<bool> IsPermittedAsync(string reason, CancellationToken cancellationToken = default)
        { Calls++; return ValueTask.FromResult(true); }
    }

    private sealed class ProvisioningProbe : IProvisionsTenant
    {
        public TenantId ProvisioningTenantId => TenantId.From(SchemaFixture.TenantA);
    }
}
