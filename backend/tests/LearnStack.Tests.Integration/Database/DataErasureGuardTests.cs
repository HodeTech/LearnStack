using FluentAssertions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.DataRequestSchemaSeed;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0107 § 5's one database exception, as <c>learnstack_app</c>: the contextual mutation guard admits a clearing UPDATE
/// of exactly the three personal columns of a Revoked Membership's profile only while the connection holds that person's
/// User fence and the tenant key, both exclusively, and that person's erasure request for that very group is
/// <c>fulfilling</c>. Each condition is removed alone and the UPDATE is refused; every other refusal of the guard stands.
/// </summary>
/// <remarks>
/// Tenant A's tenant-wide learner Membership of person A is Revoked and person A's tenant-wide erasure is durable
/// <c>fulfilling</c>; person A's first branch Membership is Revoked with no erasure in that branch; tenant B's branch
/// Membership of person B is Revoked with a scheduled erasure that is not yet due. Every probe runs in its own
/// transaction and rolls back.
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class DataErasureGuardTests(SchemaFixture fixture)
{
    private const string Clear = """
        UPDATE public.membership_profiles SET display_name=NULL, preferred_locale=NULL, custom_fields='[]'::jsonb
        WHERE membership_id=@member
        """;

    private const string MembershipParentRefusal = "P0304";
    private const string FenceRefusal = "P0301";
    private const string PrivilegeRefusal = "42501";

    [Fact]
    public async Task A_clearing_update_of_a_revoked_profile_is_admitted_only_under_every_condition_of_the_erasure_witness()
    {
        await using var database = await DataExportTestKit.CreateSeededDatabaseAsync(fixture.Postgres);
        var erasure = await ArrangeAsync(database);

        // Admitted: both fences exclusive, the person's fulfilling erasure of exactly this group, the three columns cleared.
        await ProbeAsync(database, SchemaFixture.TenantA, null, PersonA, Fence.Exclusive, async (app, transaction) =>
        {
            (await CountAsync(app, transaction, Clear, LearnerMembershipA)).Should().Be(1);
            (await ScalarAsync<bool>(app, transaction, """
                SELECT display_name IS NULL AND preferred_locale IS NULL AND custom_fields='[]'::jsonb
                FROM public.membership_profiles WHERE membership_id=@member
                """, ("member", LearnerMembershipA))).Should().BeTrue();
            // A role assignment DELETE of a Revoked parent was admitted before this exception and still is.
            (await CountAsync(app, transaction, "DELETE FROM public.membership_role_assignments WHERE membership_id=@member",
                LearnerMembershipA)).Should().Be(1);
        });

        // Each condition removed alone.
        await RefusedAsync(database, SchemaFixture.TenantA, null, null, Fence.Exclusive, Clear, LearnerMembershipA, MembershipParentRefusal,
            "without the subject's User fence");
        await RefusedAsync(database, SchemaFixture.TenantA, null, PersonA, Fence.Exclusive, Clear, LearnerMembershipA, MembershipParentRefusal,
            "with the subject's User fence held shared instead of exclusively", userFenceShared: true);
        await RefusedAsync(database, SchemaFixture.TenantA, null, PersonA, Fence.Shared, Clear, LearnerMembershipA, FenceRefusal,
            "with the tenant key held shared instead of exclusively");
        await RefusedAsync(database, SchemaFixture.TenantA, null, PersonA, Fence.None, Clear, LearnerMembershipA, FenceRefusal,
            "without the tenant key");
        await RefusedAsync(database, SchemaFixture.TenantA, null, PersonA, Fence.Exclusive, Clear, LearnerMembershipA, MembershipParentRefusal,
            "once the erasure is no longer fulfilling", (app, transaction) => StepAsync(app, transaction, erasure, "status='awaiting_privileged_step'"));
        await RefusedAsync(database, SchemaFixture.TenantB, SchemaFixture.OrgB1, PersonB, Fence.Exclusive, Clear, BranchMembershipB,
            MembershipParentRefusal, "while the person's erasure of the group is only scheduled");
        await RefusedAsync(database, SchemaFixture.TenantA, SchemaFixture.OrgA1, PersonA, Fence.Exclusive, Clear, BranchMembershipA1,
            MembershipParentRefusal, "in another group of the same person, whose erasure is the tenant-wide group's");
        await RefusedAsync(database, SchemaFixture.TenantA, SchemaFixture.OrgA1, PersonA, Fence.Exclusive, Clear, BranchMembershipA1,
            MembershipParentRefusal, "with a fulfilling request of another kind for the person and group", async (app, transaction) =>
            {
                var export = Guid.CreateVersion7();
                await InsertRequestAsync(app, transaction, export, SchemaFixture.TenantA, SchemaFixture.OrgA1, PersonA, [BranchMembershipA1]);
                await StepAsync(app, transaction, export, "status='fulfilling'");
            });
        await RefusedAsync(database, SchemaFixture.TenantA, null, AdminA, Fence.Exclusive, Clear, AdminMembershipA, MembershipParentRefusal,
            "for another person of the group, whose own User fence is held but who has no erasure", (app, transaction) => ExecuteAsync(app,
                transaction, RevokeSql, ("member", AdminMembershipA), ("system", SystemActor)));

        // Only a clearing UPDATE: a value left or set, or any other column, is not one.
        await RefusedAsync(database, SchemaFixture.TenantA, null, PersonA, Fence.Exclusive,
            "UPDATE public.membership_profiles SET display_name='kept', preferred_locale=NULL, custom_fields='[]'::jsonb WHERE membership_id=@member",
            LearnerMembershipA, MembershipParentRefusal, "with a display name that is not cleared");
        await RefusedAsync(database, SchemaFixture.TenantA, null, PersonA, Fence.Exclusive,
            "UPDATE public.membership_profiles SET display_name=NULL, custom_fields='[]'::jsonb WHERE membership_id=@member",
            LearnerMembershipA, MembershipParentRefusal, "with the locale left in place");
        await RefusedAsync(database, SchemaFixture.TenantA, null, PersonA, Fence.Exclusive,
            "UPDATE public.membership_profiles SET display_name=NULL, preferred_locale=NULL, custom_fields='[{}]'::jsonb WHERE membership_id=@member",
            LearnerMembershipA, MembershipParentRefusal, "with custom values that are not the empty list");
        await RefusedAsync(database, SchemaFixture.TenantA, null, PersonA, Fence.Exclusive,
            "UPDATE public.membership_profiles SET display_name=NULL, preferred_locale=NULL, custom_fields=NULL WHERE membership_id=@member",
            LearnerMembershipA, MembershipParentRefusal, "with NULL custom values, which compare as neither equal nor different");
        await RefusedAsync(database, SchemaFixture.TenantA, null, PersonA, Fence.Exclusive, """
            UPDATE public.membership_profiles SET display_name=NULL, preferred_locale=NULL, custom_fields='[]'::jsonb,
                membership_id=@other WHERE membership_id=@member
            """, LearnerMembershipA, MembershipParentRefusal, "with another column in the same UPDATE",
            other: BranchMembershipA1);

        // Every other refusal stands: the Revoked row itself, an assignment added to it, and the profile's DELETE.
        await RefusedAsync(database, SchemaFixture.TenantA, null, PersonA, Fence.Exclusive,
            "UPDATE public.memberships SET updated_at=clock_timestamp(), row_version=row_version+1 WHERE id=@member",
            LearnerMembershipA, MembershipParentRefusal, "on the Revoked Membership's own row");
        await RefusedAsync(database, SchemaFixture.TenantA, null, PersonA, Fence.Exclusive, """
            INSERT INTO public.membership_role_assignments(tenant_id,membership_id,role_id,organization_id)
            VALUES(@tenant,@member,@role,NULL)
            """, LearnerMembershipA, MembershipParentRefusal, "for a role assignment added to the Revoked parent",
            role: RoleId(SchemaFixture.TenantA, LearnStack.SharedKernel.Permissions.BuiltInRoleKey.Learner,
                LearnStack.SharedKernel.Permissions.PermissionScope.Tenant));
        await RefusedAsync(database, SchemaFixture.TenantA, null, PersonA, Fence.Exclusive,
            "DELETE FROM public.membership_profiles WHERE membership_id=@member", LearnerMembershipA, PrivilegeRefusal,
            "for the profile's DELETE, which the application role holds no grant for");
    }

    /// <summary>
    /// The witness is bound to the request's frozen snapshot: a Revoked Membership of the same person and group that the
    /// request does not name (one admitted and revoked again after it was filed) is not cleared by it.
    /// </summary>
    [Fact]
    public async Task A_revoked_Membership_outside_the_request_s_snapshot_is_not_cleared_by_its_witness()
    {
        await using var database = await DataExportTestKit.CreateSeededDatabaseAsync(fixture.Postgres);
        var erasure = await ArrangeAsync(database);
        await CorruptSnapshotAsync(database, erasure, Guid.CreateVersion7());

        await RefusedAsync(database, SchemaFixture.TenantA, null, PersonA, Fence.Exclusive, Clear, LearnerMembershipA, MembershipParentRefusal,
            "for a Revoked Membership the fulfilling request's snapshot does not name");
    }

    /// <summary>
    /// The organization and subject predicates of the witness are each enough alone: a fulfilling tenant-wide erasure of
    /// person A whose frozen snapshot is corrupted to also name a Revoked Membership of person A in another group, or a
    /// Revoked Membership of another person of the group, still clears neither. The snapshot contains each target, so the
    /// snapshot predicate is satisfied and cannot be what refuses; the probe holds the target person's User fence and the
    /// tenant key exclusively, so no fence is what refuses either.
    /// </summary>
    [Fact]
    public async Task A_snapshot_widened_to_another_group_or_another_person_is_refused_by_the_organization_and_subject_predicates()
    {
        await using var database = await DataExportTestKit.CreateSeededDatabaseAsync(fixture.Postgres);
        var erasure = await ArrangeAsync(database);
        await CorruptSnapshotAsync(database, erasure, LearnerMembershipA, BranchMembershipA1, AdminMembershipA);
        const string named = "SELECT membership_snapshot @> jsonb_build_array(@member::text) FROM public.data_subject_requests WHERE id=@erasure";

        // Person A's other group: the erasure is the tenant-wide one, whose group is not the Membership's.
        await ProbeAsync(database, SchemaFixture.TenantA, SchemaFixture.OrgA1, PersonA, Fence.Exclusive, async (app, transaction) =>
        {
            (await ScalarAsync<bool>(app, transaction, named, ("member", BranchMembershipA1), ("erasure", erasure))).Should().BeTrue();
            (await DataExportTestKit.RefuseAsync(transaction, () => CountAsync(app, transaction, Clear, BranchMembershipA1)))
                .SqlState.Should().Be(MembershipParentRefusal, "the erasure names the Membership but is not of its group");
        });

        // Another person of the same group: the erasure is person A's, not the Membership's person's.
        await ProbeAsync(database, SchemaFixture.TenantA, null, AdminA, Fence.Exclusive, async (app, transaction) =>
        {
            // Revoked inside the rolled-back probe: committing it would strand the tenant without an administrator.
            await ExecuteAsync(app, transaction, RevokeSql, ("member", AdminMembershipA), ("system", SystemActor));
            (await ScalarAsync<bool>(app, transaction, named, ("member", AdminMembershipA), ("erasure", erasure))).Should().BeTrue();
            (await DataExportTestKit.RefuseAsync(transaction, () => CountAsync(app, transaction, Clear, AdminMembershipA)))
                .SqlState.Should().Be(MembershipParentRefusal, "the erasure names the Membership but is not of its person");
        });

        // The control: the Membership of the erasure's own person and group is still admitted from the same arrangement.
        await ProbeAsync(database, SchemaFixture.TenantA, null, PersonA, Fence.Exclusive, async (app, transaction) =>
            (await CountAsync(app, transaction, Clear, LearnerMembershipA)).Should().Be(1));
    }

    /// <summary>Replaces a request's frozen snapshot as the table owner with the request guard paused, in one committed transaction.</summary>
    private static async Task CorruptSnapshotAsync(DisposableSchemaDatabase database, Guid erasure, params Guid[] memberships)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        await ExecuteAsync(owner, transaction, "ALTER TABLE public.data_subject_requests DISABLE TRIGGER tg_data_subject_requests_mutation_guard");
        await ScopeAsync(owner, transaction, SchemaFixture.TenantA, null);
        await ExecuteAsync(owner, transaction, "UPDATE public.data_subject_requests SET membership_snapshot=@snapshot::jsonb WHERE id=@id",
            ("id", erasure), ("snapshot", Snapshot(memberships)));
        await ExecuteAsync(owner, transaction, "ALTER TABLE public.data_subject_requests ENABLE TRIGGER tg_data_subject_requests_mutation_guard");
        await transaction.CommitAsync();
    }

    private enum Fence { None, Shared, Exclusive }

    private const string RevokeSql = """
        UPDATE public.memberships SET status='Revoked', deleted_at=clock_timestamp(), deleted_by=@system,
            updated_at=clock_timestamp(), updated_by=@system, row_version=row_version+1
        WHERE id=@member
        """;

    /// <summary>The three Revoked Memberships and the two erasures the probes read, committed by the application role.</summary>
    private static async Task<Guid> ArrangeAsync(DisposableSchemaDatabase database)
    {
        var erasure = Guid.CreateVersion7();
        await using var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString);
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await ScopeAsync(app, transaction, SchemaFixture.TenantA, null);
            await LockAsync(app, transaction, SchemaFixture.TenantA, PersonA, false);
            await ExecuteAsync(app, transaction, RevokeSql, ("member", LearnerMembershipA), ("system", SystemActor));
            await InsertRequestAsync(app, transaction, erasure, SchemaFixture.TenantA, null, PersonA, [LearnerMembershipA], "erasure");
            await transaction.CommitAsync();
        }

        await using (var transaction = await app.BeginTransactionAsync())
        {
            await ScopeAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
            await LockAsync(app, transaction, SchemaFixture.TenantA, PersonA, false);
            await ExecuteAsync(app, transaction, RevokeSql, ("member", BranchMembershipA1), ("system", SystemActor));
            await transaction.CommitAsync();
        }

        await using (var transaction = await app.BeginTransactionAsync())
        {
            await FenceAsync(app, transaction, SchemaFixture.TenantB, SchemaFixture.OrgB1, PersonB);
            await ExecuteAsync(app, transaction, RevokeSql, ("member", BranchMembershipB), ("system", SystemActor));
            await InsertRequestAsync(app, transaction, Guid.CreateVersion7(), SchemaFixture.TenantB, SchemaFixture.OrgB1, PersonB,
                [BranchMembershipB], "erasure");
            await transaction.CommitAsync();
        }

        await DataErasureTestKit.BackdateAsync(database.MigrationConnectionString, SchemaFixture.TenantA, null, erasure);
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await FenceAsync(app, transaction, SchemaFixture.TenantA, null, PersonA);
            await StepAsync(app, transaction, erasure, "status='fulfilling'");
            await transaction.CommitAsync();
        }

        return erasure;
    }

    private static async Task RefusedAsync(DisposableSchemaDatabase database, Guid tenant, Guid? organization, Guid? user, Fence fence,
        string sql, Guid member, string sqlState, string because, Func<NpgsqlConnection, NpgsqlTransaction, Task>? arrange = null,
        Guid? other = null, Guid? role = null, bool userFenceShared = false) =>
        await ProbeAsync(database, tenant, organization, user, fence, async (app, transaction) =>
        {
            if (arrange is not null) await arrange(app, transaction);
            var refusal = await DataExportTestKit.RefuseAsync(transaction, () => CountAsync(app, transaction, sql, member, tenant, other, role));
            refusal.SqlState.Should().Be(sqlState, because);
        }, userFenceShared);

    /// <summary>One rolled-back application-role transaction announced for the group, with the fences asked for.</summary>
    private static async Task ProbeAsync(DisposableSchemaDatabase database, Guid tenant, Guid? organization, Guid? user, Fence fence,
        Func<NpgsqlConnection, NpgsqlTransaction, Task> probe, bool userFenceShared = false)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant, organization);
        if (user is { } person)
            await ExecuteAsync(app, transaction, userFenceShared ? "SELECT pg_advisory_xact_lock_shared(@key)" : "SELECT pg_advisory_xact_lock(@key)",
                ("key", LearnStack.SharedKernel.Identity.IdentityAdvisoryLockKey.ForUser(LearnStack.SharedKernel.Identifiers.UserId.From(person))));
        if (fence != Fence.None)
            await ExecuteAsync(app, transaction, fence == Fence.Exclusive ? "SELECT pg_advisory_xact_lock(@key)" : "SELECT pg_advisory_xact_lock_shared(@key)",
                ("key", LearnStack.SharedKernel.Identity.IdentityAdvisoryLockKey.ForTenant(LearnStack.SharedKernel.Identifiers.TenantId.From(tenant))));
        await probe(app, transaction);
        await transaction.RollbackAsync();
    }

    private static async Task<int> CountAsync(NpgsqlConnection app, NpgsqlTransaction transaction, string sql, Guid member,
        Guid? tenant = null, Guid? other = null, Guid? role = null)
    {
        await using var command = new NpgsqlCommand(sql, app, transaction);
        command.Parameters.AddWithValue("member", member);
        if (tenant is { } value) command.Parameters.AddWithValue("tenant", value);
        if (other is { } replacement) command.Parameters.AddWithValue("other", replacement);
        if (role is { } assigned) command.Parameters.AddWithValue("role", assigned);
        return await command.ExecuteNonQueryAsync();
    }
}
