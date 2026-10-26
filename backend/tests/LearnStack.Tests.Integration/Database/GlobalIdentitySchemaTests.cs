using System.Text.Json;
using FluentAssertions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Actual app-role isolation and the three installed global witness contracts.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed partial class GlobalIdentitySchemaTests(GlobalIdentitySchemaFixture fixture)
    : IClassFixture<GlobalIdentitySchemaFixture>
{
    [Fact]
    public async Task Global_Identity_Storage_Is_Subject_Confined()
    {
        var first = NewSubject();
        var second = NewSubject();
        await InsertPairAsync(first);
        await InsertPairAsync(second);
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await AnnounceAsync(connection, transaction, first);
        (await ScalarAsync<long>(connection, transaction, "SELECT count(*) FROM public.users")).Should().Be(1);

        var accessor = new TestScope { Current = new(1, TestSource.Lookup(first)) };
        await using var context = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connection).Options, StaticTenantContextAccessor.Unresolved, accessor);
        await context.Database.UseTransactionAsync(transaction);
        (await context.Users.AsNoTracking().SingleAsync()).ProviderSubject.Should().Be(first.Subject);
        await AnnounceAsync(connection, transaction, second);
        (await context.Users.AsNoTracking().CountAsync()).Should().Be(0,
            "the independently parameterized EF filter still asks for the first subject");
        accessor.Current = new(2, TestSource.Lookup(second));
        (await context.Users.AsNoTracking().SingleAsync()).ProviderSubject.Should().Be(second.Subject);
        accessor.Current = null;
        (await context.Users.AsNoTracking().CountAsync()).Should().Be(0);
        await ClearAsync(connection, transaction);
        (await ScalarAsync<long>(connection, transaction, "SELECT count(*) FROM public.users")).Should().Be(0);
        await ExecuteAsync(connection, transaction, """
            SELECT set_config('app.identity_authority_id','not-a-uuid',true),
                set_config('app.identity_subject_key','not-a-digest',true)
            """);
        (await ScalarAsync<long>(connection, transaction, "SELECT count(*) FROM public.users")).Should().Be(0,
            "malformed identity settings refuse visibility without parsing caller-controlled values");
        await transaction.RollbackAsync();
        await using var reused = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.AppConnectionString);
        (await ScalarAsync<long>(reused, null, "SELECT count(*) FROM public.users")).Should().Be(0);
    }

    [Fact]
    public async Task Applied_census_has_fifty_five_tables_and_exactly_five_Identity_definers()
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.MigrationConnectionString);
        await SchemaTableClasses.AssertCompleteAsync(owner);
        (await SchemaTableClasses.ReadAsync(owner)).Should().HaveCount(55);
        var names = await SchemaQueries.ReadStringsAsync(owner, """
            SELECT p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname='public' AND p.prosecdef ORDER BY p.proname
            """, null);
        names.Should().Equal("fn_identity_admin_admission_state", "fn_identity_audit_creation_guard", "fn_identity_audit_evidence",
            "fn_identity_membership_admission_guard", "fn_identity_user_creation_audit_guard");
        (await ScalarAsync<long>(owner, null, """
            SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            JOIN pg_roles r ON r.oid=p.proowner
            WHERE n.nspname='public' AND p.prosecdef
              AND r.rolname='learnstack_migration' AND NOT r.rolbypassrls
              AND 'search_path=pg_catalog'=ANY(p.proconfig)
              AND NOT EXISTS (SELECT 1 FROM aclexplode(p.proacl) acl WHERE acl.grantee=0)
            """)).Should().Be(5);
        (await ScalarAsync<long>(owner, null, """
            SELECT count(*) FROM pg_class WHERE oid IN ('public.users'::regclass,'public.identity_authority'::regclass)
              AND relrowsecurity AND relforcerowsecurity
            """)).Should().Be(2);
    }

    [Fact]
    public async Task Global_grants_expose_only_self_select_initial_insert_and_exact_evidence()
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.AppConnectionString);
        (await ScalarAsync<bool>(app, null, "SELECT has_table_privilege(current_user,'public.users','SELECT')")).Should().BeTrue();
        foreach (var privilege in new[] { "UPDATE", "DELETE", "TRUNCATE" })
            (await ScalarAsync<bool>(app, null, $"SELECT has_table_privilege(current_user,'public.users','{privilege}')")).Should().BeFalse();
        foreach (var column in new[] { "updated_at", "updated_by", "deleted_at", "deleted_by" })
            (await ScalarAsync<bool>(app, null, $"SELECT has_column_privilege(current_user,'public.users','{column}','INSERT')")).Should().BeFalse();
        (await ScalarAsync<long>(app, null, "SELECT count(*) FROM public.identity_authority")).Should().Be(1);
        foreach (var privilege in new[] { "INSERT", "UPDATE", "DELETE", "TRUNCATE" })
            (await ScalarAsync<bool>(app, null, $"SELECT has_table_privilege(current_user,'public.identity_authority','{privilege}')")).Should().BeFalse();
        (await ScalarAsync<bool>(app, null, "SELECT has_function_privilege(current_user,'public.fn_identity_audit_evidence(uuid,timestamptz,uuid,bytea,uuid,text,text)','EXECUTE')")).Should().BeTrue();
        foreach (var function in new[] { "fn_identity_user_creation_audit_guard", "fn_identity_audit_creation_guard" })
            (await ScalarAsync<bool>(app, null, $"SELECT has_function_privilege(current_user,'public.{function}()','EXECUTE')")).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reciprocal_creation_requires_both_exact_success_witnesses(bool auditOnly)
    {
        var subject = NewSubject();
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await AnnounceAsync(app, transaction, subject);
        var target = Guid.CreateVersion7();
        var audit = Guid.CreateVersion7();
        var at = GlobalIdentityCreationReference.NormalizeTimestamp(DateTimeOffset.UtcNow);
        if (auditOnly) await InsertAuditAsync(app, transaction, subject, target, audit, at, "success");
        else await InsertUserAsync(app, transaction, subject, target, audit, at);
        var preflight = () => ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        (await preflight.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
        await transaction.RollbackAsync();
        await AnnounceInFreshReadAsync(subject, 0);
    }

    [Fact]
    public async Task Evidence_requires_exact_key_subject_target_source_outcome_and_success_relation()
    {
        var subject = NewSubject();
        var pair = await InsertPairAsync(subject);
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await AnnounceAsync(app, transaction, subject);
        (await EvidenceAsync(app, transaction, subject, pair.Target, pair.Audit, pair.At, "identity-bootstrap", "success")).Should().BeTrue();
        (await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.audit_log WHERE operation='identity.user.create'")).Should().Be(0,
            "global self identity does not grant direct global audit visibility");
        (await EvidenceAsync(app, transaction, subject, pair.Target, pair.Audit, pair.At.AddTicks(10), "identity-bootstrap", "success")).Should().BeFalse();
        (await EvidenceAsync(app, transaction, subject, Guid.CreateVersion7(), pair.Audit, pair.At, "identity-bootstrap", "success")).Should().BeFalse();
        (await EvidenceAsync(app, transaction, subject, pair.Target, pair.Audit, pair.At, "identity-seed-adoption", "success")).Should().BeFalse();
        (await EvidenceAsync(app, transaction, subject, pair.Target, pair.Audit, pair.At, "identity-bootstrap", "failed")).Should().BeFalse();
        await AnnounceAsync(app, transaction, NewSubject());
        (await EvidenceAsync(app, transaction, subject, pair.Target, pair.Audit, pair.At, "identity-bootstrap", "success")).Should().BeFalse();
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("indeterminate")]
    public async Task Non_success_evidence_needs_no_User_and_cannot_satisfy_success(string outcome)
    {
        var subject = NewSubject();
        var target = Guid.CreateVersion7();
        var audit = Guid.CreateVersion7();
        var at = GlobalIdentityCreationReference.NormalizeTimestamp(DateTimeOffset.UtcNow);
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.AppConnectionString);
        await using (var write = await app.BeginTransactionAsync())
        {
            await AnnounceAsync(app, write, subject);
            await InsertAuditAsync(app, write, subject, target, audit, at, outcome);
            await write.CommitAsync();
        }
        await using var read = await app.BeginTransactionAsync();
        await AnnounceAsync(app, read, subject);
        (await EvidenceAsync(app, read, subject, target, audit, at, "identity-bootstrap", outcome)).Should().BeTrue();
        (await EvidenceAsync(app, read, subject, target, audit, at, "identity-bootstrap", "success")).Should().BeFalse();
    }

    [Theory]
    [InlineData("DELETE FROM public.users")]
    [InlineData("TRUNCATE public.users, public.memberships, public.membership_profiles, public.membership_role_assignments")]
    [InlineData("UPDATE public.users SET status='Closing'")]
    public async Task Owner_execution_cannot_remove_or_mutate_the_permanent_binding(string sql)
    {
        await InsertPairAsync(NewSubject());
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        var mutate = () => ExecuteAsync(owner, transaction, sql);
        (await mutate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    [Fact]
    public async Task Database_digest_matches_the_network_order_unicode_subject_encoding()
    {
        var subject = NewSubject("İ-opaque-\U0001f512-");
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.MigrationConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT encode(sha256(int4send(octet_length(convert_to('learnstack.identity.subject.v1','UTF8')))
                ||convert_to('learnstack.identity.subject.v1','UTF8')||int4send(16)||uuid_send(@authority)
                ||int4send(octet_length(convert_to(@subject,'UTF8')))||convert_to(@subject,'UTF8')),'hex')
            """, owner);
        command.Parameters.AddWithValue("authority", subject.AuthorityId.Value);
        command.Parameters.AddWithValue("subject", subject.Subject);
        ((string)(await command.ExecuteScalarAsync())!).Should().Be(subject.SubjectKey.Hex);
    }

    [Fact]
    public async Task Applied_subject_lifecycle_CHECK_allows_nullable_Closing_and_bounds_retained_subjects()
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.MigrationConnectionString);
        await using var read = new NpgsqlCommand("""
            SELECT pg_get_expr(conbin,conrelid) FROM pg_constraint
            WHERE conrelid='public.users'::regclass AND conname='ck_users_subject_lifecycle'
            """, owner);
        var expression = (string)(await read.ExecuteScalarAsync())!;
        expression.Should().NotBeNullOrWhiteSpace();
        var cases = new (string Status, string? Subject, bool Accepted)[]
        {
            ("Active", null, false), ("Active", "", false), ("Active", "opaque", true),
            ("Active", new string('x', 255), true), ("Active", new string('x', 256), false),
            ("Closing", null, true), ("Closing", "", false), ("Closing", "opaque", true),
            ("Closing", new string('x', 255), true), ("Closing", new string('x', 256), false),
            ("Closed", null, true), ("Closed", "", false), ("Closed", "opaque", false),
            ("Closed", new string('x', 255), false), ("Closed", new string('x', 256), false),
        };
        foreach (var (status, subject, accepted) in cases)
        {
            // Evaluate the applied CHECK over parameterized values using PostgreSQL's
            // CHECK semantics (NULL passes), without invoking a lifecycle writer.
            await using var evaluate = new NpgsqlCommand($"""
                SELECT ({expression}) IS NOT FALSE
                FROM (VALUES (@status::text,@subject::text)) AS candidate(status,provider_subject)
                """, owner);
            evaluate.Parameters.AddWithValue("status", status);
            evaluate.Parameters.AddWithValue("subject", NpgsqlTypes.NpgsqlDbType.Text, (object?)subject ?? DBNull.Value);
            ((bool)(await evaluate.ExecuteScalarAsync())!).Should().Be(accepted,
                $"{status} with subject length {subject?.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL"} must follow its retained-subject contract");
        }
    }

    private static GlobalIdentitySubject NewSubject(string prefix = "opaque-") =>
        new(GlobalIdentitySchemaFixture.Authority, GlobalIdentitySchemaFixture.Issuer, prefix + Guid.NewGuid().ToString("N"));

    private Task<(Guid Target, Guid Audit, DateTimeOffset At)> InsertPairAsync(GlobalIdentitySubject subject) =>
        InsertPairAsync(fixture.Postgres.AppConnectionString, subject);

    private static async Task<(Guid Target, Guid Audit, DateTimeOffset At)> InsertPairAsync(
        string connectionString, GlobalIdentitySubject subject)
    {
        var target = Guid.CreateVersion7();
        var audit = Guid.CreateVersion7();
        var at = GlobalIdentityCreationReference.NormalizeTimestamp(DateTimeOffset.UtcNow);
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await AnnounceAsync(app, transaction, subject);
        await InsertUserAsync(app, transaction, subject, target, audit, at);
        await InsertAuditAsync(app, transaction, subject, target, audit, at, "success");
        await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        await transaction.CommitAsync();
        return (target, audit, at);
    }

    private async Task AnnounceInFreshReadAsync(GlobalIdentitySubject subject, long expected)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await AnnounceAsync(app, transaction, subject);
        (await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.users")).Should().Be(expected);
    }

    private static async Task InsertUserAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        GlobalIdentitySubject subject, Guid target, Guid audit, DateTimeOffset at)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO public.users(id,authority_id,subject_key,provider_subject,status,creation_audit_id,
                creation_audit_timestamp,created_at,created_by,row_version)
            VALUES(@target,@authority,@key,@subject,'Active',@audit,@at,@at,@actor,0)
            """, connection, transaction);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("authority", subject.AuthorityId.Value);
        command.Parameters.AddWithValue("key", subject.SubjectKey.ToByteArray());
        command.Parameters.AddWithValue("subject", subject.Subject);
        command.Parameters.AddWithValue("audit", audit);
        command.Parameters.AddWithValue("at", at);
        command.Parameters.AddWithValue("actor", UserId.SystemActor.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        GlobalIdentitySubject subject, Guid target, Guid audit, DateTimeOffset at, string outcome)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO public.audit_log(id,timestamp,tenant_id,organization_id,actor_user_id,module,operation,
                operation_type,operation_class,entity_type,entity_id,outcome,metadata)
            VALUES(@audit,@at,@sentinel,NULL,NULL,'identity','identity.user.create','SecurityEvent','Must','User',@target,@outcome,@metadata::jsonb)
            """, connection, transaction);
        command.Parameters.AddWithValue("audit", audit);
        command.Parameters.AddWithValue("at", at);
        command.Parameters.AddWithValue("sentinel", TenantId.PlatformSentinel.Value);
        command.Parameters.AddWithValue("target", target.ToString("D"));
        command.Parameters.AddWithValue("outcome", outcome);
        command.Parameters.AddWithValue("metadata", JsonSerializer.Serialize(new
        {
            identity_authority_id = subject.AuthorityId.Value.ToString("D"),
            identity_subject_key = subject.SubjectKey.Hex,
            identity_source = "identity-bootstrap",
        }));
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task AnnounceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        GlobalIdentitySubject subject)
    {
        await ClearAsync(connection, transaction);
        await using var command = new NpgsqlCommand("""
            SELECT set_config('app.identity_authority_id',@authority,true),set_config('app.identity_subject_key',@key,true)
            """, connection, transaction);
        command.Parameters.AddWithValue("authority", subject.AuthorityId.Value.ToString("D"));
        command.Parameters.AddWithValue("key", subject.SubjectKey.Hex);
        await command.ExecuteNonQueryAsync();
    }

    private static Task ClearAsync(NpgsqlConnection connection, NpgsqlTransaction transaction) => ExecuteAsync(connection, transaction, """
        SELECT set_config('app.tenant_id','',true),set_config('app.organization_id','',true),set_config('app.scope','',true),
          set_config('app.resolving_host','',true),set_config('app.identity_authority_id','',true),
          set_config('app.identity_subject_key','',true)
        """);

    private static async Task<bool> EvidenceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        GlobalIdentitySubject subject, Guid target, Guid audit, DateTimeOffset at, string source, string outcome)
    {
        await using var command = new NpgsqlCommand("SELECT public.fn_identity_audit_evidence(@audit,@at,@authority,@key,@target,@source,@outcome)", connection, transaction);
        command.Parameters.AddWithValue("audit", audit);
        command.Parameters.AddWithValue("at", at);
        command.Parameters.AddWithValue("authority", subject.AuthorityId.Value);
        command.Parameters.AddWithValue("key", subject.SubjectKey.ToByteArray());
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("source", source);
        command.Parameters.AddWithValue("outcome", outcome);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private sealed class TestScope : IGlobalIdentityScopeAccessor
    {
        public GlobalIdentityContextAnnouncement? Current { get; set; }
    }

    private sealed class TestSource : GlobalIdentityOwnerDescriptor.MintingSource
    {
        public static GlobalIdentityOwnerDescriptor Lookup(GlobalIdentitySubject subject) =>
            ForLookup(subject, typeof(IdentityDbContext), typeof(User));
    }
}

public sealed class GlobalIdentitySchemaFixture : IAsyncLifetime
{
    public static IdentityAuthorityId Authority { get; } = IdentityAuthorityId.From(Guid.Parse("a91a3552-0000-4000-8000-000000000001"));
    public const string Issuer = "https://identity-schema.invalid/realms/learnstack";
    public PostgresFixture Postgres { get; } = new();

    public async Task InitializeAsync()
    {
        await Postgres.InitializeAsync();
        await MigrationChains.ApplyAllAsync(Postgres.MigrationConnectionString);
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(Postgres.MigrationConnectionString);
        await using var command = new NpgsqlCommand("INSERT INTO public.identity_authority(id,authority_id,issuer) VALUES(1,@authority,@issuer)", owner);
        command.Parameters.AddWithValue("authority", Authority.Value);
        command.Parameters.AddWithValue("issuer", Issuer);
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Postgres.DisposeAsync();
}
