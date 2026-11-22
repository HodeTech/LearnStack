using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.DataRequestSchemaSeed;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class AccountClosureStorageTests
{
    private const string FanOutInsert = """
        INSERT INTO public.data_subject_requests(id,tenant_id,organization_id,kind,origin,subject_user_id,requester_user_id,
            status,membership_snapshot,execute_at,created_at,created_by)
        VALUES(@id,@tenant,@organization,@kind,@origin,@subject,@requester::uuid,@status,@snapshot::jsonb,@execute::timestamptz,
            now(),@creator)
        """;

    private const string InitialState = "Data request initial state refused.";
    private const string NotAdmitted = "Data request subject is not admitted.";

    /// <summary>
    /// The request guard's one admitted birth in <c>fulfilling</c>: a closure's fan-out erasure (kind <c>erasure</c>,
    /// origin <c>closure_fanout</c>, no window, no requester, the system actor) of a subject who is Closing, written by the
    /// application role under the subject's exclusive User fence and the group's tenant fence, with the exact snapshot.
    /// Each condition is removed alone, every other origin and status stays refused, and an Active or Closed subject has none.
    /// </summary>
    [Fact]
    public async Task A_request_is_born_fulfilling_only_as_the_fan_out_erasure_of_a_Closing_subject()
    {
        await using var database = await DataExportTestKit.CreateSeededDatabaseAsync(schema.Postgres);
        var arranged = await ArrangeAsync(database);
        await MoveAsync(database, PersonA, Close, (SchemaFixture.TenantA, Mode.Exclusive), (SchemaFixture.TenantB, Mode.Exclusive));
        await using var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString);

        await using (var transaction = await app.BeginTransactionAsync())
        {
            await FenceAsync(app, transaction, SchemaFixture.TenantA, null, PersonA);
            await InsertFanOutAsync(app, transaction, Guid.CreateVersion7(), SchemaFixture.TenantA, null, PersonA, LearnerMembershipA);
            // An organization's group, under that organization's announcement and the same two fences.
            await ScopeAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
            await InsertFanOutAsync(app, transaction, Guid.CreateVersion7(), SchemaFixture.TenantA, SchemaFixture.OrgA1, PersonA,
                BranchMembershipA1);
            await transaction.RollbackAsync();
        }

        // The fences removed alone: the User fence, held shared, and the tenant fence. These are the arm's existing checks.
        foreach (var (user, tenant, constraint) in new (Mode, Mode, string)[]
        {
            (Mode.None, Mode.Shared, "identity_user_fence"),
            (Mode.Shared, Mode.Shared, "identity_user_fence"),
            (Mode.Exclusive, Mode.None, "identity_tenant_fence"),
        })
        {
            await using var transaction = await app.BeginTransactionAsync();
            await ScopeAsync(app, transaction, SchemaFixture.TenantA);
            await TakeAsync(app, transaction, IdentityAdvisoryLockKey.ForUser(UserId.From(PersonA)), user);
            await TakeAsync(app, transaction, IdentityAdvisoryLockKey.ForTenant(TenantId.From(SchemaFixture.TenantA)), tenant);
            var refusal = await DataExportTestKit.RefuseAsync(transaction, () =>
                InsertFanOutAsync(app, transaction, Guid.CreateVersion7(), SchemaFixture.TenantA, null, PersonA, LearnerMembershipA));
            (refusal.SqlState, refusal.ConstraintName).Should().Be(("P0301", constraint), $"user {user}, tenant {tenant}");
        }

        // Every other field of the fan-out removed alone, each refused by the guard before any constraint.
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await FenceAsync(app, transaction, SchemaFixture.TenantA, null, PersonA);
            Task Born(string kind = "erasure", string origin = "closure_fanout", string status = "fulfilling", Guid? requester = null,
                DateTime? executeAt = null, Guid? creator = null, Guid? membership = null) =>
                InsertFanOutAsync(app, transaction, Guid.CreateVersion7(), SchemaFixture.TenantA, null, PersonA,
                    membership ?? LearnerMembershipA, kind, origin, status, requester, executeAt, creator);
            var tomorrow = DateTime.UtcNow.AddHours(24);
            foreach (var (born, reason, because) in new (Func<Task>, string, string)[]
            {
                (() => Born(executeAt: tomorrow), InitialState, "a fan-out has no window"),
                (() => Born(requester: PersonA, creator: PersonA), InitialState, "a fan-out has no requester"),
                (() => Born(creator: PersonA), InitialState, "a fan-out is the system actor's"),
                (() => Born(kind: "export"), InitialState, "a fan-out is an erasure"),
                (() => Born(kind: "closure"), InitialState, "a fan-out is an erasure, never a closure"),
                (() => Born(membership: BranchMembershipA1), "Data request snapshot refused.", "the snapshot is exactly the group's Memberships"),
                (() => Born(origin: "self", requester: PersonA, creator: PersonA), InitialState, "a person's erasure is born scheduled"),
                (() => Born(origin: "administrator", requester: AdminA, creator: AdminA), InitialState, "an administrator's erasure is born scheduled"),
                (() => Born(kind: "closure", origin: "self", requester: PersonA, creator: PersonA, executeAt: tomorrow), InitialState,
                    "a closure is born scheduled"),
                (() => Born(kind: "export", origin: "self", requester: PersonA, creator: PersonA), InitialState, "an export is born requested"),
            })
            {
                var refusal = await DataExportTestKit.RefuseAsync(transaction, born);
                (refusal.SqlState, refusal.MessageText).Should().Be(("23514", reason), because);
            }

            foreach (var status in new[] { "requested", "scheduled", "awaiting_privileged_step", "completed", "blocked", "failed", "cancelled" })
            {
                var refusal = await DataExportTestKit.RefuseAsync(transaction, () => Born(status: status));
                (refusal.SqlState, refusal.MessageText).Should().Be(("23514", InitialState), "a fan-out is never born " + status);
            }

            await transaction.RollbackAsync();
        }

        // An Active subject has no fan-out.
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await FenceAsync(app, transaction, SchemaFixture.TenantB, SchemaFixture.OrgB1, PersonB);
            var refusal = await DataExportTestKit.RefuseAsync(transaction, () =>
                InsertFanOutAsync(app, transaction, Guid.CreateVersion7(), SchemaFixture.TenantB, SchemaFixture.OrgB1, PersonB, BranchMembershipB));
            (refusal.SqlState, refusal.MessageText).Should().Be(("23514", NotAdmitted), "person B is Active");
        }

        // Nor has a Closed one: once Finalize closed the User, no group's erasure starts.
        await MoveAsync(database, PersonA, Closed, (SchemaFixture.TenantA, Mode.Exclusive), (SchemaFixture.TenantB, Mode.Exclusive));
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await FenceAsync(app, transaction, SchemaFixture.TenantB, null, PersonA);
            var refusal = await DataExportTestKit.RefuseAsync(transaction, () =>
                InsertFanOutAsync(app, transaction, Guid.CreateVersion7(), SchemaFixture.TenantB, null, PersonA, arranged.ForeignMembership));
            (refusal.SqlState, refusal.MessageText).Should().Be(("23514", NotAdmitted), "person A is Closed");
        }
    }

    /// <summary>
    /// Every request the CHECKs admit, each kind and origin, is filed only for a subject in the one status its origin
    /// allows, read by the guard under the subject's exclusive User fence: Active for an export, an erasure and a closure
    /// of the person or an administrator, Closing for a closure's fan-out. The same filings are admitted while person A is
    /// Active, refused once person A is Closing but the fan-out, and refused once person A is Closed.
    /// </summary>
    [Fact]
    public async Task Every_request_is_filed_only_for_a_subject_in_the_status_its_origin_admits()
    {
        await using var database = await DataExportTestKit.CreateSeededDatabaseAsync(schema.Postgres);
        await using var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString);
        (string Kind, string Origin, string Status, Guid? Requester, bool Window)[] filings =
        [
            ("export", "self", "requested", PersonA, false),
            ("export", "administrator", "requested", AdminA, false),
            ("erasure", "self", "scheduled", PersonA, true),
            ("erasure", "administrator", "scheduled", AdminA, true),
            ("closure", "self", "scheduled", PersonA, true),
            ("erasure", "closure_fanout", "fulfilling", null, false),
        ];

        async Task ExpectAsync(string subjectStatus, Func<string, bool> admitted)
        {
            await using var transaction = await app.BeginTransactionAsync();
            await FenceAsync(app, transaction, SchemaFixture.TenantA, null, PersonA);
            foreach (var (kind, origin, status, requester, window) in filings)
            {
                // The window is the CHECK's exact 24 hours after the row's own creation time, which is the transaction's.
                Task File() => ExecuteAsync(app, transaction, FanOutInsert.Replace("@execute::timestamptz",
                        window ? "now() + interval '24 hours'" : "NULL::timestamptz", StringComparison.Ordinal),
                    ("id", Guid.CreateVersion7()), ("tenant", SchemaFixture.TenantA), ("organization", null), ("kind", kind),
                    ("origin", origin), ("subject", PersonA), ("requester", requester), ("status", status),
                    ("snapshot", Snapshot(LearnerMembershipA)), ("creator", requester ?? SystemActor));
                var label = $"{kind}/{origin} for a {subjectStatus} subject";
                if (admitted(origin))
                {
                    await transaction.SaveAsync("filing");
                    await File();
                    await transaction.RollbackAsync("filing");
                }
                else
                {
                    var refusal = await DataExportTestKit.RefuseAsync(transaction, File);
                    (refusal.SqlState, refusal.MessageText).Should().Be(("23514", NotAdmitted), label);
                }
            }

            await transaction.RollbackAsync();
        }

        await ExpectAsync("Active", origin => origin != "closure_fanout");
        await MoveAsync(database, PersonA, Close, (SchemaFixture.TenantA, Mode.Exclusive));
        await ExpectAsync("Closing", origin => origin == "closure_fanout");
        await MoveAsync(database, PersonA, Closed, (SchemaFixture.TenantA, Mode.Exclusive));
        await ExpectAsync("Closed", _ => false);
    }

    /// <summary>
    /// The race the witness closes: a person's filing whose transaction reaches the User fence only after a Begin that
    /// moved the person to Closing committed. It waits on the fence the platform holds, then reads the committed status
    /// under it and inserts nothing, so no closure stays scheduled behind a closing account.
    /// </summary>
    [Fact]
    public async Task A_filing_that_waited_on_the_User_fence_behind_a_Begin_reads_the_committed_status_and_inserts_nothing()
    {
        await using var database = await DataExportTestKit.CreateSeededDatabaseAsync(schema.Postgres);
        await using var platform = await OpenAsync(database.PlatformConnectionString);
        await using var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString);
        await using var observer = await OpenAsync(database.MigrationConnectionString);

        await using var begin = await platform.BeginTransactionAsync();
        await ClosureFencesAsync(platform, begin, PersonA, Mode.Exclusive, (SchemaFixture.TenantA, Mode.Exclusive));
        await ExecuteAsync(platform, begin, Close, ("user", PersonA));

        await using var filing = await app.BeginTransactionAsync();
        var filed = Task.Run(async () =>
        {
            await FenceAsync(app, filing, SchemaFixture.TenantA, SchemaFixture.OrgA1, PersonA);
            await InsertRequestAsync(app, filing, Guid.CreateVersion7(), SchemaFixture.TenantA, SchemaFixture.OrgA1, PersonA,
                [BranchMembershipA1], "closure");
        });
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!await ScalarAsync<bool>(observer, null,
            "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_locks WHERE pid=@pid AND locktype='advisory' AND NOT granted)",
            ("pid", app.ProcessID)))
        {
            filed.IsCompleted.Should().BeFalse("the filing waits on the User fence the Begin holds");
            DateTime.UtcNow.Should().BeBefore(deadline);
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        await begin.CommitAsync();
        var refusal = (await FluentActions.Awaiting(() => filed).Should().ThrowAsync<PostgresException>()).Which;
        (refusal.SqlState, refusal.MessageText).Should().Be(("23514", NotAdmitted),
            "the person was Active when the filing began and Closing when it reached the fence");
    }

    /// <summary>
    /// The guard's witness returns the subject's status and nothing else, and only inside a trigger under the subject's
    /// exclusive User fence: called directly it returns NULL, as it does without the fence, with the fence held shared and
    /// for an unknown subject. It is a definer owned by the NOBYPASSRLS migration role with a fixed search path, and only
    /// the application role may call it.
    /// </summary>
    [Fact]
    public async Task The_status_witness_answers_only_inside_a_trigger_under_the_subject_s_exclusive_User_fence()
    {
        await using var database = await DataExportTestKit.CreateSeededDatabaseAsync(schema.Postgres);
        await ArrangeAsync(database);
        await MoveAsync(database, PersonA, Close, (SchemaFixture.TenantA, Mode.Exclusive), (SchemaFixture.TenantB, Mode.Exclusive));
        const string Direct = "SELECT COALESCE(public.fn_identity_user_status(@user), 'none')";

        await using (var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await TakeAsync(app, transaction, IdentityAdvisoryLockKey.ForUser(UserId.From(PersonA)), Mode.Exclusive);
            (await ScalarAsync<string>(app, transaction, Direct, ("user", PersonA))).Should().Be("none",
                "outside a trigger the witness reveals nothing, even under the subject's fence");
        }

        // A probe trigger on a temporary table of the owner's own session: the definer runs as its owner whoever calls it,
        // so this observes exactly what the request guard's INSERT arm observes.
        await using var owner = await OpenAsync(database.MigrationConnectionString);
        await ExecuteAsync(owner, null, """
            CREATE TEMP TABLE status_probe (id uuid, status text);
            CREATE FUNCTION pg_temp.status_probe() RETURNS trigger LANGUAGE plpgsql AS $probe$
            BEGIN NEW.status := COALESCE(public.fn_identity_user_status(NEW.id), 'none'); RETURN NEW; END $probe$;
            CREATE TRIGGER status_probe BEFORE INSERT ON pg_temp.status_probe FOR EACH ROW EXECUTE FUNCTION pg_temp.status_probe();
            """);
        Task<string> Probe(NpgsqlTransaction transaction, Guid user) =>
            ScalarAsync<string>(owner, transaction, "INSERT INTO pg_temp.status_probe(id) VALUES(@user) RETURNING status", ("user", user));

        await using (var transaction = await owner.BeginTransactionAsync())
        {
            (await Probe(transaction, PersonA)).Should().Be("none", "without the subject's User fence");
            await TakeAsync(owner, transaction, IdentityAdvisoryLockKey.ForUser(UserId.From(PersonA)), Mode.Shared);
            (await Probe(transaction, PersonA)).Should().Be("none", "with the User fence held shared");
            await TakeAsync(owner, transaction, IdentityAdvisoryLockKey.ForUser(UserId.From(PersonA)), Mode.Exclusive);
            (await Probe(transaction, PersonA)).Should().Be("Closing", "inside a trigger, under the exclusive fence");
            (await ScalarAsync<string>(owner, transaction, Direct, ("user", PersonA))).Should().Be("none", "but never called directly");
            await TakeAsync(owner, transaction, IdentityAdvisoryLockKey.ForUser(UserId.From(PersonB)), Mode.Exclusive);
            (await Probe(transaction, PersonB)).Should().Be("Active");
            var unknown = Guid.CreateVersion7();
            await TakeAsync(owner, transaction, IdentityAdvisoryLockKey.ForUser(UserId.From(unknown)), Mode.Exclusive);
            (await Probe(transaction, unknown)).Should().Be("none", "no such User");
            await transaction.RollbackAsync();
        }

        await MoveAsync(database, PersonA, Closed, (SchemaFixture.TenantA, Mode.Exclusive), (SchemaFixture.TenantB, Mode.Exclusive));
        await using (var transaction = await owner.BeginTransactionAsync())
        {
            await TakeAsync(owner, transaction, IdentityAdvisoryLockKey.ForUser(UserId.From(PersonA)), Mode.Exclusive);
            (await Probe(transaction, PersonA)).Should().Be("Closed");
            await transaction.RollbackAsync();
        }

        (await SchemaQueries.ReadStringsAsync(owner, """
            SELECT concat_ws(':', pg_get_userbyid(p.proowner), p.prosecdef, p.provolatile, array_to_string(p.proconfig, ','),
                r.rolbypassrls, has_function_privilege('learnstack_app', p.oid, 'EXECUTE'),
                has_function_privilege('learnstack_platform', p.oid, 'EXECUTE'),
                has_function_privilege('learnstack_outbox_admin', p.oid, 'EXECUTE'),
                has_function_privilege('learnstack_identity_ingress', p.oid, 'EXECUTE'),
                has_function_privilege('learnstack_identity_rebind', p.oid, 'EXECUTE'),
                EXISTS (SELECT 1 FROM aclexplode(COALESCE(p.proacl, acldefault('f', p.proowner))) a WHERE a.grantee = 0),
                pg_get_function_result(p.oid))
            FROM pg_proc p JOIN pg_roles r ON r.oid = p.proowner
            WHERE p.oid = 'public.fn_identity_user_status(uuid)'::regprocedure
            """)).Should().Equal(["learnstack_migration:t:v:search_path=pg_catalog:f:t:f:f:f:f:f:text"]);
    }

    private static Task InsertFanOutAsync(NpgsqlConnection app, NpgsqlTransaction transaction, Guid id, Guid tenant, Guid? organization,
        Guid subject, Guid membership, string kind = "erasure", string origin = "closure_fanout", string status = "fulfilling",
        Guid? requester = null, DateTime? executeAt = null, Guid? creator = null) =>
        ExecuteAsync(app, transaction, FanOutInsert, ("id", id), ("tenant", tenant), ("organization", organization), ("kind", kind),
            ("origin", origin), ("subject", subject), ("requester", requester), ("status", status),
            ("snapshot", Snapshot(membership)), ("execute", executeAt), ("creator", creator ?? SystemActor));
}
