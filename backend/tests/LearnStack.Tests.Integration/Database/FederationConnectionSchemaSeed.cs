using Npgsql;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0099 rows for the nonempty isolation census: tenant A holds one tombstone and one live
/// connection, tenant B one live connection. Every write is the application role under the fence.
/// </summary>
internal static class FederationConnectionSchemaSeed
{
    internal static readonly Guid TombstoneA = Guid.Parse("01930000-0000-7000-8000-00000000fa01");
    internal static readonly Guid LiveA = Guid.Parse("01930000-0000-7000-8000-00000000fa02");
    internal static readonly Guid LiveB = Guid.Parse("01930000-0000-7000-8000-00000000fb01");

    internal static string Digest(char value) => new(value, 64);
    internal static string Alias(Guid id) => "lsf-" + id.ToString("N");

    internal static async Task SeedAsync(string connectionString)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        var at = DateTimeOffset.UtcNow.AddHours(-1);
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await FenceAsync(connection, transaction, SchemaFixture.TenantA);
            await InsertAsync(connection, transaction, TombstoneA, SchemaFixture.TenantA, AdminA, at);
            await TombstoneAsync(connection, transaction, TombstoneA, AdminA, at);
            await InsertAsync(connection, transaction, LiveA, SchemaFixture.TenantA, AdminA, at.AddMinutes(10));
            await transaction.CommitAsync();
        }
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await FenceAsync(connection, transaction, SchemaFixture.TenantB);
            await InsertAsync(connection, transaction, LiveB, SchemaFixture.TenantB, AdminB, at);
            await transaction.CommitAsync();
        }
    }

    internal static async Task FenceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenant)
    {
        await ScopeAsync(connection, transaction, tenant);
        await LockAsync(connection, transaction, tenant, null, false);
    }

    /// <summary>The Configure Begin row: Draft, revision one, its pending write and no test.</summary>
    internal static Task InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, Guid tenant,
        Guid actor, DateTimeOffset at, string protocol = "Oidc", string? clientId = "learnstack", string? alias = null,
        string displayName = "Seed SSO", string issuer = "https://idp.example.invalid/issuer") =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO public.federation_connections(id,tenant_id,protocol,alias,display_name,issuer_or_entity_id,state,
                source_kind,client_id,configuration_revision,configuration_digest,desired_digest,
                pending_operation_kind,pending_operation_id,pending_actor,pending_started_at,pending_quiet_until,
                pending_desired_digest,created_at,created_by,row_version)
            VALUES(@id,@tenant,@protocol,@alias,@name,@issuer,'Draft',
                'DiscoveryUrl',@client,1,@digest,@digest,'Configure',@operation,@actor,@at,@at+interval '90 seconds',
                @digest,@at,@actor,0)
            """, ("id", id), ("tenant", tenant), ("protocol", protocol), ("alias", alias ?? Alias(id)),
            ("name", displayName), ("issuer", issuer), ("client", clientId), ("digest", Digest('a')), ("operation", Guid.CreateVersion7()),
            ("actor", actor), ("at", at));

    /// <summary>Settle Configure, begin Delete, then record the confirmed absence as the tombstone.</summary>
    internal static async Task TombstoneAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, Guid actor, DateTimeOffset at)
    {
        await ExecuteAsync(connection, transaction, """
            UPDATE public.federation_connections SET pending_operation_kind=NULL,pending_operation_id=NULL,pending_actor=NULL,
                pending_started_at=NULL,pending_quiet_until=NULL,pending_desired_digest=NULL,last_provider_digest=desired_digest,
                row_version=row_version+1,updated_at=@at,updated_by=@actor WHERE id=@id
            """, ("id", id), ("actor", actor), ("at", at.AddMinutes(1)));
        await ExecuteAsync(connection, transaction, """
            UPDATE public.federation_connections SET pending_operation_kind='Delete',pending_operation_id=@operation,
                pending_actor=@actor,pending_started_at=@at,pending_quiet_until=@at+interval '90 seconds',
                pending_desired_digest=@digest,desired_digest=@digest,row_version=row_version+1,updated_at=@at,updated_by=@actor
            WHERE id=@id
            """, ("id", id), ("actor", actor), ("at", at.AddMinutes(2)), ("operation", Guid.CreateVersion7()), ("digest", Digest('d')));
        await ExecuteAsync(connection, transaction, """
            UPDATE public.federation_connections SET state='Deleted',pending_operation_kind=NULL,pending_operation_id=NULL,
                pending_actor=NULL,pending_started_at=NULL,pending_quiet_until=NULL,pending_desired_digest=NULL,
                last_provider_digest=desired_digest,deleted_at=@at,deleted_by=@actor,
                row_version=row_version+1,updated_at=@at,updated_by=@actor WHERE id=@id
            """, ("id", id), ("actor", actor), ("at", at.AddMinutes(3)));
    }
}
