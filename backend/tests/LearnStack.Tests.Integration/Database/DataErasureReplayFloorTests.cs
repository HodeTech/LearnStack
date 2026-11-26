using System.Diagnostics;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Application.Messaging;
using LearnStack.Infrastructure.Identity.ProviderEvents;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Platform;
using LearnStack.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.DataRequestSchemaSeed;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0107 § 9 and ADR-0103 Amendment 2 for a tenant-erased person, through the actual provider-event path, the actual
/// erasure job and the actual operator recovery owner: an erasure never rewrites or deletes an outbox, delivery, inbox or
/// job row, and a replay of a delivery the handler never completed re-runs the real handler, whose own subject rule does
/// not refuse the person because their User stays Active, so it writes the person's audit observation again. Those rows
/// are retained transport evidence, not undone.
/// </summary>
/// <remarks>
/// The subject-linked delivery is the Identity provider-event mirror, <c>audit.provider-event-mirror.v1</c>: it is the one
/// subscription in the composition root whose event names a person (its retained target carries
/// <c>affected_user_id</c>), and the whole production chain from a provider fact to the Audit observation runs against a
/// plain database with no hand-built transport row. Two facts about person A are mirrored: the first completes (outbox
/// row processed, delivery processed, inbox row, observation), the second is made to fail durably at the audit write and
/// is then dead-lettered, so a replay is possible. The Closing and Closed User half of the floor is in the class's closure
/// part.
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class DataErasureReplayFloorTests(SchemaFixture fixture)
{
    private const string Subscription = "audit.provider-event-mirror.v1";
    private static readonly string[] Tables = ["outbox_messages", "event_deliveries", "inbox_messages", "background_jobs", "provider_targets"];

    [Fact]
    public async Task A_tenant_erasure_leaves_the_transport_trail_untouched_and_a_replay_writes_for_the_erased_person_as_retained_transport_evidence()
    {
        await using var world = await ArrangeAsync();

        world.Before["outbox_messages"].Should().StartWith("2:", "both provider facts about the person were produced");
        world.Before["event_deliveries"].Should().StartWith("2:");
        world.Before["inbox_messages"].Should().StartWith("1:", "only the completed delivery left an inbox row");
        world.Before["background_jobs"].Should().StartWith("1:", "the person's export job, not the erasure job");
        world.Before["provider_targets"].Should().StartWith("2:");
        world.After.Should().BeEquivalentTo(world.Before, "an erasure never rewrites or deletes an outbox, delivery, inbox or job row");
        (await ObservationsAsync(world, world.CompletedFact)).Should().Be(1);
        (await ObservationsAsync(world, world.FailedFact)).Should().Be(0, "its handler never completed");
        (await DeliveryAsync(world)).Should().Be(new DeliveryState(0, "retry_exhausted", true));
        (await PersonStateAsync(world)).Should().Be(("Active", "Revoked", "Active", "Active"),
            "the person's User stays Active and only the tenant-wide Membership of the request's group was erased");

        // The operator's completion, through the real platform scope of the API composition, redacts the audit log and
        // completes the request; it is the last act before a replay, and it too rewrites no transport row.
        (await RequestStatusAsync(world)).Should().Be("awaiting_privileged_step");
        var retained = await ObservationAsync(world, world.CompletedFact);
        retained.Should().NotBeNull("the completed delivery's observation exists before the redaction");
        await using (var scope = world.Host.Services.CreateAsyncScope())
        {
            var completion = await scope.ServiceProvider.GetRequiredService<IDataRequestOperatorOperations>().CompleteErasureAsync(
                TestPlatformAdmissions.Admission(PlatformOperations.ErasureComplete, "INC-REPLAY-FLOOR",
                    correlationId: "00-replay-floor-complete-" + Guid.NewGuid().ToString("N")), world.ErasureRequest);
            completion.Outcome.Should().Be(ErasureCompletionOutcome.Completed);
            completion.Report!.RowsSelected.Should().BeGreaterThan(0, "the redaction ran over the person's audit rows");
        }

        (await RequestStatusAsync(world)).Should().Be("completed", "the privileged step finished before the replay");
        var completed = await TrailAsync(world);
        completed.Should().BeEquivalentTo(world.Before,
            "the operator's completion redacts audit payloads and rewrites no outbox, delivery, inbox or job row");
        (await ObservationsAsync(world, world.CompletedFact)).Should().Be(1, "the completion does not delete the retained observation");
        (await ObservationsAsync(world, world.FailedFact)).Should().Be(0, "no observation was written by the redaction");
        (await DeliveryAsync(world)).Should().Be(new DeliveryState(0, "retry_exhausted", true));

        var fence = await InspectedFenceAsync(world);
        var replay = await world.Host.Services.GetRequiredService<IOperatorRecovery>().ReplayDeliveryAsync(
            Admission(PlatformOperations.DeliveryReplay), new DeliveryReplayRequest(SchemaFixture.TenantA, Subscription,
                world.FailedEvent, fence.ReplayCount, fence.DeadLetteredAt!.Value, fence.TerminalReason!));
        replay.Should().Be(new OperatorRecoveryResult(OperatorRecoveryOutcome.Applied, 1),
            "the erasure did not reset replay and the Active tenant admits it");

        var store = world.Host.Services.GetRequiredService<DeliveryDispatchStore>();
        var claim = await store.TryClaimAsync([]);
        claim.Should().NotBeNull("the reset row is due at once");
        claim!.EventId.Should().Be(world.FailedEvent);
        var gate = new DeliveryCommitGate();
        var execution = await world.Host.Services.GetRequiredService<DeliveryExecutor>().ExecuteAsync(claim, gate);

        execution.Should().Be(new DeliveryExecutionResult(DeliveryExecutionOutcome.Completed, true, false),
            "the handler re-ran for the erased person and its subject rule did not refuse");
        gate.CommitAcknowledged.Should().BeTrue();
        (await ObservationsAsync(world, world.FailedFact)).Should().Be(1,
            "the replay wrote the erased person's audit observation again; it is retained transport evidence");
        (await ObservationsAsync(world, world.CompletedFact)).Should().Be(1, "the completed delivery is not re-run");
        var written = await ObservationAsync(world, world.FailedFact);
        written.Should().NotBeNull();
        written!.EntityType.Should().Be("User");
        written.EntityId.Should().Be(PersonA.ToString("D"));
        written.AffectedUser.Should().Be(PersonA.ToString("D"),
            "the replayed handler writes the person's identifier into its metadata as it always does; the floor is not undone");
        written.ProviderEvent.Should().Be(world.FailedFact.ToString("D"));
        written.Kind.Should().Be("Logout");
        written.TargetId.Should().Be(world.FailedTarget.ToString("D"));
        written.Operation.Should().Be("audit.provider_event.observe");
        (await ObservationAsync(world, world.CompletedFact)).Should().Be(retained,
            "the replay neither rewrites nor re-redacts the observation that was already there");
        (await RequestStatusAsync(world)).Should().Be("completed", "the replay does not reopen the completed request");
        (await DeliveryAsync(world)).Should().Be(new DeliveryState(1, null, false), "replay advanced once; nothing is dead-lettered or failed");
        var after = await TrailAsync(world);
        after["outbox_messages"].Should().Be(world.Before["outbox_messages"]);
        after["background_jobs"].Should().Be(world.Before["background_jobs"]);
        after["provider_targets"].Should().Be(world.Before["provider_targets"]);
        after["inbox_messages"].Should().StartWith("2:", "the replayed handler's inbox row is the only addition");
        (await PersonStateAsync(world)).Should().Be(("Active", "Revoked", "Active", "Active"));
    }

    [Fact]
    public async Task A_replay_after_a_tenant_erasure_is_still_refused_while_the_tenant_is_not_serving()
    {
        await using var world = await ArrangeAsync();
        await SetTenantStatusAsync(world.Database, "Suspended");
        var fence = await InspectedFenceAsync(world);
        var before = await TrailAsync(world);

        var refused = await world.Host.Services.GetRequiredService<IOperatorRecovery>().ReplayDeliveryAsync(
            Admission(PlatformOperations.DeliveryReplay), new DeliveryReplayRequest(SchemaFixture.TenantA, Subscription,
                world.FailedEvent, fence.ReplayCount, fence.DeadLetteredAt!.Value, fence.TerminalReason!));

        (refused.Outcome, refused.Refusal).Should().Be((OperatorRecoveryOutcome.NotRecoverable, "tenant_not_serving"),
            "the replay floor does not weaken ADR-0103's Active-tenant guard");
        (await TrailAsync(world)).Should().BeEquivalentTo(before);
        (await DeliveryAsync(world)).Should().Be(new DeliveryState(0, "retry_exhausted", true));
    }

    /// <summary>
    /// Two provider facts about person A run through the real intake, producer, outbox, dispatcher, inbox and Audit
    /// handler; the second fails durably, is dead-lettered and kept; the person's export job row is stored; and the real
    /// tenant-wide erasure job then runs, with the transport trail captured before and after it.
    /// </summary>
    private async Task<World> ArrangeAsync()
    {
        var world = await ArrangeDeliveriesAsync();
        var database = world.Database;
        try
        {
            await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString, "identity.data_export_tenant.v1",
                    tenant: SchemaFixture.TenantA, parameters: BackgroundJobPayloadContract.Serialize(
                        new DataExportJobParameters(TenantWideA.Request), typeof(DataExportJobParameters),
                        BackgroundJobPayloadKind.Parameters));
            var erasure = await FileDueErasureAsync(database);
            world.ErasureRequest = erasure;
            world.ErasureJob = await DataErasureTestKit.SubmitAsync(database, SchemaFixture.TenantA, null, erasure);
            world.Host = CreateHost(database);
            world.Before = await TrailAsync(world);

            var executor = world.Host.Services.GetRequiredService<JobExecutor>();
            var start = await executor.ExecuteAsync(
                await DataExportTestKit.RoutingClaim.ClaimAsync(database.OutboxConnectionString, world.ErasureJob),
                BackgroundJobTestRun.Create());
            start.Outcome.Should().Be(JobStartExecutionOutcome.Started);
            (await executor.ExecuteBusinessAsync(start.Receipt!, start.Receipt!.Run.OpenBoundary(BackgroundJobCheckpointKind.Transactional)))
                .Committed();

            world.After = await TrailAsync(world);
            return world;
        }
        catch
        {
            await world.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The two provider facts about person A through the real intake, producer, outbox, dispatcher, inbox and Audit handler:
    /// the first completes and the second is dead-lettered as exhausted with its handler never completed. Tenant A is
    /// left Active, so a replay of the second is admitted.
    /// </summary>
    private async Task<World> ArrangeDeliveriesAsync()
    {
        var database = await DataExportTestKit.CreateSeededDatabaseAsync(fixture.Postgres);
        var at = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());
        var key = IdentitySubjectKey.Compute(Authority.AuthorityId, "fixture-subject-" + PersonA.ToString("N"));
        var completed = new IdentityProviderEvent(Guid.NewGuid(), at, IdentityProviderEventKind.Login, key);
        var failed = new IdentityProviderEvent(Guid.NewGuid(), at.AddMilliseconds(1), IdentityProviderEventKind.Logout, key);
        var world = new World(database, completed.SourceEventId, failed.SourceEventId);
        try
        {
            const string refusal = "refuse_replay_floor_observation";
            await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString))
            {
                await ExecuteAsync(owner, null, $"""
                    ALTER TABLE public.audit_log ADD CONSTRAINT {refusal}
                    CHECK (NOT (operation = 'audit.provider_event.observe'
                        AND metadata->'provider_event'->>'provider_event_id' = '{failed.SourceEventId:D}')) NOT VALID
                    """);
                await using (var api = new ProviderApi(fixture.Postgres, database, new Provider(completed, failed)))
                {
                    _ = api.Services;
                    await WaitAsync(async () => await ObservationsAsync(world, completed.SourceEventId) == 1
                        && await FailedDeliveryAttemptedAsync(world));
                }

                world.FailedEvent = await EventOfAsync(world, failed.SourceEventId);
                world.FailedTarget = await TargetOfAsync(world, failed.SourceEventId);
                // The real dispatcher stopped with the host above, but it may have exhausted the failing delivery in the moment
                // between the first failed attempt the wait saw and that stop. So the arrangement asserts the state it needs,
                // not the transition into it: this UPDATE dead-letters the delivery as the dispatcher would when it is still
                // retrying (one row) and finds nothing to do when the dispatcher already did (no row); either way the delivery
                // ends dead-lettered as exhausted with its handler never completed.
                await using (var dispatcher = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.OutboxConnectionString))
                {
                    (await ScalarAsync<long>(dispatcher, null, """
                        WITH moved AS (UPDATE public.event_deliveries SET terminal_reason = 'retry_exhausted',
                                dead_lettered_at = clock_timestamp(), locked_by = NULL, locked_until = NULL
                            WHERE tenant_id = @tenant AND subscription_id = @subscription AND event_id = @event
                                AND processed_at IS NULL AND dead_lettered_at IS NULL RETURNING 1)
                        SELECT count(*) FROM moved
                        """, ("tenant", SchemaFixture.TenantA), ("subscription", Subscription), ("event", world.FailedEvent)))
                        .Should().BeLessThanOrEqualTo(1);
                    (await ScalarAsync<long>(dispatcher, null, """
                        SELECT count(*) FROM public.event_deliveries
                        WHERE tenant_id = @tenant AND subscription_id = @subscription AND event_id = @event
                            AND processed_at IS NULL AND dead_lettered_at IS NOT NULL AND terminal_reason = 'retry_exhausted'
                            AND locked_by IS NULL
                        """, ("tenant", SchemaFixture.TenantA), ("subscription", Subscription), ("event", world.FailedEvent)))
                        .Should().Be(1, "the failed delivery is dead-lettered as exhausted, with its handler never completed");
                }

                await ExecuteAsync(owner, null, $"ALTER TABLE public.audit_log DROP CONSTRAINT {refusal}");
            }

            await SetTenantStatusAsync(database, "Active");
            return world;
        }
        catch
        {
            await world.DisposeAsync();
            throw;
        }
    }

    private static WebApplicationFactory<Program> CreateHost(DisposableSchemaDatabase database) =>
        new ConstructionOnlyApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("ConnectionStrings:Default", database.AppConnectionString);
            builder.UseSetting("ConnectionStrings:OutboxDispatcher", database.OutboxConnectionString);
            builder.UseSetting("ConnectionStrings:PlatformAdmin", database.PlatformConnectionString);
        });

    /// <summary>A self-filed tenant-wide erasure of person A whose cancellable window has already ended.</summary>
    private static async Task<Guid> FileDueErasureAsync(DisposableSchemaDatabase database)
    {
        var id = Guid.CreateVersion7();
        await using (var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await FenceAsync(app, transaction, SchemaFixture.TenantA, null, PersonA);
            await InsertRequestAsync(app, transaction, id, SchemaFixture.TenantA, null, PersonA, [LearnerMembershipA], "erasure");
            await transaction.CommitAsync();
        }

        await DataErasureTestKit.BackdateAsync(database.MigrationConnectionString, SchemaFixture.TenantA, null, id);
        return id;
    }

    private static PlatformAdmission Admission(PlatformOperation operation) =>
        TestPlatformAdmissions.Admission(operation, null, correlationId: "00-replay-floor-" + Guid.NewGuid().ToString("N"));

    /// <summary>The fence an operator reads from the inspection of dead-lettered deliveries.</summary>
    private static async Task<EventDeliveryInspection> InspectedFenceAsync(World world)
    {
        await using var scope = world.Host.Services.CreateAsyncScope();
        var page = await scope.ServiceProvider.GetRequiredService<IDeliveryInspection>().InspectDeliveriesAsync(
            Admission(PlatformOperations.DeliveryInspect), DeliveryInspectionState.DeadLettered, null, 20);
        return page!.Items.Should().ContainSingle(item => item.EventId == world.FailedEvent && item.SubscriptionId == Subscription)
            .Which;
    }

    /// <summary>The count and a digest of every row of each transport store the erasure must not touch.</summary>
    private static async Task<Dictionary<string, string>> TrailAsync(World world)
    {
        var trail = new Dictionary<string, string>();
        // The erasure job's own row is the runtime's to advance; every other job row, the person's export job included, is not.
        var statements = new[]
        {
            ("outbox_messages", "public.outbox_messages", "t.id", "true"),
            ("event_deliveries", "public.event_deliveries", "t.tenant_id, t.subscription_id, t.event_id", "true"),
            ("inbox_messages", "public.inbox_messages", "t.tenant_id, t.subscription_id, t.event_id", "true"),
            ("background_jobs", "public.background_jobs", "t.id", "t.id <> @erasureJob"),
        };
        // The table owner reads every column but is subject to the forced row policy, so it announces the tenant, once for
        // its tenant-wide rows and once for each organization's (a closure's fan-out writes rows in both); the dispatcher
        // role bypasses the policy and can only count, which proves the announcements together hid no row.
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(world.Database.MigrationConnectionString);
        await using var dispatcher = (NpgsqlConnection)await PostgresFixture.OpenAsync(world.Database.OutboxConnectionString);
        foreach (var (name, table, order, filter) in statements)
        {
            var count = 0L;
            var digests = new List<string>();
            var scoped = await ScalarAsync<bool>(owner, null, """
                SELECT EXISTS (SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 'public' AND table_name = @table AND column_name = 'organization_id')
                """, ("table", name));
            foreach (var organization in scoped ? new Guid?[] { null, SchemaFixture.OrgA1, SchemaFixture.OrgA2 } : [null])
            {
                await using var announced = await owner.BeginTransactionAsync();
                await ScopeAsync(owner, announced, SchemaFixture.TenantA, organization);
                await using var command = new NpgsqlCommand($"""
                    SELECT count(*), coalesce(md5(string_agg(row_to_json(t)::text, '|' ORDER BY {order})), '')
                    FROM {table} t WHERE {filter}{(scoped ? " AND t.organization_id IS NOT DISTINCT FROM @organization::uuid" : string.Empty)}
                    """, owner, announced);
                command.Parameters.AddWithValue("organization", (object?)organization ?? DBNull.Value);
                if (filter.Contains("@erasureJob", StringComparison.Ordinal))
                    command.Parameters.AddWithValue("erasureJob", ErasureJobOf(world));
                await using (var row = await command.ExecuteReaderAsync())
                {
                    (await row.ReadAsync()).Should().BeTrue();
                    count += row.GetInt64(0);
                    digests.Add(row.GetString(1));
                }

                await announced.CommitAsync();
            }

            trail[name] = count + ":" + string.Join('/', digests);

            // The dispatcher role has no grant on the inbox, which only the application role reads.
            if (name != "inbox_messages")
                (await ScalarAsync<long>(dispatcher, null, $"SELECT count(*) FROM {table} t WHERE {filter}",
                    ("erasureJob", ErasureJobOf(world))))
                    .Should().Be(count, $"the tenant's announcements hide no {name} row");
        }

        await using var transaction = await owner.BeginTransactionAsync();
        await ScopeAsync(owner, transaction, SchemaFixture.TenantA);
        await using var targets = new NpgsqlCommand("""
            SELECT count(*), coalesce(md5(string_agg(row_to_json(t)::text, '|' ORDER BY t.id)), '')
            FROM public.identity_provider_event_targets t WHERE t.affected_user_id = @person
            """, owner, transaction);
        targets.Parameters.AddWithValue("person", PersonA);
        await using (var result = await targets.ExecuteReaderAsync())
        {
            (await result.ReadAsync()).Should().BeTrue();
            trail["provider_targets"] = result.GetInt64(0) + ":" + result.GetString(1);
        }

        await transaction.CommitAsync();
        trail.Keys.Should().BeEquivalentTo(Tables);
        return trail;
    }

    /// <summary>The erasure job the trail leaves out, or none when the arrangement ran no erasure.</summary>
    private static Guid ErasureJobOf(World world) => world.ErasureJob.IsInitialized() ? world.ErasureJob.Value : Guid.Empty;

    /// <summary>The audit observations the mirror handler wrote about person A for one provider fact.</summary>
    private static async Task<long> ObservationsAsync(World world, Guid providerEvent)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(world.Database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        return await ScalarAsync<long>(app, transaction, """
            SELECT count(*) FROM public.audit_log
            WHERE operation = 'audit.provider_event.observe' AND entity_type = 'User' AND entity_id = @person
                AND metadata->'provider_event'->>'provider_event_id' = @id
            """, ("person", PersonA.ToString("D")), ("id", providerEvent.ToString("D")));
    }

    private sealed record Observation(string Operation, string EntityType, string EntityId, string? AffectedUser, string? ProviderEvent,
        string? Kind, string? TargetId, string Metadata);

    /// <summary>The one audit observation the mirror handler wrote about person A for a provider fact, or none.</summary>
    private static async Task<Observation?> ObservationAsync(World world, Guid providerEvent)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(world.Database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await using var command = new NpgsqlCommand("""
            SELECT operation, entity_type, entity_id, metadata->'provider_event'->>'affected_user_id',
                metadata->'provider_event'->>'provider_event_id', metadata->'provider_event'->>'kind',
                metadata->'provider_event'->>'target_id', metadata::text
            FROM public.audit_log
            WHERE operation = 'audit.provider_event.observe' AND entity_type = 'User' AND entity_id = @person
                AND metadata->'provider_event'->>'provider_event_id' = @id
            """, app, transaction);
        command.Parameters.AddWithValue("person", PersonA.ToString("D"));
        command.Parameters.AddWithValue("id", providerEvent.ToString("D"));
        await using var row = await command.ExecuteReaderAsync();
        if (!await row.ReadAsync()) return null;
        string? Text(int ordinal) => row.IsDBNull(ordinal) ? null : row.GetString(ordinal);
        var observation = new Observation(row.GetString(0), row.GetString(1), row.GetString(2), Text(3), Text(4), Text(5), Text(6),
            row.GetString(7));
        (await row.ReadAsync()).Should().BeFalse("one fact leaves one observation");
        return observation;
    }

    /// <summary>The erasure request's stored status.</summary>
    private static async Task<string> RequestStatusAsync(World world)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(world.Database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        return await ScalarAsync<string>(app, transaction, "SELECT status FROM public.data_subject_requests WHERE id = @id",
            ("id", world.ErasureRequest));
    }

    private sealed record DeliveryState(int ReplayCount, string? TerminalReason, bool DeadLettered);

    private static async Task<DeliveryState> DeliveryAsync(World world)
    {
        await using var dispatcher = (NpgsqlConnection)await PostgresFixture.OpenAsync(world.Database.OutboxConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT replay_count, terminal_reason, dead_lettered_at IS NOT NULL
            FROM public.event_deliveries WHERE tenant_id = @tenant AND subscription_id = @subscription AND event_id = @event
            """, dispatcher);
        command.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
        command.Parameters.AddWithValue("subscription", Subscription);
        command.Parameters.AddWithValue("event", world.FailedEvent);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return new DeliveryState(row.GetInt32(0), row.IsDBNull(1) ? null : row.GetString(1), row.GetBoolean(2));
    }

    /// <summary>The failed fact's delivery has been tried and has failed, so the dispatcher can be stopped.</summary>
    private static async Task<bool> FailedDeliveryAttemptedAsync(World world)
    {
        Guid? eventId;
        try { eventId = await EventOfAsync(world, world.FailedFact); }
        catch (InvalidOperationException) { return false; }
        await using var dispatcher = (NpgsqlConnection)await PostgresFixture.OpenAsync(world.Database.OutboxConnectionString);
        return await ScalarAsync<long>(dispatcher, null, """
            SELECT count(*) FROM public.event_deliveries WHERE subscription_id = @subscription AND event_id = @event
                AND last_error IS NOT NULL AND processed_at IS NULL
            """, ("subscription", Subscription), ("event", eventId!.Value)) == 1;
    }

    private static async Task<Guid> TargetOfAsync(World world, Guid providerEvent)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(world.Database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        return await ScalarAsync<Guid>(app, transaction, "SELECT id FROM public.identity_provider_event_targets WHERE provider_event_id = @id",
            ("id", providerEvent));
    }

    private static async Task<Guid> EventOfAsync(World world, Guid providerEvent)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(world.Database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await using var command = new NpgsqlCommand(
            "SELECT event_id FROM public.identity_provider_event_targets WHERE provider_event_id = @id", app, transaction);
        command.Parameters.AddWithValue("id", providerEvent);
        return await command.ExecuteScalarAsync() as Guid? ?? throw new InvalidOperationException("The fact has no target yet.");
    }

    /// <summary>The person's User, their tenant-wide Membership and their two branch Memberships.</summary>
    private static async Task<(string User, string Tenant, string Branch1, string Branch2)> PersonStateAsync(World world)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(world.Database.MigrationConnectionString);
        var user = await ScalarAsync<string>(owner, null, "SELECT status FROM public.users WHERE id = @id", ("id", PersonA));
        await using var app = await DataExportTestKit.OpenAppAsync(world.Database.AppConnectionString);
        var statuses = new List<string>();
        foreach (var (organization, membership) in new[] { ((Guid?)null, LearnerMembershipA),
                     (SchemaFixture.OrgA1, BranchMembershipA1), (SchemaFixture.OrgA2, BranchMembershipA2) })
        {
            await using var transaction = await app.BeginTransactionAsync();
            await ScopeAsync(app, transaction, SchemaFixture.TenantA, organization);
            statuses.Add(await ScalarAsync<string>(app, transaction, "SELECT status FROM public.memberships WHERE id = @id",
                ("id", membership)));
        }

        return (user, statuses[0], statuses[1], statuses[2]);
    }

    /// <summary>
    /// Sets tenant A's lifecycle status. The seeded tenant has no default organization, which a serving tenant has, so the
    /// recovery guard would refuse it before the status mattered.
    /// </summary>
    private static async Task SetTenantStatusAsync(DisposableSchemaDatabase database, string status)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await TenancyLifecycleFixture.LockAsync(app, transaction, SchemaFixture.TenantA);
        await ExecuteAsync(app, transaction, """
            UPDATE tenants SET status = @status, default_organization_id = coalesce(default_organization_id, @organization)
            WHERE id = @tenant
            """, ("status", status), ("organization", SchemaFixture.OrgA1), ("tenant", SchemaFixture.TenantA));
        await transaction.CommitAsync();
    }

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var started = Stopwatch.GetTimestamp();
        while (!await condition())
        {
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(60))
                throw new TimeoutException("The provider facts did not reach their expected durable boundary.");
            await Task.Delay(50);
        }
    }

    /// <summary>The database, the host that runs the erasure and the recovery, and the ids the proofs read.</summary>
    private sealed class World(DisposableSchemaDatabase database, Guid completedFact, Guid failedFact) : IAsyncDisposable
    {
        public DisposableSchemaDatabase Database => database;
        public Guid CompletedFact => completedFact;
        public Guid FailedFact => failedFact;
        public Guid FailedEvent { get; set; }
        public BackgroundJobId ErasureJob { get; set; }
        public Guid ErasureRequest { get; set; }
        public Guid FailedTarget { get; set; }
        public WebApplicationFactory<Program> Host { get; set; } = null!;
        public Dictionary<string, string> Before { get; set; } = null!;
        public Dictionary<string, string> After { get; set; } = null!;

        public async ValueTask DisposeAsync()
        {
            if (Host is not null) await Host.DisposeAsync();
            await database.DisposeAsync();
        }
    }

    /// <summary>The real Program with its dispatcher and mirror running over the disposable database, as the provider-event flow proof builds it.</summary>
    private sealed class ProviderApi(PostgresFixture postgres, DisposableSchemaDatabase database, IIdentityEventProvider provider)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            InvitationEmailFixtureConfiguration.AddTo(builder);
            builder.UseSetting("ConnectionStrings:Default", database.AppConnectionString);
            builder.UseSetting("ConnectionStrings:OutboxDispatcher", database.OutboxConnectionString);
            builder.UseSetting("ConnectionStrings:IdentityIngress", new NpgsqlConnectionStringBuilder(postgres.IdentityIngressConnectionString)
            { Database = new NpgsqlConnectionStringBuilder(database.AppConnectionString).Database }.ConnectionString);
            builder.UseSetting("Identity:AuthorityId", Authority.AuthorityId.Value.ToString("D"));
            builder.UseSetting("Authentication:Tenant:Issuer", Authority.Issuer);
            builder.UseSetting("IdentityEvents:RealmId", "learnstack");
            builder.UseSetting("Telemetry:OtlpEndpoint", "");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IIdentityEventProvider>();
                services.AddSingleton(provider);
            });
        }
    }

    private sealed class Provider(params IdentityProviderEvent[] facts) : IIdentityEventProvider
    {
        public Task<IdentityEventSourcePolicy> ReadPolicyAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new IdentityEventSourcePolicy(604800));

        public Task<IdentityEventPage> ReadAsync(IdentityEventRead request, CancellationToken cancellationToken = default) =>
            Task.FromResult(IdentityEventPage.Read(facts.Where(item => item.OccurredAt >= request.From && item.OccurredAt <= request.To)
                .Skip(request.Offset).Take(request.Maximum).Select(IdentityEventReadItem.Observed)));
    }
}
