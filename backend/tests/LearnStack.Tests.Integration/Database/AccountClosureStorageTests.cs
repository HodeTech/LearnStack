using System.Globalization;
using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.DataRequestSchemaSeed;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0107 §§ 7 and 8 and ADR-0072 Amendment 8, on real PostgreSQL as each real role: the platform role's closed-column
/// grants, the one <c>users</c> trigger exception, the request guard's closure arrows and the fan-out erasure's INSERT
/// witness. Every arrow is admitted with its exact witnesses and refused with each one removed alone.
/// </summary>
/// <remarks>
/// <para>
/// Each case runs in its own disposable copy of the seeded schema, because a User a closure moved never returns to Active.
/// Person A holds Memberships in tenant A (tenant-wide and both branches) and a Revoked tombstone of a tenant-wide
/// Membership of tenant B, so tenant B is one of person A's participants through the tombstone alone. Person B holds one
/// Membership, in tenant B's branch. A closure of person A is due in tenant A's tenant-wide group and another is still
/// in its window in branch A1; a closure of person B is due in branch B1.
/// </para>
/// <para>
/// The platform role holds its fences the way the closure's acquirer does: transaction-scoped advisory locks, the User key
/// first and the tenant keys after it, ascending by key. A refusal probe rolls back.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class AccountClosureStorageTests(SchemaFixture schema)
{
    private const string NoOwner = "Identity bindings have no admitted update or removal owner.";
    private const string Close = "UPDATE public.users SET status='Closing' WHERE id=@user";
    private const string Closed = "UPDATE public.users SET status='Closed', provider_subject=NULL WHERE id=@user";

    private const string RevokeSql = """
        UPDATE public.memberships SET status='Revoked', deleted_at=clock_timestamp(), deleted_by=@system,
            updated_at=clock_timestamp(), updated_by=@system, row_version=row_version+1
        WHERE id=@member
        """;

    private enum Mode { None, Shared, Exclusive }

    private sealed record Arranged(Guid ForeignMembership, Guid DueClosureA, Guid PendingClosureA1, Guid DueClosureB);

    /// <summary>
    /// The User trigger exception: the platform session moves a User Active to Closing and Closing to Closed only with the
    /// subject's User key and the key of every participant tenant held exclusively, changes nothing but the two columns,
    /// and stamps the bookkeeping itself. Each condition is removed alone.
    /// </summary>
    [Fact]
    public async Task The_platform_session_moves_a_User_only_by_the_two_transitions_under_every_participant_fence()
    {
        await using var database = await DataExportTestKit.CreateSeededDatabaseAsync(schema.Postgres);
        await ArrangeAsync(database);
        var before = await UserStateAsync(database, PersonA);

        // Each fence removed alone, or held shared: the User key, the tenant key of a live Membership's tenant, and the
        // tenant key of the tenant person A holds only a Revoked tombstone in.
        foreach (var (user, tenantA, tenantB, reason, because) in new (Mode, Mode, Mode, string, string)[]
        {
            (Mode.None, Mode.Exclusive, Mode.Exclusive, "User closure fence refused.", "without the User key"),
            (Mode.Shared, Mode.Exclusive, Mode.Exclusive, "User closure fence refused.", "with the User key held shared"),
            (Mode.Exclusive, Mode.Exclusive, Mode.None, "User closure tenant fence refused.", "without the tombstone tenant's key"),
            (Mode.Exclusive, Mode.None, Mode.Exclusive, "User closure tenant fence refused.", "without the live tenant's key"),
            (Mode.Exclusive, Mode.Shared, Mode.Exclusive, "User closure tenant fence refused.", "with a tenant key held shared"),
            (Mode.Exclusive, Mode.Exclusive, Mode.Shared, "User closure tenant fence refused.", "with the tombstone tenant's key shared"),
        })
        {
            var refusal = await PlatformRefusedAsync(database, PersonAFences(user, tenantA, tenantB), Close, ("user", PersonA));
            (refusal.SqlState, refusal.MessageText).Should().Be(("23514", reason), because);
        }

        // Every transition but the two, each with every fence held.
        foreach (var (sql, because) in new[]
        {
            ("UPDATE public.users SET status='Closed', provider_subject=NULL WHERE id=@user", "Active to Closed skips Closing"),
            ("UPDATE public.users SET status='Active' WHERE id=@user", "Active to Active is no transition"),
            ("UPDATE public.users SET status='Closing', provider_subject=NULL WHERE id=@user", "Active to Closing keeps the subject"),
            ("UPDATE public.users SET status='Closing', provider_subject='another-subject' WHERE id=@user",
                "Active to Closing cannot rewrite the subject"),
        })
        {
            var refusal = await PlatformRefusedAsync(database, FullFencesOfPersonA, sql, ("user", PersonA));
            (refusal.SqlState, refusal.MessageText).Should().Be(("23514", "User closure transition refused."), because);
        }

        await using (var platform = await OpenAsync(database.PlatformConnectionString))
        await using (var transaction = await platform.BeginTransactionAsync())
        {
            await FullFencesOfPersonA(platform, transaction);
            await ExecuteAsync(platform, transaction, Close, ("user", PersonA));
            foreach (var (sql, because) in new[]
            {
                ("UPDATE public.users SET status='Active' WHERE id=@user", "nothing returns to Active"),
                ("UPDATE public.users SET status='Closing' WHERE id=@user", "Closing to Closing is no transition"),
                ("UPDATE public.users SET status='Closed' WHERE id=@user", "Closed removes the raw subject"),
            })
            {
                var refusal = await DataExportTestKit.RefuseAsync(transaction, () => ExecuteAsync(platform, transaction, sql, ("user", PersonA)));
                (refusal.SqlState, refusal.MessageText).Should().Be(("23514", "User closure transition refused."), because);
            }

            await ExecuteAsync(platform, transaction, Closed, ("user", PersonA));
            foreach (var (sql, because) in new[]
            {
                ("UPDATE public.users SET status='Closing' WHERE id=@user", "Closed never reopens"),
                ("UPDATE public.users SET status='Active' WHERE id=@user", "Closed never returns to Active"),
                ("UPDATE public.users SET status='Closed' WHERE id=@user", "Closed to Closed is no transition"),
            })
            {
                var refusal = await DataExportTestKit.RefuseAsync(transaction, () => ExecuteAsync(platform, transaction, sql, ("user", PersonA)));
                (refusal.SqlState, refusal.MessageText).Should().Be(("23514", "User closure transition refused."), because);
            }

            await transaction.RollbackAsync();
        }

        // Only the two columns: a third is refused by the grant, and, with the grant widened in this disposable database,
        // by the trigger itself. The bookkeeping columns are the trigger's to stamp, never the statement's.
        foreach (var column in new[] { "row_version=row_version+1", "updated_at=now()", "subject_key=subject_key", "authority_id=authority_id" })
        {
            var refusal = await PlatformRefusedAsync(database, FullFencesOfPersonA, $"UPDATE public.users SET status='Closing',{column} WHERE id=@user",
                ("user", PersonA));
            refusal.SqlState.Should().Be("42501", column);
        }

        await using (var owner = await OpenAsync(database.MigrationConnectionString))
            await ExecuteAsync(owner, null, "GRANT UPDATE (row_version, created_at, updated_by) ON public.users TO learnstack_platform");
        try
        {
            // Constants and the readable id only: the role still reads none of these columns.
            foreach (var column in new[] { "row_version=99", "created_at='2001-01-01T00:00:00Z'", "updated_by=id" })
            {
                var refusal = await PlatformRefusedAsync(database, FullFencesOfPersonA,
                    $"UPDATE public.users SET status='Closing',{column} WHERE id=@user", ("user", PersonA));
                (refusal.SqlState, refusal.MessageText).Should().Be(("23514", "User closure column refused."), column);
            }
        }
        finally
        {
            await using var owner = await OpenAsync(database.MigrationConnectionString);
            await ExecuteAsync(owner, null, "REVOKE UPDATE (row_version, created_at, updated_by) ON public.users FROM learnstack_platform");
        }

        // Admitted, committed, and stamped by the trigger: the version steps once per transition, the update is the
        // system actor's, and the key, the authority and the creation evidence stay as the tombstone.
        await MoveAsync(database, PersonA, Close, (SchemaFixture.TenantA, Mode.Exclusive), (SchemaFixture.TenantB, Mode.Exclusive));
        var closing = await UserStateAsync(database, PersonA);
        closing.UpdatedAt.Should().NotBeNull();
        closing.Should().Be(before with { Status = "Closing", Version = before.Version + 1, UpdatedBy = SystemActor, UpdatedAt = closing.UpdatedAt },
            "Active to Closing keeps the raw subject and changes nothing else the statement could reach");
        await MoveAsync(database, PersonA, Closed, (SchemaFixture.TenantA, Mode.Exclusive), (SchemaFixture.TenantB, Mode.Exclusive));
        var closed = await UserStateAsync(database, PersonA);
        closed.Should().Be(closing with { Status = "Closed", SubjectPresent = false, Version = before.Version + 2, UpdatedAt = closed.UpdatedAt },
            "Closing to Closed removes the raw subject and keeps the key and actor as the permanent tombstone");
        closed.UpdatedAt.Should().BeOnOrAfter(closing.UpdatedAt!.Value);
    }

    /// <summary>
    /// The exception is the platform session's alone: the application role, the migration owner and a superuser session
    /// switched to the platform role are refused while holding every fence, and DELETE and TRUNCATE stay refused for
    /// every role, the platform role included once its grant is widened in this disposable database.
    /// </summary>
    [Fact]
    public async Task Every_other_session_and_every_removal_is_refused_even_under_every_fence()
    {
        await using var database = await DataExportTestKit.CreateSeededDatabaseAsync(schema.Postgres);
        await ArrangeAsync(database);

        // The application role holds no UPDATE on users at all; widened to the status column and reading its own row
        // through its identity announcement, it reaches the trigger, which refuses it.
        await using (var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await AnnounceAsync(app, transaction, PersonA);
            await FullFencesOfPersonA(app, transaction);
            (await DataExportTestKit.RefuseAsync(transaction, () => ExecuteAsync(app, transaction, Close, ("user", PersonA))))
                .SqlState.Should().Be("42501", "the application role cannot make a User Closing");
        }

        await using (var owner = await OpenAsync(database.MigrationConnectionString))
            await ExecuteAsync(owner, null, "GRANT UPDATE (status) ON public.users TO learnstack_app");
        try
        {
            await using var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString);
            await using var transaction = await app.BeginTransactionAsync();
            await AnnounceAsync(app, transaction, PersonA);
            await FullFencesOfPersonA(app, transaction);
            (await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.users WHERE id=@user", ("user", PersonA)))
                .Should().Be(1, "the application role sees the row it would change, so only the trigger can refuse it");
            var refusal = await DataExportTestKit.RefuseAsync(transaction, () => ExecuteAsync(app, transaction, Close, ("user", PersonA)));
            (refusal.SqlState, refusal.MessageText).Should().Be(("23514", NoOwner));
        }
        finally
        {
            await using var owner = await OpenAsync(database.MigrationConnectionString);
            await ExecuteAsync(owner, null, "REVOKE UPDATE (status) ON public.users FROM learnstack_app");
        }

        // The migration owner holds every privilege and reads every row; the trigger refuses it.
        await using (var owner = await OpenAsync(database.MigrationConnectionString))
        await using (var transaction = await owner.BeginTransactionAsync())
        {
            await FullFencesOfPersonA(owner, transaction);
            var refusal = await DataExportTestKit.RefuseAsync(transaction, () => ExecuteAsync(owner, transaction, Close, ("user", PersonA)));
            (refusal.SqlState, refusal.MessageText).Should().Be(("23514", NoOwner));
        }

        // A session whose current role is the platform role but whose login is not: the superuser, switched.
        var keys = new[] { IdentityAdvisoryLockKey.ForUser(UserId.From(PersonA)) }
            .Concat(new[] { SchemaFixture.TenantA, SchemaFixture.TenantB }.Select(tenant => IdentityAdvisoryLockKey.ForTenant(TenantId.From(tenant))).Order())
            .Select(key => $"SELECT pg_catalog.pg_advisory_xact_lock({key.ToString(CultureInfo.InvariantCulture)});");
        var switched = () => schema.Postgres.ExecuteAsSuperuserAsync(
            $"BEGIN; SET LOCAL ROLE learnstack_platform; {string.Join(' ', keys)} "
            + $"UPDATE public.users SET status='Closing' WHERE id='{PersonA:D}'; COMMIT;",
            new NpgsqlConnectionStringBuilder(database.MigrationConnectionString).Database);
        (await switched.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be("Superuser statement failed (SQLSTATE 23514).",
            "the trigger requires the platform login itself, not only its current role");

        // Removal: refused by the missing grant, and by the trigger once the grant is widened.
        string[] references = ["users", "memberships", "membership_profiles", "membership_role_assignments", "invitations",
            "data_subject_requests", "data_export_artifacts", "data_export_tickets", "data_export_chunks"];
        // The users table first, so its own statement trigger is the first TRUNCATE trigger to fire.
        var truncate = "TRUNCATE " + string.Join(", ", references.Select(table => "public." + table));
        var delete = $"DELETE FROM public.users WHERE id='{PersonA:D}'";
        foreach (var sql in new[] { delete, truncate })
            (await PlatformRefusedAsync(database, FullFencesOfPersonA, sql)).SqlState.Should().Be("42501", sql);
        await using (var owner = await OpenAsync(database.MigrationConnectionString))
            await ExecuteAsync(owner, null, $"""
                GRANT DELETE ON public.users TO learnstack_platform;
                GRANT TRUNCATE ON {string.Join(", ", references.Select(table => "public." + table))} TO learnstack_platform;
                """);
        try
        {
            foreach (var sql in new[] { delete, truncate })
            {
                var refusal = await PlatformRefusedAsync(database, FullFencesOfPersonA, sql);
                (refusal.SqlState, refusal.MessageText).Should().Be(("23514", NoOwner), sql);
            }
        }
        finally
        {
            await using var owner = await OpenAsync(database.MigrationConnectionString);
            await ExecuteAsync(owner, null, $"""
                REVOKE DELETE ON public.users FROM learnstack_platform;
                REVOKE TRUNCATE ON {string.Join(", ", references.Select(table => "public." + table))} FROM learnstack_platform;
                """);
        }

        (await UserStateAsync(database, PersonA)).Status.Should().Be("Active", "nothing above changed the User");
    }

    /// <summary>
    /// A subject with no Membership has no participant to fence and no closure to file (ADR-0107 § 10 gives that person to
    /// Phase 11): an UPDATE with no fence at all is refused, and one holding the User key is refused for the empty set.
    /// </summary>
    [Fact]
    public async Task A_subject_with_no_Membership_is_never_moved()
    {
        await using var database = await DataExportTestKit.CreateSeededDatabaseAsync(schema.Postgres);
        var lonely = Guid.CreateVersion7();
        await InsertUserAsync(database.AppConnectionString, lonely);

        var unfenced = await PlatformRefusedAsync(database, (_, _) => Task.CompletedTask, Close, ("user", lonely));
        (unfenced.SqlState, unfenced.MessageText).Should().Be(("23514", "User closure fence refused."));
        var fenced = await PlatformRefusedAsync(database, (connection, transaction) => ClosureFencesAsync(connection, transaction, lonely, Mode.Exclusive),
            Close, ("user", lonely));
        (fenced.SqlState, fenced.MessageText).Should().Be(("23514", "User closure participant refused."));
        (await UserStateAsync(database, lonely)).Status.Should().Be("Active");
    }

    /// <summary>Person A's tombstone in tenant B, and the three closures, two of them past their window.</summary>
    private static async Task<Arranged> ArrangeAsync(DisposableSchemaDatabase database, bool pendingDue = false)
    {
        var arranged = new Arranged(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await using var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString);
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await ScopeAsync(app, transaction, SchemaFixture.TenantB);
            await LockAsync(app, transaction, SchemaFixture.TenantB, PersonA, false);
            await InsertMemberAsync(app, transaction, SchemaFixture.TenantB, null, arranged.ForeignMembership, PersonA,
                RoleId(SchemaFixture.TenantB, BuiltInRoleKey.Learner, PermissionScope.Tenant));
            await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
            await transaction.CommitAsync();
        }

        await using (var transaction = await app.BeginTransactionAsync())
        {
            await ScopeAsync(app, transaction, SchemaFixture.TenantB);
            await LockAsync(app, transaction, SchemaFixture.TenantB, PersonA, false);
            await ExecuteAsync(app, transaction, RevokeSql, ("member", arranged.ForeignMembership), ("system", SystemActor));
            await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
            await transaction.CommitAsync();
        }

        (Guid Id, Guid Tenant, Guid? Organization, Guid Subject, Guid Membership, bool Due)[] closures =
        [
            (arranged.DueClosureA, SchemaFixture.TenantA, null, PersonA, LearnerMembershipA, true),
            (arranged.PendingClosureA1, SchemaFixture.TenantA, SchemaFixture.OrgA1, PersonA, BranchMembershipA1, pendingDue),
            (arranged.DueClosureB, SchemaFixture.TenantB, SchemaFixture.OrgB1, PersonB, BranchMembershipB, true),
        ];
        foreach (var closure in closures)
        {
            await using var transaction = await app.BeginTransactionAsync();
            await FenceAsync(app, transaction, closure.Tenant, closure.Organization, closure.Subject);
            await InsertRequestAsync(app, transaction, closure.Id, closure.Tenant, closure.Organization, closure.Subject,
                [closure.Membership], "closure");
            await transaction.CommitAsync();
        }

        // What a closure filed 25 hours ago looks like: backdated by the owner with the one guard paused, then restored.
        await using var owner = await OpenAsync(database.MigrationConnectionString);
        await using (var transaction = await owner.BeginTransactionAsync())
        {
            await ExecuteAsync(owner, transaction, "ALTER TABLE public.data_subject_requests DISABLE TRIGGER tg_data_subject_requests_mutation_guard");
            foreach (var closure in closures.Where(closure => closure.Due))
            {
                await ScopeAsync(owner, transaction, closure.Tenant, closure.Organization);
                await ExecuteAsync(owner, transaction, """
                    UPDATE public.data_subject_requests SET created_at=created_at - interval '25 hours',
                        execute_at=execute_at - interval '25 hours' WHERE id=@id
                    """, ("id", closure.Id));
            }
            await ExecuteAsync(owner, transaction, "ALTER TABLE public.data_subject_requests ENABLE TRIGGER tg_data_subject_requests_mutation_guard");
            await transaction.CommitAsync();
        }

        return arranged;
    }

    /// <summary>
    /// The closure's fences on one transaction, as its acquirer takes them: the subject's User key first, then each tenant
    /// key ascending by key, each in its own mode; <see cref="Mode.None"/> leaves a key out.
    /// </summary>
    private static async Task ClosureFencesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid subject, Mode user,
        params (Guid Tenant, Mode Mode)[] tenants)
    {
        await TakeAsync(connection, transaction, IdentityAdvisoryLockKey.ForUser(UserId.From(subject)), user);
        foreach (var (tenant, mode) in tenants.OrderBy(entry => IdentityAdvisoryLockKey.ForTenant(TenantId.From(entry.Tenant))))
            await TakeAsync(connection, transaction, IdentityAdvisoryLockKey.ForTenant(TenantId.From(tenant)), mode);
    }

    private static Task TakeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long key, Mode mode) => mode switch
    {
        Mode.None => Task.CompletedTask,
        Mode.Shared => ExecuteAsync(connection, transaction, "SELECT pg_catalog.pg_advisory_xact_lock_shared(@key)", ("key", key)),
        _ => ExecuteAsync(connection, transaction, "SELECT pg_catalog.pg_advisory_xact_lock(@key)", ("key", key)),
    };

    private static Func<NpgsqlConnection, NpgsqlTransaction, Task> PersonAFences(Mode user, Mode tenantA, Mode tenantB) =>
        (connection, transaction) => ClosureFencesAsync(connection, transaction, PersonA, user,
            (SchemaFixture.TenantA, tenantA), (SchemaFixture.TenantB, tenantB));

    private static Task FullFencesOfPersonA(NpgsqlConnection connection, NpgsqlTransaction transaction) =>
        PersonAFences(Mode.Exclusive, Mode.Exclusive, Mode.Exclusive)(connection, transaction);

    /// <summary>Runs one statement on a fresh platform transaction holding <paramref name="fences"/>, expects the refusal and rolls back.</summary>
    private static async Task<PostgresException> PlatformRefusedAsync(DisposableSchemaDatabase database,
        Func<NpgsqlConnection, NpgsqlTransaction, Task> fences, string sql, params (string Name, object? Value)[] values)
    {
        await using var platform = await OpenAsync(database.PlatformConnectionString);
        await using var transaction = await platform.BeginTransactionAsync();
        await fences(platform, transaction);
        var act = () => ExecuteAsync(platform, transaction, sql, values);
        return (await act.Should().ThrowAsync<PostgresException>(sql)).Which;
    }

    /// <summary>Commits one User statement on the platform role under the subject's User key and the given tenant keys.</summary>
    private static async Task MoveAsync(DisposableSchemaDatabase database, Guid user, string sql, params (Guid Tenant, Mode Mode)[] tenants)
    {
        await using var platform = await OpenAsync(database.PlatformConnectionString);
        await using var transaction = await platform.BeginTransactionAsync();
        await ClosureFencesAsync(platform, transaction, user, Mode.Exclusive, tenants);
        await ExecuteAsync(platform, transaction, sql, ("user", user));
        await transaction.CommitAsync();
    }

    private sealed record UserState(string Status, bool SubjectPresent, long Version, Guid? UpdatedBy, DateTime? UpdatedAt, string Tombstone);

    /// <summary>The User row as the owner reads it, the permanent columns folded into one comparable value.</summary>
    private static async Task<UserState> UserStateAsync(DisposableSchemaDatabase database, Guid user)
    {
        await using var owner = await OpenAsync(database.MigrationConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT status, provider_subject IS NOT NULL, row_version, updated_by, updated_at,
                concat_ws(':', id, authority_id, encode(subject_key, 'hex'), creation_audit_id, creation_audit_timestamp, created_at, created_by)
            FROM public.users WHERE id=@user
            """, owner);
        command.Parameters.AddWithValue("user", user);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return new UserState(row.GetString(0), row.GetBoolean(1), row.GetInt64(2), row.IsDBNull(3) ? null : row.GetGuid(3),
            row.IsDBNull(4) ? null : row.GetDateTime(4), row.GetString(5));
    }

    /// <summary>Announces person A's own verified identity, so the application role sees exactly its own User row.</summary>
    private static Task AnnounceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid user) =>
        GlobalIdentitySchemaTests.AnnounceAsync(connection, transaction,
            new GlobalIdentitySubject(Authority.AuthorityId, Authority.Issuer, "fixture-subject-" + user.ToString("N")));

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString) =>
        (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
}
