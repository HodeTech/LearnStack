using LearnStack.Modules.Identity.Domain;
using System.Text.Json;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Populated application-role contextual graphs, independent of the four development personas.</summary>
public sealed class IdentityMembershipFixture : IAsyncLifetime
{
    public static readonly Guid AdminA = Guid.Parse("01930000-0000-7000-8000-00000000c101");
    public static readonly Guid PersonA = Guid.Parse("01930000-0000-7000-8000-00000000c102");
    public static readonly Guid AdminB = Guid.Parse("01930000-0000-7000-8000-00000000c201");
    public static readonly Guid PersonB = Guid.Parse("01930000-0000-7000-8000-00000000c202");
    public static readonly Guid AdminMembershipA = Guid.Parse("01930000-0000-7000-8000-00000000d101");
    public static readonly Guid LearnerMembershipA = Guid.Parse("01930000-0000-7000-8000-00000000d102");
    public static readonly Guid BranchMembershipA1 = Guid.Parse("01930000-0000-7000-8000-00000000d103");
    public static readonly Guid BranchMembershipA2 = Guid.Parse("01930000-0000-7000-8000-00000000d104");
    public static readonly Guid AdminMembershipB = Guid.Parse("01930000-0000-7000-8000-00000000d201");
    public static readonly Guid BranchMembershipB = Guid.Parse("01930000-0000-7000-8000-00000000d202");
    public static readonly IdentityAuthorityRegistration Authority = new(
        IdentityAuthorityId.From(Guid.Parse("01930000-0000-7000-8000-00000000c000")), "https://membership-fixture.invalid/realms/learnstack");

    public PostgresFixture Postgres { get; } = new();
    public async Task InitializeAsync()
    {
        await Postgres.InitializeAsync();
        await MigrationChains.ApplyAllAsync(Postgres.MigrationConnectionString);
        await SeedSchemaAsync(Postgres.MigrationConnectionString, Postgres.AppConnectionString);
    }
    public Task DisposeAsync() => Postgres.DisposeAsync();

