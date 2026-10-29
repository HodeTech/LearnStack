using System.Data.Common;
using LearnStack.SharedKernel.Identifiers;
using Npgsql;

namespace LearnStack.Tests.Integration.Database;

/// <summary>App-role lifecycle fixture setup under the same canonical exclusive Tenant fence as ordinary writers.</summary>
internal static class TenancyLifecycleFixture
{
    internal static async Task LockAsync(DbConnection connection, DbTransaction transaction, Guid tenant)
    {
        await SchemaQueries.AssertApplicationRoleAsync(connection, transaction);
        await IdentityMembershipFixture.LockAsync((NpgsqlConnection)connection, (NpgsqlTransaction)transaction,
            tenant, null, true);
        // Complete the plan before observing or editing retained roots. The stable
        // Tenant fence excludes a concurrent valid Organization/default writer.
        await SchemaQueries.ExecuteAsync(connection, transaction, """
            SELECT id FROM public.tenants WHERE id=@tenant FOR NO KEY UPDATE;
            SELECT id FROM public.organizations WHERE tenant_id=@tenant ORDER BY id FOR UPDATE;
            """, ("tenant", tenant));
    }

    internal static Task SelectDefaultAsync(DbConnection connection, DbTransaction transaction,
        Guid tenant, Guid organization) => SchemaQueries.ExecuteAsync(connection, transaction, """
            UPDATE public.tenants SET default_organization_id=@organization,
                row_version=row_version+1,updated_at=clock_timestamp(),updated_by=@actor
            WHERE id=@tenant;
            """, ("tenant", tenant), ("organization", organization), ("actor", UserId.SystemActor.Value));
}
