using LearnStack.Modules.Identity.Application.Federation;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Npgsql;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// ADR-0099 § Sign-in exposure on the public read's announced READ ONLY owner: the host tenant's Active,
/// non-tombstone connection only. The SELECT names its tenant beside row security, and no DbContext is involved.
/// </summary>
public sealed class PublicSignInReader(ITenantContext context, IUnitOfWork unit) : IPublicSignInReader
{
    private const string Sql = $"""
        SELECT alias,display_name,protocol FROM public.federation_connections
        WHERE tenant_id=@tenant AND {FederationConnectionReads.LiveSql} AND state='Active'
        """;

    public async Task<PublicSignInOption?> ReadActiveAsync(CancellationToken cancellationToken)
    {
        if (!context.IsResolved || context.TenantId == TenantId.PlatformSentinel || context.HostScope is not { } host
            || host.TenantId != context.TenantId || unit.Mode != TransactionMode.ReadOnly || !unit.HasActiveTransaction
            || !unit.IsTenantContextIssuedOn(unit.Transaction))
            throw new TenantContextMissingException("The public sign-in read requires host provenance and an announced read-only transaction.");
        await using var command = new NpgsqlCommand(Sql, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("tenant", context.TenantId.Value);
        await using var rows = await command.ExecuteReaderAsync(cancellationToken);
        if (!await rows.ReadAsync(cancellationToken)) return null;
        var option = new PublicSignInOption(rows.GetString(0), rows.GetString(1),
            rows.GetString(2) == "Oidc" ? FederationBrokerProtocol.Oidc : FederationBrokerProtocol.Saml);
        if (await rows.ReadAsync(cancellationToken)) throw new InfrastructureException("Federation sign-in storage is inconsistent.");
        return option;
    }
}
