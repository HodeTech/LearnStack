using FluentAssertions;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class MembershipFieldConsentStorageTests(SchemaFixture schema)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task App_append_refuses_missing_or_only_shared_Tenant_fences(bool holdShared)
    {
        await using var app = new NpgsqlConnection(schema.Postgres.AppConnectionString);
        await app.OpenAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var tx = await app.BeginTransactionAsync();
        await ScopeAsync(app, tx, SchemaFixture.TenantA, null);
        if (holdShared)
            await ExecuteAsync(app, tx, "SELECT pg_advisory_xact_lock_shared(public.fn_identity_contextual_lock_key('tenant',@tenant))",
                ("tenant", SchemaFixture.TenantA));
        var actor = Guid.CreateVersion7();
        await RefusedAsync(app, tx, "P0301", () => InsertAsync(app, tx, Guid.CreateVersion7(), SchemaFixture.TenantA,
            null, Guid.CreateVersion7(), actor, actor, Guid.CreateVersion7(), 1, "Granted", null, 0));
    }

    [Theory]
    [InlineData("UPDATE public.membership_field_consents SET notice_locale='tr'")]
    [InlineData("DELETE FROM public.membership_field_consents")]
    [InlineData("TRUNCATE public.membership_field_consents")]
    public async Task The_table_owner_cannot_rewrite_or_remove_nonempty_consent_history(string statement)
    {
        await using var owner = new NpgsqlConnection(schema.Postgres.MigrationConnectionString);
        await owner.OpenAsync();
        await using var tx = await owner.BeginTransactionAsync();
        var before = await ScalarAsync<long>(owner, tx, "SELECT count(*) FROM public.membership_field_consents");
        before.Should().BeGreaterThan(0, "an immutable-history assertion over an empty fixture is not evidence");
        await RefusedAsync(owner, tx, "P0320", () => ExecuteAsync(owner, tx, statement));
        (await ScalarAsync<long>(owner, tx, "SELECT count(*) FROM public.membership_field_consents")).Should().Be(before);
    }

    [Fact]
    public async Task App_role_transitions_are_fenced_immutable_and_cooldown_uses_database_time()
    {
        await using var app = new NpgsqlConnection(schema.Postgres.AppConnectionString);
        await app.OpenAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var tx = await app.BeginTransactionAsync();
        await ScopeAsync(app, tx, SchemaFixture.TenantA, null);
        await FenceAsync(app, tx, SchemaFixture.TenantA);
        var actor = Guid.CreateVersion7();
        var member = Guid.CreateVersion7();
        var definition = Guid.CreateVersion7();
        var grant = Guid.CreateVersion7();
        var withdrawal = Guid.CreateVersion7();
        await InsertAsync(app, tx, grant, SchemaFixture.TenantA, null, member, actor, actor, definition, 1, "Granted", null, 0);
        await InsertAsync(app, tx, withdrawal, SchemaFixture.TenantA, null, member, actor, actor, definition, 2, "Withdrawn", grant, 1);
        await RefusedAsync(app, tx, "P0323", () => InsertAsync(app, tx, Guid.CreateVersion7(), SchemaFixture.TenantA,
            null, member, actor, actor, definition, 3, "Granted", withdrawal, 2));
        await RefusedAsync(app, tx, "P0322", () => InsertAsync(app, tx, Guid.CreateVersion7(), SchemaFixture.TenantA,
            null, member, actor, actor, definition, 3, "Withdrawn", withdrawal, 2));
        await RefusedAsync(app, tx, "P0322", () => InsertAsync(app, tx, Guid.CreateVersion7(), SchemaFixture.TenantA,
            null, member, actor, actor, definition, 3, "Granted", grant, 2));
        await RefusedAsync(app, tx, "P0321", () => InsertAsync(app, tx, Guid.CreateVersion7(), SchemaFixture.TenantA,
            null, member, actor, Guid.CreateVersion7(), definition, 3, "Granted", withdrawal, 2));
        await RefusedAsync(app, tx, "42501", () => ExecuteAsync(app, tx, "UPDATE public.membership_field_consents SET notice_locale='tr' WHERE id=@id", ("id", grant)));
        await RefusedAsync(app, tx, "42501", () => ExecuteAsync(app, tx, "DELETE FROM public.membership_field_consents WHERE id=@id", ("id", grant)));
        await RefusedAsync(app, tx, "42501", () => ExecuteAsync(app, tx, "TRUNCATE public.membership_field_consents"));
        // Every insert above supplies a timestamp one day old; the trigger must replace it.
        (await ScalarAsync<bool>(app, tx, "SELECT created_at > clock_timestamp() - interval '1 minute' FROM public.membership_field_consents WHERE id=@id", ("id", grant))).Should().BeTrue();
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Definer_scope_is_subject_and_host_capped_with_a_closed_bounded_interface()
    {
        await using var app = new NpgsqlConnection(schema.Postgres.AppConnectionString);
        await app.OpenAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var tx = await app.BeginTransactionAsync();
        var actor = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var root = Guid.CreateVersion7();
        var a1 = Guid.CreateVersion7();
        var a2 = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        var foreignSubject = Guid.CreateVersion7();
        foreach (var item in new[] { (SchemaFixture.TenantA, (Guid?)null, root, actor),
            (SchemaFixture.TenantA, (Guid?)SchemaFixture.OrgA1, a1, actor),
            (SchemaFixture.TenantA, (Guid?)SchemaFixture.OrgA2, a2, actor),
            (SchemaFixture.TenantB, (Guid?)null, b, actor),
            (SchemaFixture.TenantA, (Guid?)SchemaFixture.OrgA1, foreignSubject, other) })
        {
            await ScopeAsync(app, tx, item.Item1, item.Item2);
            await FenceAsync(app, tx, item.Item1);
            await InsertAsync(app, tx, Guid.CreateVersion7(), item.Item1, item.Item2, item.Item3, item.Item4,
                item.Item4, Guid.CreateVersion7(), 1, "Granted", null, 0);
        }
        var allMembers = new[] { root, a1, a2, b, foreignSubject };
        await ScopeAsync(app, tx, SchemaFixture.TenantA, SchemaFixture.OrgA1);
        (await ScalarAsync<long>(app, tx, "SELECT count(*) FROM public.membership_field_consents WHERE membership_id = ANY(@members)",
            ("members", allMembers))).Should().Be(3);
        await ScopeAsync(app, tx, SchemaFixture.TenantA, SchemaFixture.OrgA2);
        (await ScalarAsync<long>(app, tx, "SELECT count(*) FROM public.membership_field_consents WHERE membership_id = ANY(@members)",
            ("members", allMembers))).Should().Be(2);
        await ScopeAsync(app, tx, SchemaFixture.TenantB, null);
        (await ScalarAsync<long>(app, tx, "SELECT count(*) FROM public.membership_field_consents WHERE membership_id = ANY(@members)",
            ("members", allMembers))).Should().Be(1);
        (await ScopeMembersAsync(app, tx, actor, SchemaFixture.TenantA, null, "list", null, null, 20))
            .Should().BeEquivalentTo([root, a1, a2]);
        (await ScopeMembersAsync(app, tx, actor, SchemaFixture.TenantA, SchemaFixture.OrgA1, "list", null, null, 20))
            .Should().Equal(a1);
        (await ScopeMembersAsync(app, tx, actor, SchemaFixture.TenantA, SchemaFixture.OrgA1, "select", a2, null, 1))
            .Should().BeEmpty();
        (await ScopeMembersAsync(app, tx, actor, SchemaFixture.TenantB, null, "select", b, null, 1))
            .Should().Equal(b);
        var ordered = new[] { root, a1, a2 }.Order().ToArray();
        (await ScopeMembersAsync(app, tx, actor, SchemaFixture.TenantA, null, "list", null, ordered[0], 20))
            .Should().Equal(ordered.Skip(1));
        await RefusedAsync(app, tx, "P0324", () => ScopeMembersAsync(app, tx, actor, SchemaFixture.TenantA, null, "bad", null, null, 20));
        await RefusedAsync(app, tx, "P0324", () => ScopeMembersAsync(app, tx, actor, SchemaFixture.TenantA, null, "list", null, null, 21));
        await RefusedAsync(app, tx, "P0324", () => ScopeMembersAsync(app, tx, Guid.Empty, SchemaFixture.TenantA, null, "list", null, null, 20));
        await RefusedAsync(app, tx, "P0324", () => ScopeMembersAsync(app, tx, UserId.SystemActor.Value, SchemaFixture.TenantA, null, "list", null, null, 20));
        await RefusedAsync(app, tx, "P0324", () => ScopeMembersAsync(app, tx, actor, TenantId.PlatformSentinel.Value, null, "list", null, null, 20));
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Batch_reader_returns_latest_exact_tuple_in_input_order_without_history()
    {
        await using var app = new NpgsqlConnection(schema.Postgres.AppConnectionString);
        await app.OpenAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var tx = await app.BeginTransactionAsync();
        await ScopeAsync(app, tx, SchemaFixture.TenantA, null);
        await FenceAsync(app, tx, SchemaFixture.TenantA);
        var actor = Guid.CreateVersion7();
        var member = Guid.CreateVersion7();
        var definition = Guid.CreateVersion7();
        var secondDefinition = Guid.CreateVersion7();
        var grant = Guid.CreateVersion7();
        var withdrawal = Guid.CreateVersion7();
        var secondGrant = Guid.CreateVersion7();
        await InsertAsync(app, tx, grant, SchemaFixture.TenantA, null, member, actor, actor, definition, 1, "Granted", null, 0);
        await InsertAsync(app, tx, withdrawal, SchemaFixture.TenantA, null, member, actor, actor, definition, 2, "Withdrawn", grant, 1);
        await InsertAsync(app, tx, secondGrant, SchemaFixture.TenantA, null, member, actor, actor, secondDefinition, 1, "Granted", null, 0);
        var scope = new StaticTenantContextAccessor(new SeedTenantContext(TenantId.From(SchemaFixture.TenantA), null));
        await using var db = new TenancyDbContext(new DbContextOptionsBuilder<TenancyDbContext>().UseNpgsql(app).Options, scope);
        await db.Database.UseTransactionAsync(tx);
        var reader = new MembershipFieldConsentReader(db);
        var first = new MembershipFieldConsentTuple(member, UserId.From(actor), definition, 1, "test-purpose");
        var second = new MembershipFieldConsentTuple(member, UserId.From(actor), secondDefinition, 1, "test-purpose");
        var missing = new MembershipFieldConsentTuple(member, UserId.From(actor), Guid.CreateVersion7(), 1, "test-purpose");
        var rows = await reader.ReadCurrentBatchAsync([second, missing, first]);
        rows[0]!.EvidenceId.Should().Be(secondGrant);
        rows[0]!.IsGranted.Should().BeTrue();
        rows[1].Should().BeNull();
        rows[2]!.EvidenceId.Should().Be(withdrawal);
        rows[2]!.Sequence.Should().Be(2);
        rows[2]!.IsGranted.Should().BeFalse();
        await FluentActions.Awaiting(() => reader.ReadCurrentBatchAsync([first, first])).Should().ThrowAsync<ArgumentException>();
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Scope_function_has_exact_execution_grants_and_migration_owner()
    {
        await using var app = new NpgsqlConnection(schema.Postgres.AppConnectionString);
        await app.OpenAsync();
        const string sql = """
            SELECT r.rolname, has_function_privilege('learnstack_app', p.oid, 'EXECUTE'),
              EXISTS (SELECT 1 FROM aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a
                WHERE a.grantee=0 AND a.privilege_type='EXECUTE'),
              has_function_privilege('learnstack_platform', p.oid, 'EXECUTE'),
              has_function_privilege('learnstack_outbox_admin', p.oid, 'EXECUTE'), p.provolatile::text
            FROM pg_proc p JOIN pg_roles r ON r.oid=p.proowner
            WHERE p.oid='public.fn_tenancy_own_field_consent_scope(uuid,uuid,uuid,text,uuid,uuid,integer)'::regprocedure
            """;
        await using var cmd = new NpgsqlCommand(sql, app);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("learnstack_migration");
        reader.GetBoolean(1).Should().BeTrue();
        reader.GetBoolean(2).Should().BeFalse();
        reader.GetBoolean(3).Should().BeFalse();
        reader.GetBoolean(4).Should().BeFalse();
        reader.GetString(5).Should().Be("v");
    }

    private static async Task ScopeAsync(NpgsqlConnection app, NpgsqlTransaction tx, Guid tenant, Guid? organization)
    {
        await SchemaQueries.SetSettingAsync(app, tx, "app.tenant_id", tenant.ToString());
        await SchemaQueries.SetSettingAsync(app, tx, "app.organization_id", organization?.ToString() ?? string.Empty);
        await SchemaQueries.SetSettingAsync(app, tx, "app.scope", string.Empty);
    }

    private static Task FenceAsync(NpgsqlConnection app, NpgsqlTransaction tx, Guid tenant) =>
        ExecuteAsync(app, tx, "SELECT pg_advisory_xact_lock(@key)",
            ("key", IdentityAdvisoryLockKey.ForTenant(TenantId.From(tenant))));

    private static Task InsertAsync(NpgsqlConnection app, NpgsqlTransaction tx, Guid id, Guid tenant, Guid? org,
        Guid member, Guid subject, Guid actor, Guid definition, long sequence, string state, Guid? predecessor, long previousSequence) =>
        ExecuteAsync(app, tx, """
            INSERT INTO public.membership_field_consents
              (id,tenant_id,organization_id,membership_id,subject_user_id,actor_user_id,definition_id,definition_version,
               purpose_key,notice_locale,notice_hash,sequence,state,predecessor_id,predecessor_sequence,created_at,created_by,row_version)
            VALUES (@id,@tenant,@org,@member,@subject,@actor,@definition,1,'test-purpose','en',
              'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',@sequence,@state,@predecessor,@previous,
              clock_timestamp() - interval '1 day',@actor,0)
            """, ("id", id), ("tenant", tenant), ("org", org), ("member", member), ("subject", subject),
            ("actor", actor), ("definition", definition), ("sequence", sequence), ("state", state),
            ("predecessor", predecessor), ("previous", previousSequence));

    private static async Task<Guid[]> ScopeMembersAsync(NpgsqlConnection app, NpgsqlTransaction tx,
        Guid actor, Guid tenant, Guid? organization, string mode, Guid? membership, Guid? after, int limit)
    {
        await using var cmd = Command(app, tx, """
            SELECT membership_id FROM public.fn_tenancy_own_field_consent_scope(@actor,@tenant,@org,@mode,@member,@after,@limit)
            """, ("actor", actor), ("tenant", tenant), ("org", organization), ("mode", mode),
            ("member", membership), ("after", after), ("limit", limit));
        var result = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetGuid(0));
        return [.. result];
    }

    private static async Task RefusedAsync(NpgsqlConnection app, NpgsqlTransaction tx, string state, Func<Task> action)
    {
        await ExecuteAsync(app, tx, "SAVEPOINT consent_refusal");
        (await FluentActions.Awaiting(action).Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(state);
        await ExecuteAsync(app, tx, "ROLLBACK TO SAVEPOINT consent_refusal");
        await ExecuteAsync(app, tx, "RELEASE SAVEPOINT consent_refusal");
    }

    private static async Task ExecuteAsync(NpgsqlConnection app, NpgsqlTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        await using var command = Command(app, tx, sql, args);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection app, NpgsqlTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        await using var command = Command(app, tx, sql, args);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static NpgsqlCommand Command(NpgsqlConnection app, NpgsqlTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = new NpgsqlCommand(sql, app, tx) { CommandTimeout = 10 };
        foreach (var (name, value) in args)
        {
            if (value is null) cmd.Parameters.AddWithValue(name, NpgsqlDbType.Uuid, DBNull.Value);
            else cmd.Parameters.AddWithValue(name, value);
        }
        return cmd;
    }
}
