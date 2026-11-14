using LearnStack.Modules.Identity.Application.Contracts.Entry;
using LearnStack.Modules.Identity.Application.Federation;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Npgsql;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// ADR-0099 Amendment 6 (H): the fourteenth logical tenant setter. On a connection of its own it issues
/// <c>SET TRANSACTION READ ONLY</c>, announces exactly the host tenant (no organization, scope, host or identity GUC)
/// and runs one statement: the host tenant's Active, non-tombstone federation alias and protocol. That protocol's plan
/// entitlement then comes through <see cref="IEntitlementProvider"/>, the port <c>IFeatureFlags</c> composes over; the
/// two SSO keys are plan-projected, default false and name no killswitch, so the composition is the same answer. It
/// writes nothing and grants nothing. Any failure is unavailable: the self read reports 503 and the BFF treats the
/// outcome as indeterminate.
/// </summary>
public sealed class FederationHostAliasReader(Lazy<NpgsqlDataSource> dataSource, IEntitlementProvider entitlements)
    : IFederationHostAliasReader
{
    /// <summary>The one statement this setter runs after its announcement; pinned by the architecture rule.</summary>
    internal const string AliasSql = $"""
        SELECT alias,protocol FROM public.federation_connections
        WHERE tenant_id=@tenant AND {FederationConnectionReads.LiveSql} AND state='Active'
        """;

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);

    public async Task<string?> ReadActiveAliasAsync(TenantId hostTenant, CancellationToken cancellationToken = default)
    {
        JobApplicationSourceOwner.RefuseExternalEntry();
        if (!StronglyTypedId.IsAssigned(hostTenant) || hostTenant == TenantId.PlatformSentinel)
            throw Unavailable();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);
        string? alias;
        string? protocol;
        try
        {
            (alias, protocol) = await ReadAsync(hostTenant, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw Unavailable(exception);
        }
        if (alias is null) return null;
        var key = protocol switch
        {
            "Oidc" => FeatureKeys.SsoOidc,
            "Saml" => FeatureKeys.SsoSaml,
            _ => throw Unavailable(),
        };
        if (!FeatureKeys.All.TryGetValue(key, out var descriptor) || descriptor.Source != FeatureSource.PlanProjected
            || descriptor.Killswitch is not null)
            throw Unavailable();
        EntitlementProjection projection;
        try
        {
            projection = await entitlements.GetAsync(hostTenant, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw Unavailable(exception);
        }
        if (projection is null || projection.TenantId != hostTenant) throw Unavailable();
        var granted = projection.Features.TryGetValue(key.Value, out var value) ? value : descriptor.Default;
        return granted ? alias : null;
    }

    private async Task<(string? Alias, string? Protocol)> ReadAsync(TenantId hostTenant, CancellationToken cancellationToken)
    {
        await using var connection = dataSource.Value.CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction) { CommandTimeout = 3 })
            await readOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using (var announce = new NpgsqlCommand("SELECT set_config('app.tenant_id', @tenant, true)", connection, transaction)
        { CommandTimeout = 3 })
        {
            announce.Parameters.AddWithValue("tenant", hostTenant.Value.ToString("D"));
            await announce.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        string? alias = null;
        string? protocol = null;
        await using (var read = new NpgsqlCommand(AliasSql, connection, transaction) { CommandTimeout = 3 })
        {
            read.Parameters.AddWithValue("tenant", hostTenant.Value);
            await using var rows = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                alias = rows.GetString(0);
                protocol = rows.GetString(1);
                if (await rows.ReadAsync(cancellationToken).ConfigureAwait(false)) throw Unavailable();
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (alias is not null && !FederationAliasShape.IsDerived(alias)) throw Unavailable();
        return (alias, protocol);
    }

    private static InfrastructureException Unavailable(Exception? inner = null) =>
        inner is null ? new("The host federation alias is unavailable.") : new("The host federation alias is unavailable.", inner);
}

/// <summary>The derived alias shape (<c>lsf-</c> and 32 lowercase hexadecimal characters); never accepted from input.</summary>
internal static class FederationAliasShape
{
    private static readonly System.Buffers.SearchValues<char> LowerHex = System.Buffers.SearchValues.Create("0123456789abcdef");

    internal static bool IsDerived(string value) => value.Length == 36 && value.StartsWith("lsf-", StringComparison.Ordinal)
        && value.AsSpan(4).IndexOfAnyExcept(LowerHex) < 0;
}