    public static Guid RoleId(Guid tenant, BuiltInRoleKey key, PermissionScope scope)
    {
        var index = Array.FindIndex(IdentityFoundationManifest.Version1.Templates.ToArray(), template => template.BuiltInKey == key && template.Scope == scope);
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(key));
        return Guid.Parse($"01930000-0000-7000-8000-00000000e{(tenant == SchemaFixture.TenantA ? 1 : 2)}{index + 1:00}");
    }

    public static async Task SeedSchemaAsync(string ownerConnection, string appConnection)
    {
        await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(ownerConnection))
            await IdentityAuthorityDeploymentInstaller.InstallAsync(owner, Authority);
        foreach (var actor in new[] { AdminA, PersonA, AdminB, PersonB })
            await InsertUserAsync(appConnection, actor);
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(appConnection);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        foreach (var tenant in new[] { SchemaFixture.TenantA, SchemaFixture.TenantB })
            foreach (var template in IdentityFoundationManifest.Version1.Templates)
            {
                await using var transaction = await app.BeginTransactionAsync();
                await ScopeAsync(app, transaction, tenant);
                await LockAsync(app, transaction, tenant, null, false);
                var role = RoleId(tenant, template.BuiltInKey, template.Scope);
                await ExecuteAsync(app, transaction, """
                INSERT INTO public.roles(id,tenant_id,built_in_key,permission_scope,created_at,created_by)
                VALUES(@id,@tenant,@key,@scope,clock_timestamp(),@actor)
                """, ("id", role), ("tenant", tenant), ("key", template.BuiltInKey.ToString()),
                    ("scope", template.Scope.ToString()), ("actor", UserId.SystemActor.Value));
                foreach (var permission in template.Grants)
                    await ExecuteAsync(app, transaction, """
                    INSERT INTO public.role_permission_grants(tenant_id,role_id,permission_scope,permission_key)
                    VALUES(@tenant,@role,@scope,@key)
                    """, ("tenant", tenant), ("role", role), ("scope", permission.Scope.ToString()), ("key", permission.Key));
                await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
                await transaction.CommitAsync();
            }
        await CommitMemberAsync(app, SchemaFixture.TenantA, null, AdminMembershipA, AdminA, BuiltInRoleKey.TenantAdmin);
        await CommitMemberAsync(app, SchemaFixture.TenantA, null, LearnerMembershipA, PersonA, BuiltInRoleKey.Learner);
        await CommitMemberAsync(app, SchemaFixture.TenantA, SchemaFixture.OrgA1, BranchMembershipA1, PersonA, BuiltInRoleKey.Learner);
        await CommitMemberAsync(app, SchemaFixture.TenantA, SchemaFixture.OrgA2, BranchMembershipA2, PersonA, BuiltInRoleKey.Learner);
        await CommitMemberAsync(app, SchemaFixture.TenantB, null, AdminMembershipB, AdminB, BuiltInRoleKey.TenantAdmin);
        await CommitMemberAsync(app, SchemaFixture.TenantB, SchemaFixture.OrgB1, BranchMembershipB, PersonB, BuiltInRoleKey.Learner);
    }

    private static async Task CommitMemberAsync(NpgsqlConnection connection, Guid tenant, Guid? organization,
        Guid member, Guid user, BuiltInRoleKey key)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await ScopeAsync(connection, transaction, tenant, organization);
        await LockAsync(connection, transaction, tenant, user, key == BuiltInRoleKey.TenantAdmin);
        await InsertMemberAsync(connection, transaction, tenant, organization, member, user,
            RoleId(tenant, key, organization is null ? PermissionScope.Tenant : PermissionScope.Organization));
        await ExecuteAsync(connection, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        await transaction.CommitAsync();
    }

    public static async Task InsertUserAsync(string appConnection, Guid user)
    {
        var subject = new GlobalIdentitySubject(Authority.AuthorityId, Authority.Issuer, "fixture-subject-" + user.ToString("N"));
        var audit = Guid.CreateVersion7();
        var at = GlobalIdentityCreationReference.NormalizeTimestamp(DateTimeOffset.UtcNow);
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(appConnection);
        await using var transaction = await app.BeginTransactionAsync();
        await GlobalIdentitySchemaTests.AnnounceAsync(app, transaction, subject);
        await ExecuteAsync(app, transaction, """
            INSERT INTO public.users(id,authority_id,subject_key,provider_subject,status,creation_audit_id,creation_audit_timestamp,created_at,created_by,row_version)
            VALUES(@user,@authority,@key,@subject,'Active',@audit,@at,@at,@actor,0);
            INSERT INTO public.audit_log(id,timestamp,tenant_id,organization_id,actor_user_id,module,operation,
                operation_type,operation_class,entity_type,entity_id,outcome,metadata)
            VALUES(@audit,@at,@sentinel,NULL,NULL,'identity','identity.user.create','SecurityEvent','Must','User',@entity,'success',@metadata::jsonb);
            SET CONSTRAINTS ALL IMMEDIATE;
            """, ("user", user), ("authority", subject.AuthorityId.Value), ("key", subject.SubjectKey.ToByteArray()),
            ("subject", subject.Subject), ("audit", audit), ("at", at), ("actor", UserId.SystemActor.Value),
            ("sentinel", TenantId.PlatformSentinel.Value), ("entity", user.ToString("D")),
            ("metadata", JsonSerializer.Serialize(new
            {
                identity_authority_id = subject.AuthorityId.Value.ToString("D"),
                identity_subject_key = subject.SubjectKey.Hex,
                identity_source = "identity-bootstrap"
            })));
        await transaction.CommitAsync();
    }

    public static async Task InsertMemberAsync(NpgsqlConnection app, NpgsqlTransaction transaction, Guid tenant,
        Guid? organization, Guid member, Guid user, Guid role, string? displayName = "profile-canary")
    {
        await ExecuteAsync(app, transaction, """
            INSERT INTO public.memberships(id,tenant_id,organization_id,user_id,status,created_at,created_by)
            VALUES(@member,@tenant,@organization,@user,'Active',clock_timestamp(),@actor);
            INSERT INTO public.membership_profiles(tenant_id,membership_id,organization_id,display_name,preferred_locale)
            VALUES(@tenant,@member,@organization,@display,'en');
            INSERT INTO public.membership_role_assignments(tenant_id,membership_id,role_id,organization_id)
            VALUES(@tenant,@member,@role,@organization);
            """, ("member", member), ("tenant", tenant), ("organization", organization), ("user", user),
            ("actor", UserId.SystemActor.Value), ("role", role), ("display", displayName));
    }

    public static Task ScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenant, Guid? organization = null) =>
        ExecuteAsync(connection, transaction, """
            SELECT set_config('app.tenant_id',@tenant,true),set_config('app.organization_id',@organization,true)
            """, ("tenant", tenant.ToString("D")), ("organization", organization?.ToString("D") ?? string.Empty));

    public static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenant, Guid? user, bool exclusive)
    {
        if (user is { } actor)
            await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(@key)",
                ("key", IdentityAdvisoryLockKey.ForUser(UserId.From(actor))));
        await ExecuteAsync(connection, transaction,
            exclusive ? "SELECT pg_advisory_xact_lock(@key)" : "SELECT pg_advisory_xact_lock_shared(@key)",
            ("key", IdentityAdvisoryLockKey.ForTenant(TenantId.From(tenant))));
    }

    public static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql,
        params (string Name, object? Value)[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddParameters(command, values);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql,
        params (string Name, object? Value)[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddParameters(command, values);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static void AddParameters(NpgsqlCommand command, (string Name, object? Value)[] values)
    {
        foreach (var (name, value) in values)
        {
            if (value is not null) command.Parameters.AddWithValue(name, value);
            else command.Parameters.Add(new NpgsqlParameter(name,
                name == "organization" ? NpgsqlDbType.Uuid : NpgsqlDbType.Text)
            { Value = DBNull.Value });
        }
    }
}
