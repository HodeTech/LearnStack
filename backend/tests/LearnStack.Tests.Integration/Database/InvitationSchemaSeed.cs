using System.Security.Cryptography;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Permissions;
using Npgsql;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

internal static class InvitationSchemaSeed
{
    internal static readonly Guid TenantOfferA = Guid.Parse("01930000-0000-7000-8000-00000000f101");
    internal static readonly Guid BranchOfferA1 = Guid.Parse("01930000-0000-7000-8000-00000000f103");
    internal static readonly Guid BranchOfferA2 = Guid.Parse("01930000-0000-7000-8000-00000000f104");
    internal static readonly Guid TenantOfferB = Guid.Parse("01930000-0000-7000-8000-00000000f201");

    internal static byte[] Digest(Guid id) => SHA256.HashData(id.ToByteArray());

    internal static async Task SeedAsync(string connectionString)
    {
        (Guid Tenant, Guid? Organization, Guid Id)[] rows =
        [
            (SchemaFixture.TenantA, null, TenantOfferA),
            (SchemaFixture.TenantA, null, Guid.Parse("01930000-0000-7000-8000-00000000f102")),
            (SchemaFixture.TenantA, SchemaFixture.OrgA1, BranchOfferA1),
            (SchemaFixture.TenantA, SchemaFixture.OrgA2, BranchOfferA2),
            (SchemaFixture.TenantB, null, TenantOfferB),
            (SchemaFixture.TenantB, SchemaFixture.OrgB1, Guid.Parse("01930000-0000-7000-8000-00000000f202")),
        ];
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        foreach (var row in rows)
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await ScopeAsync(connection, transaction, row.Tenant, row.Organization);
            await LockAsync(connection, transaction, row.Tenant, null, false);
            await InsertAsync(connection, transaction, row.Id, row.Tenant, row.Organization);
            await transaction.CommitAsync();
        }
    }

    internal static Task InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid id, Guid tenant, Guid? organization = null, string? email = null, Guid? roleId = null, DateTimeOffset? created = null) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO public.invitations(id,tenant_id,organization_id,recipient_email,inviter_user_id,role_id,
                pinned_role_version,status,generation_id,generation,generation_created_at,expires_at,
                verifier_digest,entry_origin,entry_locale,current_delivery_job_id,submission_status,created_at,created_by)
            VALUES(@id,@tenant,@organization,@email,@inviter,@role,0,'Pending',@generation,1,
                @at,@at+interval '14 days',@digest,
                'https://manage.invitation.invalid','en',@job,'Queued',@at,@inviter)
            """, ("id", id), ("tenant", tenant), ("organization", organization),
            ("email", email ?? "invitation-" + id.ToString("N") + "@example.invalid"),
            ("inviter", tenant == SchemaFixture.TenantA ? AdminA : AdminB),
            ("role", roleId ?? RoleId(tenant, BuiltInRoleKey.Learner, organization is null ? PermissionScope.Tenant : PermissionScope.Organization)),
            ("generation", Guid.CreateVersion7()), ("job", Guid.CreateVersion7()), ("digest", Digest(id)),
            ("at", created ?? DateTimeOffset.UtcNow));
}
