using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Tenancy;
using NpgsqlTypes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Caller-owned, real two-slot evidence lane for database component proofs. The
/// test qualification overload proves the installed anchor and application role;
/// it makes no production topology or HTTP authorization qualification claim.
/// </summary>
internal sealed class ComponentAuthorizationEvidence : IAsyncDisposable
{
    private ComponentAuthorizationEvidence(AuthorizationEvidenceSource source) => Source = source;
    internal AuthorizationEvidenceSource Source { get; }

    internal static Task<ComponentAuthorizationEvidence> CreateAsync(DisposableSchemaDatabase database,
        IdentityAuthorityRegistration? authority = null) =>
        CreateAsync(database.AppConnectionString, database.MigrationConnectionString, authority);

    internal static async Task<ComponentAuthorizationEvidence> CreateAsync(string appConnection,
        string migrationConnection, IdentityAuthorityRegistration? authority = null)
    {
        var registration = authority ?? IdentityMembershipFixture.Authority;
        await using (var owner = new NpgsqlConnection(migrationConnection))
        {
            await owner.OpenAsync();
            // The deployment installer preserves an exact existing anchor and refuses
            // a mismatch. It never rebinds another fixture's authority.
            await IdentityAuthorityDeploymentInstaller.InstallAsync(owner, registration);
        }
        var source = new AuthorizationEvidenceSource(appConnection);
        try
        {
            await source.QualifyAsync(registration.AuthorityId, registration.Issuer);
            return new(source);
        }
        catch
        {
            await source.DisposeAsync();
            throw;
        }
    }

    internal void AddTo(IServiceCollection services) =>
        services.AddSingleton<IAuthorizationEvidenceSessionFactory>(Source);

    internal static async Task<long> CountDenialsAsync(NpgsqlDataSource business, ITenantContext context,
        string policyPrefix)
    {
        await using var connection = await business.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, context.TenantId.Value);
        await SchemaQueries.SetSettingAsync(connection, transaction, "app.organization_id",
            context.OrganizationId?.Value.ToString("D") ?? "");
        await SchemaQueries.AssertApplicationRoleAsync(connection, transaction);
        await using var query = new NpgsqlCommand("""
            SELECT count(*) FROM audit_log
            WHERE operation = 'identity.authorization.deny' AND operation_class = 'Must'
              AND outcome = 'denied' AND tenant_id = @tenant
              AND organization_id IS NOT DISTINCT FROM @organization
              AND starts_with(metadata->'authorization'->>'policy', @prefix)
              AND metadata->'authorization'->>'source' = 'seed'
            """, connection, transaction);
        query.Parameters.AddWithValue("tenant", context.TenantId.Value);
        query.Parameters.Add(new NpgsqlParameter("organization", NpgsqlDbType.Uuid)
        { Value = context.OrganizationId is { } organization ? organization.Value : DBNull.Value });
        query.Parameters.AddWithValue("prefix", policyPrefix);
        return (long)(await query.ExecuteScalarAsync())!;
    }

    public ValueTask DisposeAsync() => Source.DisposeAsync();
}
