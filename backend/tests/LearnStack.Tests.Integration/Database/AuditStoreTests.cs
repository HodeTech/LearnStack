using System.Net;
using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The four write paths, against the real table and the real policies.
/// </summary>
/// <remarks>
/// <para>
/// Every case runs as <c>learnstack_app</c> except the platform-scope one, which is the
/// only path that does not: the sentinel tenant is unreachable to the runtime role by
/// construction, which is the point of it.
/// </para>
/// <para>
/// Rows are written under this class's own tenant and removed in a <c>finally</c> as the
/// platform role — the only role that can delete one. The tenant is not in <c>tenants</c>
/// and does not need to be: <c>audit_log</c> deliberately carries no foreign key to it.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class AuditStoreTests
{
    private readonly SchemaFixture _schema;

    public AuditStoreTests(SchemaFixture schema) => _schema = schema;

    private static readonly Guid StoreTenant = Guid.Parse("eeeeeeee-1111-7111-8111-111111111111");
    private static readonly Guid StoreOrg = Guid.Parse("eeeeeeee-2222-7222-8222-222222222222");

    [Fact]
    public async Task WritePending_writes_one_row_per_MUST_intent_on_the_business_transaction()
    {
        // Intents are plural — one per audited (resource, operation), not one per request
        // — because ProvisionTenantCommand writes two aggregate roots on one transaction
        // and the Tenancy matrix classifies both MUST (ADR-0033 Amendment 2 § 1).
        var capture = new AuditStateCapture();
        var first = Intent(capture, "tenancy.tenant.create");
        var second = Intent(capture, "tenancy.organization.create");

        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var store = Store(capture, dataSource);

        try
        {
            await using (var unitOfWork = new NpgsqlUnitOfWork(dataSource, NullLogger<NpgsqlUnitOfWork>.Instance))
            {
                await using var scope = await unitOfWork.BeginTransactionAsync();
                await AnnounceAsync(unitOfWork);

                await store.WritePendingAsync(unitOfWork);

                capture.State.Should().Be(AuditIntentState.WrittenInTransaction,
                    "the rows are on the transaction and not yet durable");

                await scope.CompleteAsync();
            }

            (await CountAsync(first.Id)).Should().Be(1);
            (await CountAsync(second.Id)).Should().Be(1);
        }
        finally
        {
            await DeleteAsync(first.Id, second.Id);
        }
    }

    [Fact]
    public async Task A_rolled_back_transaction_takes_its_audit_rows_with_it()
    {
        // The guarantee ADR-0033 makes, stated as an observation rather than an argument:
        // the row commits with the change it describes or not at all.
        var capture = new AuditStateCapture();
        var intent = Intent(capture, "tenancy.tenant.create");

        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var store = Store(capture, dataSource);

        await using (var unitOfWork = new NpgsqlUnitOfWork(dataSource, NullLogger<NpgsqlUnitOfWork>.Instance))
        {
            await using var scope = await unitOfWork.BeginTransactionAsync();
            await AnnounceAsync(unitOfWork);

            await store.WritePendingAsync(unitOfWork);

            // Disposing the scope unresolved rolls the unit back, which is the shape a
            // handler failure produces.
        }

        (await CountAsync(intent.Id)).Should().Be(0);
    }

    [Fact]
    public async Task WritePending_refuses_to_run_without_a_transaction()
    {
        // Not a silent skip. This runs from the owning frame immediately before COMMIT,
        // so no transaction here means the caller's own invariant is broken — and writing
        // the row anywhere else would give it a durability nobody asked for.
        var capture = new AuditStateCapture();
        Intent(capture, "tenancy.tenant.create");

        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var store = Store(capture, dataSource);

        await using var unitOfWork = new NpgsqlUnitOfWork(dataSource, NullLogger<NpgsqlUnitOfWork>.Instance);

        var act = async () => await store.WritePendingAsync(unitOfWork);

        // The MESSAGE, not merely the type. Delete the guard and the INSERT still fails —
        // in autocommit, with no tenant announced, refused by the policy — and throws the
        // same exception type from the catch below it. A case asserting only the type
        // passes against a deleted guard, which is the shape this suite has caught before.
        (await act.Should().ThrowAsync<AuditWriteFailedException>())
            .WithMessage("*no ambient transaction*");
    }

    [Fact]
    public async Task WritePending_writes_nothing_for_a_SHOULD_or_MAY_intent()
    {
        // Those ride WriteBestEffortAsync, on their own transaction and with the opposite
        // failure posture. Writing them here would give a MAY-class row the MUST-class
        // guarantee and would roll a business write back for one.
        var capture = new AuditStateCapture();
        var intent = Intent(capture, "audit.event.read", OperationClass.Should);

        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var store = Store(capture, dataSource);

        await using (var unitOfWork = new NpgsqlUnitOfWork(dataSource, NullLogger<NpgsqlUnitOfWork>.Instance))
        {
            await using var scope = await unitOfWork.BeginTransactionAsync();
            await AnnounceAsync(unitOfWork);

            await store.WritePendingAsync(unitOfWork);
            await scope.CompleteAsync();
        }

        (await CountAsync(intent.Id)).Should().Be(0);
        capture.State.Should().Be(AuditIntentState.Pending, "nothing was written");
    }

    [Fact]
    public async Task A_refused_insert_throws_so_the_business_write_rolls_back()
    {
        // Fail-closed, and the exception is how: TransactionBehavior's catch rolls the
        // business write back and the caller is answered 503 audit_unavailable rather
        // than committing unaudited. The refusal here is the policy's — the intent names
        // a tenant the transaction did not announce, which is the one thing WITH CHECK is
        // there to stop.
        var capture = new AuditStateCapture();
        capture.DeclareIntent(new AuditIntent(
            AuditEntryId.From(Guid.CreateVersion7()),
            TenantId.From(SchemaFixture.TenantB),
            OrganizationId: null,
            "tenancy",
            "tenancy.tenant.create",
            OperationType.Create,
            OperationClass.Must,
            EntityType: null,
            DateTimeOffset.UtcNow));

        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var store = Store(capture, dataSource);

        await using var unitOfWork = new NpgsqlUnitOfWork(dataSource, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var scope = await unitOfWork.BeginTransactionAsync();
        await AnnounceAsync(unitOfWork);

        var act = async () => await store.WritePendingAsync(unitOfWork);

        var thrown = (await act.Should().ThrowAsync<AuditWriteFailedException>()).Which;

        thrown.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(
                PostgresErrorCodes.InsufficientPrivilege,
                "WITH CHECK refuses a row outside the tenant the transaction announced");
    }

    [Fact]
    public async Task The_standalone_write_announces_both_session_variables()
    {
        // The consequence of the org-scoped class that binds this writer: a row whose
        // organization_id is non-null while app.organization_id is unset fails WITH
        // CHECK. Every `denied` row for an org-scoped resource travels this path, which
        // is the path whose whole job is that the record survives (ADR-0044 § 9).
        var draft = Draft(organizationId: StoreOrg);

        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var store = Store(new AuditStateCapture(), dataSource);

        try
        {
            await store.WriteStandaloneAsync(draft);

            (await CountAsync(draft.Id)).Should().Be(1,
                "the organization GUC is announced from the draft, not left unset");
        }
        finally
        {
            await DeleteAsync(draft.Id);
        }
    }

    [Fact]
    public async Task A_duplicate_on_the_standalone_re_write_is_positive_evidence_and_is_swallowed()
    {
        // A 23505 here means the in-transaction row is already durable under this id and
        // timestamp — so the row this call exists to rescue does not need rescuing. It is
        // logged, counted and swallowed; it is not an audit failure and must not produce
        // audit_unavailable (ADR-0044 § 5).
        var draft = Draft(organizationId: null);

        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var store = Store(new AuditStateCapture(), dataSource);

        try
        {
            await store.WriteStandaloneAsync(draft);

            var act = async () => await store.WriteStandaloneAsync(draft);

            await act.Should().NotThrowAsync();
            (await CountAsync(draft.Id)).Should().Be(1);
        }
        finally
        {
            await DeleteAsync(draft.Id);
        }
    }

    [Fact]
    public async Task The_indeterminate_pair_is_two_rows_under_one_id()
    {
        // The same id at a FRESH instant, which is what the composite primary key exists
        // to make legal. Both rows are the same operation; the second says the first
        // one's fate is unknown.
        var first = Draft(organizationId: null);
        var second = first with
        {
            Timestamp = first.Timestamp.AddMilliseconds(5),
            Outcome = AuditOutcome.Indeterminate,
        };

        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var store = Store(new AuditStateCapture(), dataSource);

        try
        {
            await store.WriteStandaloneAsync(first);
            await store.WriteStandaloneAsync(second);

            (await CountAsync(first.Id)).Should().Be(2);
        }
        finally
        {
            await DeleteAsync(first.Id);
        }
    }

    [Fact]
    public async Task A_best_effort_failure_is_logged_and_dropped()
    {
        // The opposite posture to the standalone write. The accepted loss is written down
        // in the module's coverage matrix rather than assumed here; what must not happen
        // is the operation failing because a SHOULD-class row did.
        var draft = Draft(organizationId: null) with { TenantId = TenantId.From(SchemaFixture.TenantB) };

        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var store = Store(new AuditStateCapture(), dataSource);

        // TenantB is a real tenant, but the draft's own announcement makes the row legal —
        // so to force a failure the row has to break something the announcement cannot
        // fix. An outcome outside the CHECK does it, and it is the shape a future writer
        // would actually get wrong.
        var refused = draft with { Outcome = (AuditOutcome)99 };

        var act = async () => await store.WriteBestEffortAsync(refused);

        await act.Should().NotThrowAsync();
        (await CountAsync(refused.Id)).Should().Be(0);
    }

    [Fact]
    public async Task The_platform_scope_row_is_written_on_the_callers_connection()
    {
        // One caller: EnterPlatformAdminScope(reason). The connection and transaction are
        // the scope's, not the request's — learnstack_app cannot write a row under the
        // platform sentinel, and the request's transaction is the wrong lifetime for a
        // record that must outlive it.
        var draft = Draft(organizationId: null) with
        {
            TenantId = TenantId.PlatformSentinel,
            ModuleName = "platform",
            Operation = "platform.admin_scope.enter",
            OperationType = OperationType.PlatformAdmin,
            Reason = "probe",
        };

        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var store = Store(new AuditStateCapture(), dataSource);

        await using var platform = await PostgresFixture.OpenAsync(_schema.Postgres.PlatformConnectionString);

        try
        {
            await using (var transaction = await platform.BeginTransactionAsync())
            {
                await store.WritePlatformScopeAsync(draft, platform, transaction);
                await transaction.CommitAsync();
            }

            (await CountAsync(draft.Id)).Should().Be(1);
        }
        finally
        {
            await DeleteAsync(draft.Id);
        }
    }

    [Fact]
    public async Task The_platform_scope_row_rolls_back_with_the_scope_that_wrote_it()
    {
        // It is written BEFORE the operation, so an operation that later fails is still
        // on the record — but a scope that never opened is not an entry, and the row goes
        // with the transaction that carried it.
        var draft = Draft(organizationId: null) with
        {
            TenantId = TenantId.PlatformSentinel,
            ModuleName = "platform",
            Operation = "platform.admin_scope.enter",
            OperationType = OperationType.PlatformAdmin,
            Reason = "probe",
        };

        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var store = Store(new AuditStateCapture(), dataSource);

        await using var platform = await PostgresFixture.OpenAsync(_schema.Postgres.PlatformConnectionString);

        await using (var transaction = await platform.BeginTransactionAsync())
        {
            await store.WritePlatformScopeAsync(draft, platform, transaction);
            await transaction.RollbackAsync();
        }

        (await CountAsync(draft.Id)).Should().Be(0);
    }

    [Fact]
    public async Task The_snapshot_columns_land_as_jsonb_and_the_diff_as_an_array()
    {
        // Npgsql infers `text` from a string and PostgreSQL will not assign text to
        // jsonb, so an inferred parameter fails on every row carrying a snapshot — which
        // is every row this store exists to write. And `changes` is an array on both
        // sides of the cap, single-entity and multi-entity alike (ADR-0044 § 7).
        var capture = new AuditStateCapture();
        capture.Add(new CapturedEntityChange(
            nameof(ProbeTenant), "t-1", """{"slug":"a"}""", """{"slug":"b"}""",
            [new CapturedFieldChange("/ProbeTenant/slug", "\"a\"", "\"b\"")]));

        var intent = Intent(capture, "tenancy.tenant.update", entityType: typeof(ProbeTenant));

        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var store = Store(capture, dataSource);

        try
        {
            await using (var unitOfWork = new NpgsqlUnitOfWork(dataSource, NullLogger<NpgsqlUnitOfWork>.Instance))
            {
                await using var scope = await unitOfWork.BeginTransactionAsync();
                await AnnounceAsync(unitOfWork);

                await store.WritePendingAsync(unitOfWork);
                await scope.CompleteAsync();
            }

            await using var connection = await PostgresFixture.OpenAsync(
                _schema.Postgres.PlatformConnectionString);
            await using var read = new NpgsqlCommand(
                """
                SELECT jsonb_typeof(before_state), jsonb_typeof(after_state),
                       jsonb_typeof(changes), entity_type, entity_id
                FROM audit_log WHERE id = @id
                """, (NpgsqlConnection)connection);
            read.Parameters.AddWithValue("id", intent.Id.Value);

            await using var reader = await read.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();

            reader.GetString(0).Should().Be("object");
            reader.GetString(1).Should().Be("object");
            reader.GetString(2).Should().Be("array", "changes is an array whatever the arity");
            reader.GetString(3).Should().Be(nameof(ProbeTenant));
            reader.GetString(4).Should().Be("t-1");
        }
        finally
        {
            await DeleteAsync(intent.Id);
        }
    }

    [Fact]
    public async Task Several_captures_of_one_entity_merge_earliest_before_and_latest_after()
    {
        // Not hypothetical: ProvisionTenantCommand saves three times and captures Tenant
        // twice, so picking one capture arbitrarily records half of what happened
        // (ADR-0044 Amendment 5 § 2).
        var capture = new AuditStateCapture();
        capture.Add(new CapturedEntityChange(
            nameof(ProbeTenant), "t-1", """{"step":0}""", """{"step":1}""",
            [new CapturedFieldChange("/ProbeTenant/step", "0", "1")]));
        capture.Add(new CapturedEntityChange(
            nameof(ProbeTenant), "t-1", """{"step":1}""", """{"step":2}""",
            [new CapturedFieldChange("/ProbeTenant/step", "1", "2")]));

        var intent = Intent(capture, "tenancy.tenant.update", entityType: typeof(ProbeTenant));

        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var store = Store(capture, dataSource);

        try
        {
            await using (var unitOfWork = new NpgsqlUnitOfWork(dataSource, NullLogger<NpgsqlUnitOfWork>.Instance))
            {
                await using var scope = await unitOfWork.BeginTransactionAsync();
                await AnnounceAsync(unitOfWork);

                await store.WritePendingAsync(unitOfWork);
                await scope.CompleteAsync();
            }

            await using var connection = await PostgresFixture.OpenAsync(
                _schema.Postgres.PlatformConnectionString);
            await using var read = new NpgsqlCommand(
                """
                SELECT before_state ->> 'step', after_state ->> 'step',
                       jsonb_array_length(changes)
                FROM audit_log WHERE id = @id
                """, (NpgsqlConnection)connection);
            read.Parameters.AddWithValue("id", intent.Id.Value);

            await using var reader = await read.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();

            reader.GetString(0).Should().Be("0", "before_state is the EARLIEST capture's");
            reader.GetString(1).Should().Be("2", "after_state is the LATEST capture's");
            reader.GetInt32(2).Should().Be(2, "changes is their concatenation in capture order");
        }
        finally
        {
            await DeleteAsync(intent.Id);
        }
    }

    [Fact]
    public async Task An_insert_followed_by_an_update_still_records_a_creation()
    {
        // The case ADR-0044 Amendment 5 § 2 is actually about, and the one the earlier
        // merge got wrong. ProvisionTenantCommand saves three times: Tenant is captured
        // Added (BeforeJson null) and then Modified. Skipping the null "to find a real
        // snapshot" walks past the only capture that says the entity did not exist, and
        // the row then claims a complete prior state for something it calls a creation.
        // On an append-only table that reading is permanent.
        var capture = new AuditStateCapture();
        capture.Add(new CapturedEntityChange(
            nameof(ProbeTenant), "t-1", null, Step(1),
            [new CapturedFieldChange("/ProbeTenant/step", null, "1")]));
        capture.Add(new CapturedEntityChange(
            nameof(ProbeTenant), "t-1", Step(1), Step(2),
            [new CapturedFieldChange("/ProbeTenant/step", "1", "2")]));

        var intent = Intent(capture, "tenancy.tenant.create", entityType: typeof(ProbeTenant));

        var (before, after) = await WriteAndReadStatesAsync(capture, intent);

        before.Should().BeNull("the earliest capture is the insert, and it has no prior state");
        after.Should().Be("2", "the latest capture's after state");
    }

    [Fact]
    public async Task An_update_followed_by_a_delete_records_that_the_row_is_gone()
    {
        // The mirror, and it fails the same way: keeping the last NON-NULL after state
        // makes a `delete` row assert the entity still exists.
        var capture = new AuditStateCapture();
        capture.Add(new CapturedEntityChange(
            nameof(ProbeTenant), "t-1", Step(1), Step(2),
            [new CapturedFieldChange("/ProbeTenant/step", "1", "2")]));
        capture.Add(new CapturedEntityChange(
            nameof(ProbeTenant), "t-1", Step(2), null,
            [new CapturedFieldChange("/ProbeTenant/step", "2", null)]));

        var intent = Intent(capture, "tenancy.tenant.delete", entityType: typeof(ProbeTenant));

        var (before, after) = await WriteAndReadStatesAsync(capture, intent);

        before.Should().Be("1");
        after.Should().BeNull("the latest capture is the delete, and it has no new state");
    }

    [Fact]
    public async Task A_capture_of_another_entity_type_does_not_reach_the_row()
    {
        // One request captures every entity it touched; an intent is about one of them.
        // Without the type filter, ProvisionTenantCommand's tenant row would carry the
        // organization's snapshot — and the trail would attribute one aggregate's change
        // to another.
        var capture = new AuditStateCapture();
        capture.Add(new CapturedEntityChange(
            "SomethingElse", "x-1", Step(9), Step(9),
            [new CapturedFieldChange("/SomethingElse/step", "9", "9")]));

        var intent = Intent(capture, "tenancy.tenant.create", entityType: typeof(ProbeTenant));

        var (before, after) = await WriteAndReadStatesAsync(capture, intent);

        before.Should().BeNull();
        after.Should().BeNull();
    }

    [Fact]
    public async Task The_standalone_write_throws_when_the_row_cannot_be_written()
    {
        // Fail-closed is the whole difference between this method and the best-effort one,
        // and without a case the two can be made identical. The refusal here is the
        // CHECK's: an outcome outside the closed set, which is the shape a future writer
        // would actually get wrong.
        var draft = Draft(organizationId: null) with { Outcome = (AuditOutcome)99 };

        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var store = Store(new AuditStateCapture(), dataSource);

        var act = async () => await store.WriteStandaloneAsync(draft);

        await act.Should().ThrowAsync<AuditWriteFailedException>();
        (await CountAsync(draft.Id)).Should().Be(0);
    }

    [Fact]
    public async Task The_best_effort_write_actually_writes_the_row()
    {
        // The accepting half. Without it the method could be reduced to a no-op with the
        // whole suite green, and every SHOULD/MAY row would be silently lost.
        var draft = Draft(organizationId: StoreOrg);

        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var store = Store(new AuditStateCapture(), dataSource);

        try
        {
            await store.WriteBestEffortAsync(draft);

            (await CountAsync(draft.Id)).Should().Be(1);
        }
        finally
        {
            await DeleteAsync(draft.Id);
        }
    }


    [Fact]
    public async Task The_sentinel_is_refused_on_the_standalone_path()
    {
        // The FIFTH announcement site. TenantId's remarks enumerate four guards because a
        // CHECK on `tenants` cannot stop a session variable from being announced — and
        // this method announces one from a draft, on a learnstack_app connection, into a
        // table that deliberately has no foreign key to `tenants`. Measured before the
        // guard: the row was written, through the runtime role, under the sentinel — which
        // is exactly what IAuditStore's own contract says cannot happen.
        var draft = Draft(organizationId: null) with { TenantId = TenantId.PlatformSentinel };

        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var store = Store(new AuditStateCapture(), dataSource);

        var standalone = async () => await store.WriteStandaloneAsync(draft);
        var bestEffort = async () => await store.WriteBestEffortAsync(draft);

        (await standalone.Should().ThrowAsync<AuditWriteFailedException>())
            .WithMessage("*WritePlatformScopeAsync*");

        // Best effort swallows a DATABASE failure; it must not swallow this one, which is
        // a caller error rather than an outage.
        await bestEffort.Should().ThrowAsync<AuditWriteFailedException>();

        (await CountAsync(draft.Id)).Should().Be(0);
    }

    [Fact]
    public async Task The_durable_duplicate_is_counted_as_well_as_logged()
    {
        // ADR-0033 § 4 and ADR-0044 § 5 both say "logged, counted, and swallowed". The
        // count is the half that matters operationally: each one is a business COMMIT
        // whose outcome the process could not observe, so a rate that moves is a signal
        // about the connection rather than about any one request.
        var draft = Draft(organizationId: null);

        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var store = Store(new AuditStateCapture(), dataSource);

        var counted = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Meter.Name == PostgresAuditStore.MeterName
                && instrument.Name == PostgresAuditStore.DurableDuplicateCounterName)
            {
                active.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => counted += measurement);
        listener.Start();

        try
        {
            await store.WriteStandaloneAsync(draft);
            await store.WriteStandaloneAsync(draft);

            counted.Should().Be(1, "the second write hit the duplicate and counted it");
        }
        finally
        {
            await DeleteAsync(draft.Id);
        }
    }

    [Fact]
    public async Task Two_instances_of_one_type_under_one_intent_are_refused()
    {
        // Earliest and latest are meaningful for ONE instance across several flushes. Two
        // different instances are not that, and the type-name filter cannot tell them
        // apart — entity_id would name one while after_state described the other, and the
        // pointers in `changes` carry no instance. That row is self-contradictory and
        // permanent, so the mismatch is loud rather than composed.
        var capture = new AuditStateCapture();
        capture.Add(new CapturedEntityChange(
            nameof(ProbeTenant), "t-1", null, Step(1),
            [new CapturedFieldChange("/ProbeTenant/step", null, "1")]));
        capture.Add(new CapturedEntityChange(
            nameof(ProbeTenant), "t-2", null, Step(2),
            [new CapturedFieldChange("/ProbeTenant/step", null, "2")]));

        var intent = Intent(capture, "tenancy.tenant.create", entityType: typeof(ProbeTenant));

        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var store = Store(capture, dataSource);

        await using var unitOfWork = new NpgsqlUnitOfWork(dataSource, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var frame = await unitOfWork.BeginTransactionAsync();
        await AnnounceAsync(unitOfWork);

        var act = async () => await store.WritePendingAsync(unitOfWork);

        (await act.Should().ThrowAsync<AuditWriteFailedException>())
            .WithMessage("*different instances*");

        (await CountAsync(intent.Id)).Should().Be(0);
    }

    [Fact]
    public async Task The_row_takes_its_entity_id_from_the_captured_aggregate()
    {
        // entity_id is what a reader joins on. Composing it from the wrong capture — or
        // leaving it null when a capture exists — makes the row unfindable from the
        // aggregate it is about.
        var capture = new AuditStateCapture();
        capture.Add(new CapturedEntityChange(
            nameof(ProbeTenant), "t-42", null, Step(1),
            [new CapturedFieldChange("/ProbeTenant/step", null, "1")]));

        var intent = Intent(capture, "tenancy.tenant.create", entityType: typeof(ProbeTenant));

        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var store = Store(capture, dataSource);

        try
        {
            await using (var unitOfWork = new NpgsqlUnitOfWork(dataSource, NullLogger<NpgsqlUnitOfWork>.Instance))
            {
                await using var scope = await unitOfWork.BeginTransactionAsync();
                await AnnounceAsync(unitOfWork);

                await store.WritePendingAsync(unitOfWork);
                await scope.CompleteAsync();
            }

            await using var connection = await PostgresFixture.OpenAsync(
                _schema.Postgres.PlatformConnectionString);
            await using var read = new NpgsqlCommand(
                "SELECT entity_id, entity_type FROM audit_log WHERE id = @id",
                (NpgsqlConnection)connection);
            read.Parameters.AddWithValue("id", intent.Id.Value);

            await using var reader = await read.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();

            reader.GetString(0).Should().Be("t-42");
            reader.GetString(1).Should().Be(nameof(ProbeTenant));
        }
        finally
        {
            await DeleteAsync(intent.Id);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string Step(int value) => $"{{\"step\":{value}}}";

    /// <summary>Writes the intent's row and reads back the two snapshot columns.</summary>
    private async Task<(string? Before, string? After)> WriteAndReadStatesAsync(
        AuditStateCapture capture, AuditIntent intent)
    {
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var store = Store(capture, dataSource);

        try
        {
            await using (var unitOfWork = new NpgsqlUnitOfWork(dataSource, NullLogger<NpgsqlUnitOfWork>.Instance))
            {
                await using var scope = await unitOfWork.BeginTransactionAsync();
                await AnnounceAsync(unitOfWork);

                await store.WritePendingAsync(unitOfWork);
                await scope.CompleteAsync();
            }

            await using var connection = await PostgresFixture.OpenAsync(
                _schema.Postgres.PlatformConnectionString);
            await using var read = new NpgsqlCommand(
                "SELECT before_state ->> 'step', after_state ->> 'step' FROM audit_log WHERE id = @id",
                (NpgsqlConnection)connection);
            read.Parameters.AddWithValue("id", intent.Id.Value);

            await using var reader = await read.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();

            return (
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1));
        }
        finally
        {
            await DeleteAsync(intent.Id);
        }
    }

    /// <summary>A stand-in for the aggregate an intent names, so the merge has a type.</summary>
    private sealed class ProbeTenant;

    private static PostgresAuditStore Store(AuditStateCapture capture, NpgsqlDataSource dataSource) =>
        new(capture, new Lazy<NpgsqlDataSource>(() => dataSource),
            NullLogger<PostgresAuditStore>.Instance, MeterFactory);

    /// <summary>A real meter factory, so the counter the store increments is a real one.</summary>
    private static readonly IMeterFactory MeterFactory =
        new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();

    private static AuditIntent Intent(
        AuditStateCapture capture,
        string operation,
        OperationClass operationClass = OperationClass.Must,
        Type? entityType = null)
    {
        var intent = new AuditIntent(
            AuditEntryId.From(Guid.CreateVersion7()),
            TenantId.From(StoreTenant),
            OrganizationId: null,
            operation.Split('.')[0],
            operation,
            OperationType.Create,
            operationClass,
            entityType,
            DateTimeOffset.UtcNow);

        capture.DeclareIntent(intent);

        return intent;
    }

    private static AuditEntryDraft Draft(Guid? organizationId) =>
        new()
        {
            Id = AuditEntryId.From(Guid.CreateVersion7()),
            TenantId = TenantId.From(StoreTenant),
            OrganizationId = organizationId is null ? null : OrganizationId.From(organizationId.Value),
            ActorUserId = null,
            ActorEmail = null,
            ModuleName = "tenancy",
            Operation = "tenancy.tenant.provision",
            OperationType = OperationType.Create,
            OperationClass = OperationClass.Must,
            EntityType = null,
            EntityId = null,
            Outcome = AuditOutcome.Success,
            ErrorKey = null,
            Reason = null,
            BeforeState = null,
            AfterState = null,
            Changes = null,
            CorrelationId = null,
            IpAddress = IPAddress.Parse("2001:db8::1"),
            UserAgent = null,
            Timestamp = DateTimeOffset.UtcNow,
            Metadata = null,
        };

    /// <summary>Announces this class's tenant on the ambient transaction.</summary>
    /// <remarks>
    /// The unit of work's own setter refuses a tenant it cannot resolve from a context,
    /// so the announcement is issued directly here — which is also what a provisioning
    /// command's transaction does through <c>SetProvisioningTenantContextAsync</c>.
    /// </remarks>
    private static async Task AnnounceAsync(NpgsqlUnitOfWork unitOfWork)
    {
        await using var command = unitOfWork.Connection.CreateCommand();
        command.Transaction = unitOfWork.Transaction;
        command.CommandText = "SELECT set_config('app.tenant_id', @tenant, true)";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "tenant";
        parameter.Value = StoreTenant.ToString();
        command.Parameters.Add(parameter);

        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(AuditEntryId id)
    {
        await using var connection = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM audit_log WHERE id = @id", (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("id", id.Value);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task DeleteAsync(params AuditEntryId[] ids)
    {
        await using var connection = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString);
        await using var command = new NpgsqlCommand(
            "DELETE FROM audit_log WHERE id = ANY(@ids)", (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("ids", ids.Select(id => id.Value).ToArray());

        await command.ExecuteNonQueryAsync();
    }
}
