using FluentAssertions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.DataRequestSchemaSeed;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class AccountClosureStorageTests
{
    private const string Begin = "status='fulfilling',row_version=row_version+1";
    private const string Finalize = "status='completed',row_version=row_version+1";
    private const string ImmutableState = "Data request immutable state refused.";
    private const string RefusalRecord = "Data request refusal record refused.";
    private const string ResultRefused = "Data request result refused.";
    private const string Transition = "Data request transition refused.";
    private const string Subject = "Data request closure subject refused.";
    private const string UserFence = "Data request closure user fence refused.";
    private const string TenantFence = "Data request closure tenant fence refused.";

    /// <summary>
    /// A closure's Begin (<c>scheduled → fulfilling</c>) once its window has passed, with the subject Closing and the
    /// subject's User key, the request's tenant key and every participant tenant key held exclusively; and its refusal
    /// record (<c>scheduled → scheduled</c> setting a closed code), which needs no witness. Each condition is removed alone,
    /// and the platform role takes no other arrow, row INSERT or DELETE.
    /// </summary>
    [Fact]
    public async Task A_closure_s_Begin_and_its_refusal_record_take_their_arrows_only_under_their_witnesses()
    {
        await using var database = await DataExportTestKit.CreateSeededDatabaseAsync(schema.Postgres);
        var arranged = await ArrangeAsync(database);
        await MoveAsync(database, PersonA, Close, (SchemaFixture.TenantA, Mode.Exclusive), (SchemaFixture.TenantB, Mode.Exclusive));
        static Task Unfenced(NpgsqlConnection connection, NpgsqlTransaction transaction) => Task.CompletedTask;

        // The refusal record, on the closure still in its window.
        foreach (var (set, reason) in new[]
        {
            ("last_refusal='blocked_window'", ImmutableState),
            ("last_refusal='blocked_window',row_version=row_version+2", ImmutableState),
            ("last_refusal='blocked_everything',row_version=row_version+1", RefusalRecord),
            ("last_refusal=NULL,row_version=row_version+1", RefusalRecord),
            ("last_refusal='blocked_window',result='{\"audit_actor_reference\":1}',row_version=row_version+1", ResultRefused),
            ("last_refusal='blocked_window',completed_at=now(),row_version=row_version+1", "Data request completion refused."),
            ("last_refusal='blocked_window',status='cancelled',row_version=row_version+1", RefusalRecord),
            ("status='cancelled',row_version=row_version+1", Transition),
            ("status='completed',row_version=row_version+1", Transition),
        })
        {
            var refusal = await PlatformRefusedAsync(database, Unfenced, Format(set), ("id", arranged.PendingClosureA1));
            (refusal.SqlState, refusal.MessageText).Should().Be(("23514", reason), set);
        }

        await PlatformCommitAsync(database, Unfenced, Format("last_refusal='blocked_window',row_version=row_version+1"),
            arranged.PendingClosureA1);
        var recorded = await RequestStateAsync(database, SchemaFixture.TenantA, SchemaFixture.OrgA1, arranged.PendingClosureA1);
        recorded.Should().Be(new RequestState("scheduled", 1, "blocked_window", null, SystemActor, "{}"),
            "the record stamps the system actor, steps the version once and leaves the request scheduled");
        foreach (var (set, because) in new[]
        {
            ("last_refusal='blocked_window',row_version=row_version+1", "recording the standing code again changes nothing"),
            ("last_refusal=NULL,row_version=row_version+1", "a code is never cleared; a Begin that succeeds moves the request on"),
        })
        {
            var refusal = await PlatformRefusedAsync(database, Unfenced, Format(set), ("id", arranged.PendingClosureA1));
            (refusal.SqlState, refusal.MessageText).Should().Be(("23514", RefusalRecord), because);
        }

        await PlatformCommitAsync(database, Unfenced, Format("last_refusal='blocked_last_administrator',row_version=row_version+1"),
            arranged.PendingClosureA1);
        (await RequestStateAsync(database, SchemaFixture.TenantA, SchemaFixture.OrgA1, arranged.PendingClosureA1)).Refusal
            .Should().Be("blocked_last_administrator", "a later refusal replaces the standing one");
        (await PlatformRefusedAsync(database, Unfenced, Format("last_refusal='blocked_hold',row_version=row_version+1"), ("id", BranchB.Request)))
            .MessageText.Should().Be(RefusalRecord, "only a closure carries the platform's refusal record");

        // Begin: every condition but one, each time.
        foreach (var (set, reason) in new[]
        {
            ("status='fulfilling'", ImmutableState),
            (Begin + ",result='{\"audit_actor_reference\":1}'", ResultRefused),
            (Begin + ",completed_at=now()", "Data request completion refused."),
            (Begin + ",last_refusal='blocked_hold'", RefusalRecord),
        })
        {
            var refusal = await PlatformRefusedAsync(database, FullFencesOfPersonA, Format(set), ("id", arranged.DueClosureA));
            (refusal.SqlState, refusal.MessageText).Should().Be(("23514", reason), set);
        }

        (await PlatformRefusedAsync(database, FullFencesOfPersonA, Format(Begin + ",progress='{}'::jsonb"), ("id", arranged.DueClosureA)))
            .SqlState.Should().Be("42501", "a column outside the grant never reaches the guard");
        foreach (var (user, tenantA, tenantB, reason, because) in new (Mode, Mode, Mode, string, string)[]
        {
            (Mode.None, Mode.Exclusive, Mode.Exclusive, UserFence, "without the User key"),
            (Mode.Shared, Mode.Exclusive, Mode.Exclusive, UserFence, "with the User key shared"),
            (Mode.Exclusive, Mode.None, Mode.Exclusive, TenantFence, "without the request's own tenant key"),
            (Mode.Exclusive, Mode.Shared, Mode.Exclusive, TenantFence, "with the request's tenant key shared"),
            (Mode.Exclusive, Mode.Exclusive, Mode.None, TenantFence, "without the tombstone tenant's key"),
            (Mode.Exclusive, Mode.Exclusive, Mode.Shared, TenantFence, "with the tombstone tenant's key shared"),
        })
        {
            var refusal = await PlatformRefusedAsync(database, PersonAFences(user, tenantA, tenantB), Format(Begin), ("id", arranged.DueClosureA));
            (refusal.SqlState, refusal.MessageText).Should().Be(("23514", reason), because);
        }

        (await PlatformRefusedAsync(database, FullFencesOfPersonA, Format(Begin), ("id", arranged.PendingClosureA1)))
            .MessageText.Should().Be("Data request closure window refused.", "a closure is begun only once its window has passed");
        (await PlatformRefusedAsync(database, (connection, transaction) => ClosureFencesAsync(connection, transaction, PersonB, Mode.Exclusive,
                (SchemaFixture.TenantB, Mode.Exclusive)), Format(Begin), ("id", arranged.DueClosureB)))
            .MessageText.Should().Be(Subject, "the User is moved to Closing first, in the same transaction");

        await PlatformCommitAsync(database, FullFencesOfPersonA, Format(Begin), arranged.DueClosureA);
        (await RequestStateAsync(database, SchemaFixture.TenantA, null, arranged.DueClosureA))
            .Should().Be(new RequestState("fulfilling", 1, null, null, SystemActor, "{}"));

        // From fulfilling the platform role takes Finalize alone, and only once the subject is Closed.
        foreach (var (set, reason) in new[]
        {
            (Finalize, Subject),
            ("status='cancelled',row_version=row_version+1", Transition),
            ("status='scheduled',row_version=row_version+1", Transition),
            (Begin, Transition),
        })
        {
            var refusal = await PlatformRefusedAsync(database, FullFencesOfPersonA, Format(set), ("id", arranged.DueClosureA));
            (refusal.SqlState, refusal.MessageText).Should().Be(("23514", reason), set);
        }

        // The platform role has no INSERT or DELETE grant; widened in this disposable database, its arm refuses both.
        await using (var owner = await OpenAsync(database.MigrationConnectionString))
            await ExecuteAsync(owner, null, "GRANT INSERT, DELETE ON public.data_subject_requests TO learnstack_platform");
        try
        {
            var delete = await PlatformRefusedAsync(database, FullFencesOfPersonA, "DELETE FROM public.data_subject_requests WHERE id=@id",
                ("id", arranged.DueClosureA));
            (delete.SqlState, delete.MessageText).Should().Be(("23514", "Data request mutation refused."));
            var insert = await PlatformRefusedAsync(database, FullFencesOfPersonA, """
                INSERT INTO public.data_subject_requests(id,tenant_id,organization_id,kind,origin,subject_user_id,requester_user_id,
                    status,membership_snapshot,execute_at,created_at,created_by)
                VALUES(@id,@tenant,NULL,'erasure','closure_fanout',@subject,NULL,'fulfilling',@snapshot::jsonb,NULL,now(),@system)
                """, ("id", Guid.CreateVersion7()), ("tenant", SchemaFixture.TenantA), ("subject", PersonA),
                ("snapshot", Snapshot(LearnerMembershipA)), ("system", SystemActor));
            (insert.SqlState, insert.MessageText).Should().Be(("23514", "Data request mutation refused."));
        }
        finally
        {
            await using var owner = await OpenAsync(database.MigrationConnectionString);
            await ExecuteAsync(owner, null, "REVOKE INSERT, DELETE ON public.data_subject_requests FROM learnstack_platform");
        }
    }

    /// <summary>
    /// A closure's Finalize (<c>fulfilling → completed</c>) once the subject is Closed, under the fences of Begin, with the
    /// result changed only in the audit kinds and the unserved groups; and the completion of its fan-out erasures
    /// (<c>awaiting_privileged_step → completed</c>) once the subject is Closing or Closed, under the subject's User key and
    /// the request's own tenant key, with the result changed only in the audit kinds. The application role takes none of
    /// the closure's arrows.
    /// </summary>
    [Fact]
    public async Task A_closure_s_Finalize_and_its_fan_out_completions_take_their_arrows_only_under_their_witnesses()
    {
        await using var database = await DataExportTestKit.CreateSeededDatabaseAsync(schema.Postgres);
        var arranged = await ArrangeAsync(database, pendingDue: true);
        await MoveAsync(database, PersonA, Close, (SchemaFixture.TenantA, Mode.Exclusive), (SchemaFixture.TenantB, Mode.Exclusive));
        await PlatformCommitAsync(database, FullFencesOfPersonA, Format(Begin), arranged.DueClosureA);
        var fanA = await FanOutAsync(database, SchemaFixture.TenantA, null, PersonA, LearnerMembershipA);
        var fanB = await FanOutAsync(database, SchemaFixture.TenantB, null, PersonA, arranged.ForeignMembership);

        // The application role's arms are unchanged: none of the closure's arrows is its own.
        await using (var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString))
        {
            foreach (var (tenant, organization, id, set, because) in new (Guid, Guid?, Guid, string, string)[]
            {
                (SchemaFixture.TenantA, SchemaFixture.OrgA1, arranged.PendingClosureA1, "status='fulfilling'", "Begin"),
                (SchemaFixture.TenantA, null, arranged.DueClosureA, "status='completed',completed_at=now()", "Finalize"),
                (SchemaFixture.TenantA, null, fanA, "status='completed',completed_at=now()", "a fan-out's completion"),
            })
            {
                await using var transaction = await app.BeginTransactionAsync();
                await FenceAsync(app, transaction, tenant, organization, PersonA);
                var refusal = await DataExportTestKit.RefuseAsync(transaction, () => StepAsync(app, transaction, id, set));
                (refusal.SqlState, refusal.MessageText).Should().Be(("23514", Transition), because);
            }

            // ADR-0106 § 7's application refusal record admits a code on any scheduled request, a closure's included; no
            // application path writes one, and the closure's own record is the platform's (flagged for the maintainer).
            await using (var transaction = await app.BeginTransactionAsync())
            {
                await FenceAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1, null);
                await StepAsync(app, transaction, arranged.PendingClosureA1, "last_refusal='blocked_hold'");
                await transaction.RollbackAsync();
            }
        }

        // A fan-out's completion while the subject is Closing, admitted with its two fences and refused without either.
        const string Redacted = "result='{\"consent_evidence\":2,\"audit_actor_reference\":4,\"unscoped_audit_rows\":1}'";
        Task FanOutFences(NpgsqlConnection connection, NpgsqlTransaction transaction, Mode user, Mode tenant) =>
            ClosureFencesAsync(connection, transaction, PersonA, user, (SchemaFixture.TenantA, tenant));
        await PlatformAdmittedAsync(database, (connection, transaction) => FanOutFences(connection, transaction, Mode.Exclusive, Mode.Exclusive),
            Format(Finalize + "," + Redacted), fanA);
        foreach (var (user, tenant, reason, because) in new (Mode, Mode, string, string)[]
        {
            (Mode.None, Mode.Exclusive, UserFence, "without the User key"),
            (Mode.Shared, Mode.Exclusive, UserFence, "with the User key shared"),
            (Mode.Exclusive, Mode.None, TenantFence, "without the request's tenant key"),
            (Mode.Exclusive, Mode.Shared, TenantFence, "with the request's tenant key shared"),
        })
        {
            var refusal = await PlatformRefusedAsync(database, (connection, transaction) => FanOutFences(connection, transaction, user, tenant),
                Format(Finalize + "," + Redacted), ("id", fanA));
            (refusal.SqlState, refusal.MessageText).Should().Be(("23514", reason), because);
        }

        foreach (var (set, reason) in new[]
        {
            (Finalize + ",result='{\"consent_evidence\":3,\"audit_actor_reference\":4}'", ResultRefused),
            (Finalize + ",result='{\"audit_actor_reference\":4}'", ResultRefused),
            (Finalize + ",result='{\"consent_evidence\":2,\"audit_actor_reference\":4,\"unserved_group\":1}'", ResultRefused),
            (Finalize + ",last_refusal='blocked_hold'", RefusalRecord),
            ("status='completed'", ImmutableState),
        })
        {
            var refusal = await PlatformRefusedAsync(database, FullFencesOfPersonA, Format(set), ("id", fanA));
            (refusal.SqlState, refusal.MessageText).Should().Be(("23514", reason), set);
        }

        (await PlatformRefusedAsync(database, FullFencesOfPersonA, Format(Finalize), ("id", arranged.DueClosureA)))
            .MessageText.Should().Be(Subject, "Finalize completes the closure only once the subject is Closed");

        await MoveAsync(database, PersonA, Closed, (SchemaFixture.TenantA, Mode.Exclusive), (SchemaFixture.TenantB, Mode.Exclusive));
        (await PlatformRefusedAsync(database, FullFencesOfPersonA, Format(Begin), ("id", arranged.PendingClosureA1)))
            .MessageText.Should().Be(Subject, "no closure is begun once the subject is Closed");

        // Finalize: every condition but one, each time.
        const string Report = "result='{\"audit_actor_reference\":3,\"unserved_group\":1}'";
        foreach (var (user, tenantA, tenantB, reason, because) in new (Mode, Mode, Mode, string, string)[]
        {
            (Mode.None, Mode.Exclusive, Mode.Exclusive, UserFence, "without the User key"),
            (Mode.Shared, Mode.Exclusive, Mode.Exclusive, UserFence, "with the User key shared"),
            (Mode.Exclusive, Mode.None, Mode.Exclusive, TenantFence, "without the request's own tenant key"),
            (Mode.Exclusive, Mode.Shared, Mode.Exclusive, TenantFence, "with the request's tenant key shared"),
            (Mode.Exclusive, Mode.Exclusive, Mode.None, TenantFence, "without the tombstone tenant's key"),
            (Mode.Exclusive, Mode.Exclusive, Mode.Shared, TenantFence, "with the tombstone tenant's key shared"),
        })
        {
            var refusal = await PlatformRefusedAsync(database, PersonAFences(user, tenantA, tenantB), Format(Finalize + "," + Report),
                ("id", arranged.DueClosureA));
            (refusal.SqlState, refusal.MessageText).Should().Be(("23514", reason), because);
        }

        foreach (var (set, reason) in new[]
        {
            (Finalize + ",result='{\"consent_evidence\":1}'", ResultRefused),
            (Finalize + ",result='{\"request_receipt\":1,\"unserved_group\":1}'", ResultRefused),
            (Finalize + ",last_refusal='blocked_hold'", RefusalRecord),
            ("status='completed'," + Report, ImmutableState),
        })
        {
            var refusal = await PlatformRefusedAsync(database, FullFencesOfPersonA, Format(set), ("id", arranged.DueClosureA));
            (refusal.SqlState, refusal.MessageText).Should().Be(("23514", reason), set);
        }

        (await PlatformRefusedAsync(database, FullFencesOfPersonA, Format(Finalize), ("id", arranged.PendingClosureA1)))
            .MessageText.Should().Be(Transition, "a scheduled closure is not completed without its Begin");

        // Finalize's second transaction: every fan-out erasure and the closure completed, under the fences of Begin, with
        // the completion time the database clock's whatever the statement says.
        await using (var platform = await OpenAsync(database.PlatformConnectionString))
        await using (var transaction = await platform.BeginTransactionAsync())
        {
            await FullFencesOfPersonA(platform, transaction);
            foreach (var fan in new[] { fanA, fanB })
                await ExecuteAsync(platform, transaction, Format(Finalize + "," + Redacted + ",completed_at='2001-01-01T00:00:00Z'"), ("id", fan));
            await ExecuteAsync(platform, transaction, Format(Finalize + "," + Report), ("id", arranged.DueClosureA));
            await transaction.CommitAsync();
        }

        var closure = await RequestStateAsync(database, SchemaFixture.TenantA, null, arranged.DueClosureA);
        closure.Should().Be(new RequestState("completed", 2, null, closure.CompletedAt, SystemActor, "{\"unserved_group\": 1, \"audit_actor_reference\": 3}"));
        closure.CompletedAt.Should().NotBeNull().And.BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        foreach (var (tenant, fan) in new[] { (SchemaFixture.TenantA, fanA), (SchemaFixture.TenantB, fanB) })
        {
            var completed = await RequestStateAsync(database, tenant, null, fan);
            completed.Should().Be(new RequestState("completed", 3, null, completed.CompletedAt, SystemActor,
                "{\"consent_evidence\": 2, \"unscoped_audit_rows\": 1, \"audit_actor_reference\": 4}"));
            completed.CompletedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1), "no caller supplies the completion time");
        }

        (await RequestStampedAsync(database, SchemaFixture.TenantA, null, arranged.DueClosureA)).Should().BeTrue(
            "the completion time is the update stamp the arm wrote");
        foreach (var id in new[] { arranged.DueClosureA, fanA })
        {
            var refusal = await PlatformRefusedAsync(database, FullFencesOfPersonA, Format(Finalize), ("id", id));
            (refusal.SqlState, refusal.MessageText).Should().Be(("23514", Transition), "a completed request is terminal");
        }
    }

    /// <summary>
    /// One fan-out erasure as the closure's pipeline frame writes it: born <c>fulfilling</c> by the application role in the
    /// group's own transaction under the subject's User fence, then carried by the application's own arrows to the
    /// privileged step with a tenant-side result.
    /// </summary>
    private static async Task<Guid> FanOutAsync(DisposableSchemaDatabase database, Guid tenant, Guid? organization, Guid subject, Guid membership)
    {
        var id = Guid.CreateVersion7();
        await using var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString);
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await FenceAsync(app, transaction, tenant, organization, subject);
            await InsertFanOutAsync(app, transaction, id, tenant, organization, subject, membership);
            await transaction.CommitAsync();
        }

        await using (var transaction = await app.BeginTransactionAsync())
        {
            await FenceAsync(app, transaction, tenant, organization, null);
            await StepAsync(app, transaction, id, "result='{\"consent_evidence\":2,\"audit_actor_reference\":9}'");
            await StepAsync(app, transaction, id, "status='awaiting_privileged_step'");
            await transaction.CommitAsync();
        }

        return id;
    }

    private static string Format(string set) => "UPDATE public.data_subject_requests SET " + set + " WHERE id=@id";

    /// <summary>Runs one request statement on a fresh platform transaction holding <paramref name="fences"/> and commits it.</summary>
    private static async Task PlatformCommitAsync(DisposableSchemaDatabase database, Func<NpgsqlConnection, NpgsqlTransaction, Task> fences,
        string sql, Guid id)
    {
        await using var platform = await OpenAsync(database.PlatformConnectionString);
        await using var transaction = await platform.BeginTransactionAsync();
        await fences(platform, transaction);
        (await ExecuteCountAsync(platform, transaction, sql, id)).Should().Be(1, sql);
        await transaction.CommitAsync();
    }

    /// <summary>Runs one request statement on a fresh platform transaction holding <paramref name="fences"/>, sees it admitted and rolls back.</summary>
    private static async Task PlatformAdmittedAsync(DisposableSchemaDatabase database, Func<NpgsqlConnection, NpgsqlTransaction, Task> fences,
        string sql, Guid id)
    {
        await using var platform = await OpenAsync(database.PlatformConnectionString);
        await using var transaction = await platform.BeginTransactionAsync();
        await fences(platform, transaction);
        (await ExecuteCountAsync(platform, transaction, sql, id)).Should().Be(1, sql);
        await transaction.RollbackAsync();
    }

    private static async Task<int> ExecuteCountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid id)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteNonQueryAsync();
    }

    private sealed record RequestState(string Status, long Version, string? Refusal, DateTime? CompletedAt, Guid? UpdatedBy, string Result);

    /// <summary>The request row as the owner reads it in its own group, the update stamps included.</summary>
    private static async Task<RequestState> RequestStateAsync(DisposableSchemaDatabase database, Guid tenant, Guid? organization, Guid id)
    {
        await using var owner = await OpenAsync(database.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        await ScopeAsync(owner, transaction, tenant, organization);
        await using var command = new NpgsqlCommand("""
            SELECT status, row_version, last_refusal, completed_at, updated_by, result::text
            FROM public.data_subject_requests WHERE id=@id
            """, owner, transaction);
        command.Parameters.AddWithValue("id", id);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return new RequestState(row.GetString(0), row.GetInt64(1), row.IsDBNull(2) ? null : row.GetString(2),
            row.IsDBNull(3) ? null : row.GetDateTime(3), row.IsDBNull(4) ? null : row.GetGuid(4), row.GetString(5));
    }

    private static async Task<bool> RequestStampedAsync(DisposableSchemaDatabase database, Guid tenant, Guid? organization, Guid id)
    {
        await using var owner = await OpenAsync(database.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        await ScopeAsync(owner, transaction, tenant, organization);
        return await ScalarAsync<bool>(owner, transaction,
            "SELECT completed_at = updated_at FROM public.data_subject_requests WHERE id=@id", ("id", id));
    }
}
