using LearnStack.Application.Jobs;
using System.Collections.Concurrent;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Actual app-role admission serializes tenant-wide occupancy and consumes the real scoped feature-limit path.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class BackgroundJobCapacityAdmissionTests(SchemaFixture schema)
{
    private static BackgroundJobRuntimeFixture Fixture => BackgroundJobRuntimeFixture.Shared;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(false, null, 3)]
    [InlineData(true, null, 1)]
    [InlineData(true, -1L, 3)]
    [InlineData(true, 0L, 0)]
    [InlineData(true, -2L, 0)]
    [InlineData(true, 1L, 1)]
    [InlineData(true, 2L, 2)]
    [InlineData(true, 3L, 3)]
    [InlineData(true, long.MaxValue, 3)]
    public async Task Admission_intersects_optional_Hard_projection_with_operational_ceiling_before_counting_a_start(
        bool planLimit, long? projected, int accepted)
    {
        await using var database = await DatabaseAsync();
        var provider = new CapacityEntitlementProvider(projected);
        var probe = new BackgroundJobFixtureProbe();
        await using var host = CapacityHost(database, probe, provider, 3, planLimit ? LimitKeys.MaxOrganizations : null);
        var starts = new List<JobStartExecutionResult>();
        for (var index = 0; index < 4; index++)
        {
            var job = await CapacityJobAsync(database);
            var start = await StartAsync(host.Services, job);
            start.CleanupFailed.Should().BeFalse();
            start.Outcome.Should().Be(index < accepted ? JobStartExecutionOutcome.Started : JobStartExecutionOutcome.CapacityDeferred);
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(index < accepted ? 1 : 0);
            starts.Add(start);
        }
        starts.Count(start => start.Receipt is not null).Should().Be(accepted);
        (await OccupiedAsync(database)).Should().Be(accepted);
        probe.Constructions.Should().BeEmpty("committed admission is still distinct from constructing a business handler");
        provider.Tenants.Should().HaveCount(planLimit ? 4 : 0);
        if (planLimit)
            provider.Tenants.Should().OnlyContain(tenant => tenant == TenantId.From(SchemaFixture.TenantA));
        else
            provider.Tenants.Should().BeEmpty("an unconfigured plan limit never calls the provider");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Faulted_or_canceled_limit_lookup_spends_no_attempt_and_never_constructs_business(bool canceled)
    {
        await using var database = await DatabaseAsync();
        var provider = new CapacityEntitlementProvider(1)
        {
            OnLookup = (_, _) => canceled ? Task.FromCanceled(new CancellationToken(true))
                : Task.FromException(new IOException("private entitlement failure")),
        };
        var probe = new BackgroundJobFixtureProbe();
        await using var host = CapacityHost(database, probe, provider, 1, LimitKeys.MaxOrganizations);
        var job = await CapacityJobAsync(database);
        var start = await StartAsync(host.Services, job);
        start.Outcome.Should().Be(JobStartExecutionOutcome.CapacityDeferred);
        start.Receipt.Should().BeNull();
        start.CleanupFailed.Should().BeFalse();
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(0);
        (await OccupiedAsync(database)).Should().Be(0);
        probe.Constructions.Should().BeEmpty();
    }

    [Fact]
    public async Task Caller_cancellation_retains_the_actual_limit_task_and_cannot_admit_after_its_late_return()
    {
        await using var database = await DatabaseAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new CapacityEntitlementProvider(1)
        {
            OnLookup = async (_, _) => { entered.TrySetResult(); await release.Task; },
        };
        var probe = new BackgroundJobFixtureProbe();
        await using var host = CapacityHost(database, probe, provider, 1, LimitKeys.MaxOrganizations);
        var job = await CapacityJobAsync(database);
        var claim = (await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, 1))!;
        using var stopping = new CancellationTokenSource();
        var admission = host.Services.GetRequiredService<JobExecutor>().ExecuteAsync(claim, BackgroundJobTestRun.Create(), cancellationToken: stopping.Token);
        try
        {
            await entered.Task.WaitAsync(Budget);
            await stopping.CancelAsync();
            admission.IsCompleted.Should().BeFalse("canceling a caller wait never abandons the actual provider lookup or its main owner");
        }
        finally { release.TrySetResult(); }
        var completed = await admission.WaitAsync(Budget);
        completed.Outcome.Should().Be(JobStartExecutionOutcome.Canceled);
        completed.Receipt.Should().BeNull();
        completed.CleanupFailed.Should().BeFalse();
        completed.SourceOwner.CleanupConfirmed.Should().BeTrue();
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(0);
        (await OccupiedAsync(database)).Should().Be(0);
        probe.Constructions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_reservation_INSERT_fault_rolls_back_the_already_staged_attempt_and_summary()
    {
        await using var database = await DatabaseAsync();
        var job = await CapacityJobAsync(database);
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
            await SchemaQueries.ExecuteAsync(owner, null,
                $"ALTER TABLE background_job_reservations ADD CONSTRAINT reject_capacity_admission CHECK (job_id <> '{job.Value:D}'::uuid)");
        var probe = new BackgroundJobFixtureProbe();
        await using var host = CapacityHost(database, probe, new(-1));
        var completed = await StartAsync(host.Services, job);
        completed.Outcome.Should().Be(JobStartExecutionOutcome.Failed);
        completed.Receipt.Should().BeNull();
        completed.CleanupFailed.Should().BeFalse();
        var row = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
        row.NormalAttempts.Should().Be(0);
        row.CurrentAttempt.Should().BeNull();
        row.Phase.Should().Be("pending");
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(0);
        (await OccupiedAsync(database)).Should().Be(0);
        probe.Constructions.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_independent_hosts_and_organizations_count_the_reservation_committed_during_the_actual_mutex_wait(bool seedMutex)
    {
        await using var database = await DatabaseAsync();
        if (seedMutex)
        {
            await using var seed = await PostgresFixture.OpenAsync(database.AppConnectionString);
            await using var transaction = await seed.BeginTransactionAsync();
            await EducationSchemaSeed.AnnounceAsync(seed, transaction, SchemaFixture.TenantA, null);
            await SchemaQueries.ExecuteAsync(seed, transaction,
                "INSERT INTO background_job_capacity (tenant_id, capacity_group) VALUES (@tenant,@group)",
                ("tenant", SchemaFixture.TenantA), ("group", BackgroundJobRuntimeFixture.CapacityGroup));
            await transaction.CommitAsync();
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstProvider = new CapacityEntitlementProvider(1)
        {
            OnLookup = async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); },
        };
        var firstProbe = new BackgroundJobFixtureProbe();
        var secondProbe = new BackgroundJobFixtureProbe();
        var applicationName = "capacity_wait_" + Guid.NewGuid().ToString("N");
        await using var first = CapacityHost(database, firstProbe, firstProvider, 1, LimitKeys.MaxOrganizations);
        await using var second = CapacityHost(database, secondProbe, new CapacityEntitlementProvider(1), 1,
            LimitKeys.MaxOrganizations, applicationName);
        var a1 = await CapacityJobAsync(database, SchemaFixture.OrgA1);
        var a2 = await CapacityJobAsync(database, SchemaFixture.OrgA2);
        var one = StartAsync(first.Services, a1);
        Task<JobStartExecutionResult>? two = null;
        try
        {
            await entered.Task.WaitAsync(Budget);
            two = StartAsync(second.Services, a2);
            await WaitForCapacityMutexAsync(database, applicationName);
            two.IsCompleted.Should().BeFalse("the second independent instance owns its job but waits for the shared tenant/group mutex");
        }
        finally { release.TrySetResult(); }
        var firstStart = await one.WaitAsync(Budget);
        var secondStart = await two!.WaitAsync(Budget);
        firstStart.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        secondStart.Outcome.Should().Be(JobStartExecutionOutcome.CapacityDeferred,
            "a fresh count after lock acquisition must see the first instance's new committed reservation");
        (await OccupiedAsync(database, SchemaFixture.OrgA1)).Should().Be(1);
        (await OccupiedAsync(database, SchemaFixture.OrgA2)).Should().Be(1,
            "tenant-wide occupancy counts a sibling organization's reservation under real RLS");
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, a1, SchemaFixture.OrgA1)).Should().Be(1);
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, a2, SchemaFixture.OrgA2)).Should().Be(0);
        firstProbe.Constructions.Should().BeEmpty();
        secondProbe.Constructions.Should().BeEmpty();
        var completed = await first.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(firstStart.Receipt!, firstStart.Receipt!.Run.OpenBoundary(BackgroundJobCheckpointKind.Transactional));
        completed.CommitAcknowledged.Should().BeTrue();
        firstProbe.Constructions.Should().Equal("OrganizationRoot");
        (await OccupiedAsync(database, SchemaFixture.OrgA2)).Should().Be(0);
    }

    [Fact]
    public async Task A_downgrade_preserves_existing_occupancy_and_each_new_admission_reads_the_current_source_answer()
    {
        await using var database = await DatabaseAsync();
        var provider = new CapacityEntitlementProvider(2);
        await using var host = CapacityHost(database, new(), provider, 3, LimitKeys.MaxOrganizations);
        var first = await StartAsync(host.Services, await CapacityJobAsync(database));
        var second = await StartAsync(host.Services, await CapacityJobAsync(database));
        first.Receipt.Should().NotBeNull();
        second.Receipt.Should().NotBeNull();
        provider.Limit = 1;
        (await StartAsync(host.Services, await CapacityJobAsync(database))).Outcome.Should().Be(JobStartExecutionOutcome.CapacityDeferred);
        (await OccupiedAsync(database)).Should().Be(2);
        (await host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(first.Receipt!, first.Receipt!.Run.OpenBoundary(BackgroundJobCheckpointKind.Transactional))).CommitAcknowledged.Should().BeTrue();
        (await StartAsync(host.Services, await CapacityJobAsync(database))).Outcome.Should().Be(JobStartExecutionOutcome.CapacityDeferred);
        provider.Limit = 0;
        (await host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(second.Receipt!, second.Receipt!.Run.OpenBoundary(BackgroundJobCheckpointKind.Transactional))).CommitAcknowledged.Should().BeTrue();
        (await StartAsync(host.Services, await CapacityJobAsync(database))).Outcome.Should().Be(JobStartExecutionOutcome.CapacityDeferred);
        (await OccupiedAsync(database)).Should().Be(0);
        provider.Limit = 1;
        (await StartAsync(host.Services, await CapacityJobAsync(database))).Outcome.Should().Be(JobStartExecutionOutcome.Started);
        (await OccupiedAsync(database)).Should().Be(1);
    }

    [Fact]
    public async Task Expired_unknown_external_occupancy_still_blocks_its_tenant_while_another_tenant_progresses()
    {
        await using var database = await DatabaseAsync();
        // Storage/capacity proof only: Step 7's external engine remains refused.
        var unknown = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        var attempt = Guid.CreateVersion7();
        var operation = Guid.CreateVersion7();
        await BackgroundJobSchemaSeed.ClaimAsync(database.OutboxConnectionString, unknown.Value, Guid.NewGuid());
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
            await SchemaQueries.ExecuteAsync(app, transaction, """
                INSERT INTO background_job_attempts (id, tenant_id, job_id, stage, ordinal, admission_token, deadline)
                    SELECT @attempt, tenant_id, id, 'normal', 1, ownership_token, clock_timestamp()+interval '1 second'
                    FROM background_jobs WHERE id=@job;
                UPDATE background_jobs SET normal_attempts=1, current_attempt_id=@attempt,
                    attempt_deadline=(SELECT deadline FROM background_job_attempts WHERE id=@attempt), phase='preparing' WHERE id=@job;
                INSERT INTO background_job_capacity (tenant_id, capacity_group) VALUES (@tenant,@group);
                INSERT INTO background_job_reservations (tenant_id, capacity_group, job_id, attempt_id)
                    VALUES (@tenant,@group,@job,@attempt);
                UPDATE background_jobs SET plan='{}', plan_fingerprint=repeat('a',64), prepared_at=clock_timestamp(),
                    operation_id=@operation, phase='prepared' WHERE id=@job;
                UPDATE background_job_attempts SET plan_fingerprint=repeat('a',64), operation_id=@operation,
                    execute_intent_at=clock_timestamp() WHERE id=@attempt;
                UPDATE background_jobs SET phase='executing' WHERE id=@job;
                """, ("attempt", attempt), ("job", unknown.Value), ("tenant", SchemaFixture.TenantA),
                ("group", BackgroundJobRuntimeFixture.CapacityGroup), ("operation", operation));
            await transaction.CommitAsync();
        }
        await using (var control = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.OutboxConnectionString))
        {
            using var deadline = new CancellationTokenSource(Budget);
            while (true)
            {
                await using var expired = new NpgsqlCommand("SELECT attempt_deadline<clock_timestamp() FROM background_jobs WHERE id=@job", control);
                expired.Parameters.AddWithValue("job", unknown.Value);
                if (await expired.ExecuteScalarAsync(deadline.Token) is true) break;
                await Task.Delay(10, deadline.Token);
            }
        }
        await using var host = CapacityHost(database, new(), new(-1));
        var refused = await CapacityJobAsync(database);
        (await StartAsync(host.Services, refused)).Outcome.Should().Be(JobStartExecutionOutcome.CapacityDeferred);
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, refused)).Should().Be(0);
        (await OccupiedAsync(database)).Should().Be(1, "expiry, missing vendor hints and an unresolved effect never free occupancy");
        var other = await CapacityJobAsync(database, tenant: SchemaFixture.TenantB);
        var admitted = await StartAsync(host.Services, other);
        admitted.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        (await OccupiedAsync(database, tenant: SchemaFixture.TenantB)).Should().Be(1);
        (await host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(admitted.Receipt!, admitted.Receipt!.Run.OpenBoundary(BackgroundJobCheckpointKind.Transactional))).CommitAcknowledged.Should().BeTrue();
        (await OccupiedAsync(database, tenant: SchemaFixture.TenantB)).Should().Be(0);
        (await OccupiedAsync(database)).Should().Be(1);
    }

    private async Task<DisposableSchemaDatabase> DatabaseAsync()
    {
        var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        return database;
    }

    private static WebApplicationFactory<Program> CapacityHost(DisposableSchemaDatabase database,
        BackgroundJobFixtureProbe probe, CapacityEntitlementProvider provider, int ceiling = 1, LimitKey? limit = null,
        string? applicationName = null) => Fixture.Host(database, probe, configure: services =>
        {
            services.RemoveAll<BackgroundJobDefinitionRegistry>();
            services.AddSingleton(Fixture.CapacityRegistry(ceiling, limit));
            services.RemoveAll<IEntitlementProvider>();
            services.AddSingleton<IEntitlementProvider>(provider);
        }, appConnectionString: applicationName is null ? null : new NpgsqlConnectionStringBuilder(database.AppConnectionString)
        { ApplicationName = applicationName }.ConnectionString);

    private static Task<BackgroundJobId> CapacityJobAsync(DisposableSchemaDatabase database, Guid? organization = null,
        Guid? tenant = null) => BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
        organization is null ? BackgroundJobRuntimeFixture.CapacityKey : BackgroundJobRuntimeFixture.OrganizationKey,
        organization, tenant, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup);

    private static async Task<JobStartExecutionResult> StartAsync(IServiceProvider services, BackgroundJobId job)
    {
        var claim = await services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(job, 1);
        claim.Should().NotBeNull();
        return await services.GetRequiredService<JobExecutor>().ExecuteAsync(claim!, BackgroundJobTestRun.Create());
    }

    private static async Task<long> OccupiedAsync(DisposableSchemaDatabase database, Guid? organization = null,
        Guid? tenant = null)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(connection, transaction, tenant ?? SchemaFixture.TenantA, organization);
        await SchemaQueries.AssertApplicationRoleAsync(connection, transaction);
        await using var count = new NpgsqlCommand("""
            SELECT count(*) FROM background_job_reservations
            WHERE tenant_id=@tenant AND capacity_group=@group AND released_at IS NULL
            """, connection, transaction);
        count.Parameters.AddWithValue("tenant", tenant ?? SchemaFixture.TenantA);
        count.Parameters.AddWithValue("group", BackgroundJobRuntimeFixture.CapacityGroup);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private static async Task WaitForCapacityMutexAsync(DisposableSchemaDatabase database, string applicationName)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (true)
        {
            await using var query = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname=current_database()
                    AND usename='learnstack_app' AND application_name=@application AND wait_event_type='Lock'
                    AND query LIKE '%background_job_capacity%'
                    AND (query LIKE '%FOR UPDATE%' OR query LIKE '%INSERT INTO background_job_capacity%'))
                """, connection);
            query.Parameters.AddWithValue("application", applicationName);
            if (await query.ExecuteScalarAsync(deadline.Token) is true) return;
            await Task.Delay(10, deadline.Token);
        }
    }
}

/// <summary>Fixture-only plan projection; actual scoped FeatureFlags supplies the trusted tenant.</summary>
internal sealed class CapacityEntitlementProvider(long? limit) : IEntitlementProvider
{
    internal long? Limit { get; set; } = limit;
    internal ConcurrentQueue<TenantId> Tenants { get; } = new();
    internal Func<TenantId, CancellationToken, Task>? OnLookup { get; init; }
    public async Task<EntitlementProjection> GetAsync(TenantId tenantId, CancellationToken ct = default)
    {
        Tenants.Enqueue(tenantId);
        if (OnLookup is { } lookup) await lookup(tenantId, ct);
        return new(tenantId, "fixture", new Dictionary<string, bool>(),
            Limit is { } value ? new Dictionary<string, long> { [LimitKeys.MaxOrganizations.Value] = value }
                : new Dictionary<string, long>(), ComplianceCaps.None, null, null, 1);
    }
    public Task<EntitlementRefreshOutcome> RefreshAsync(EntitlementProjection projection, CancellationToken ct = default) =>
        Task.FromResult(EntitlementRefreshOutcome.IgnoredAsStale);
}
