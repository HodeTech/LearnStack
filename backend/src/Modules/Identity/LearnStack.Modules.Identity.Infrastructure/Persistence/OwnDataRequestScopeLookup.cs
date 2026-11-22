using LearnStack.Infrastructure.Email;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// One preliminary fixed-function read of <c>fn_identity_own_data_request_scopes</c> (ADR-0106 § 9), on its own
/// physically read-only owner in a child scope, disposed before any scope observation escapes. Modelled on the
/// consent subject's <c>ConsentSubjectScopeLookup</c>.
/// </summary>
public sealed class OwnDataRequestScopeLookup(IServiceScopeFactory scopes, IUnitOfWork ambient) : IOwnDataRequestScopeLookup
{
    public async Task<IReadOnlyList<DataRequestScopeGroup>> LookupAsync(UserId actor, TenantId hostTenant,
        OrganizationId? hostOrganization, OwnDataRequestLookupMode mode, DataRequestScopeGroup? selected,
        DataRequestScopeGroup? after, int limit, CancellationToken cancellationToken = default)
    {
        if (!StronglyTypedId.IsAssigned(actor) || actor == UserId.SystemActor || actor.Value.Version != 7
            || !StronglyTypedId.IsAssigned(hostTenant) || hostTenant == TenantId.PlatformSentinel || hostTenant.Value.Version != 7
            || !StronglyTypedId.IsAssignedWhenPresent(hostOrganization)
            || (hostOrganization is { } hostOrg && hostOrg.Value.Version != 7) || !Enum.IsDefined(mode)
            || limit is < 1 or > OwnDataRequestScopeWitness.MaximumPage
            || !WithinHost(selected, hostTenant) || !WithinHost(after, hostTenant)
            || (mode == OwnDataRequestLookupMode.List && selected is not null)
            || (mode == OwnDataRequestLookupMode.Select && (selected is null || after is not null || limit != 1)))
            throw new ArgumentException("Own data request scope lookup input refused.");

        if (ambient.HasActiveTransaction || ambient.IsRollbackOnly)
            throw new InfrastructureException("Own data request scope lookup unavailable.");
        try
        {
            IReadOnlyList<DataRequestScopeGroup> observed;
            await using (var childScope = scopes.CreateAsyncScope())
            {
                var child = childScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                if (child.HasActiveTransaction || child.IsRollbackOnly)
                    throw new InfrastructureException("Own data request scope lookup unavailable.");
                await using var owner = await child.BeginTransactionAsync(TransactionMode.ReadOnly, cancellationToken);
                if (!owner.IsOwner || child is IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true })
                    throw new InfrastructureException("Own data request scope lookup unavailable.");
                await RequirePrivacyAsync(child, cancellationToken);
                await using var command = new NpgsqlCommand("""
                    SELECT tenant_id, organization_id
                    FROM public.fn_identity_own_data_request_scopes(@actor,@tenant,@host_organization,@mode,@organization,@after,@limit)
                    """, (NpgsqlConnection)child.Connection, (NpgsqlTransaction)child.Transaction!) { CommandTimeout = 5 };
                command.Parameters.AddWithValue("actor", NpgsqlDbType.Uuid, actor.Value);
                command.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, hostTenant.Value);
                command.Parameters.AddWithValue("host_organization", NpgsqlDbType.Uuid, hostOrganization is { } org ? org.Value : DBNull.Value);
                command.Parameters.AddWithValue("mode", NpgsqlDbType.Text, mode == OwnDataRequestLookupMode.List ? "list" : "select");
                command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid,
                    selected?.OrganizationId is { } chosen ? chosen.Value : DBNull.Value);
                // The tenant-wide group's list key is nil: it sorts first, so "after it" is every organization's group.
                command.Parameters.AddWithValue("after", NpgsqlDbType.Uuid,
                    after is null ? DBNull.Value : after.OrganizationId is { } cursor ? cursor.Value : Guid.Empty);
                command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit);
                observed = await ReadAsync(command, hostTenant, hostOrganization, mode, selected, after, limit, cancellationToken);
                await owner.CompleteAsync(cancellationToken);
            }
            return observed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception failure) when (failure is NpgsqlException or TimeoutException or OperationCanceledException
            or InvalidCastException or InvalidOperationException or ArgumentException)
        {
            throw new InfrastructureException("Own data request scope lookup unavailable.");
        }
    }

    private static bool WithinHost(DataRequestScopeGroup? group, TenantId hostTenant) => group is null
        || (group.TenantId == hostTenant && (group.OrganizationId is not { } organization || organization.Value.Version == 7));

    private static async Task RequirePrivacyAsync(IUnitOfWork owner, CancellationToken cancellationToken)
    {
        try { await new InvitationEmailDatabasePrivacy(owner).RequireAsync(cancellationToken); }
        catch (InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InfrastructureException("Own data request database privacy unavailable.");
        }
    }

    /// <summary>
    /// Re-checks every row the function returned against the same host ceiling, order, page and selection, so a
    /// function that drifted can never widen what the caller observes.
    /// </summary>
    private static async Task<IReadOnlyList<DataRequestScopeGroup>> ReadAsync(NpgsqlCommand command,
        TenantId hostTenant, OrganizationId? hostOrganization, OwnDataRequestLookupMode mode, DataRequestScopeGroup? selected,
        DataRequestScopeGroup? after, int limit, CancellationToken cancellationToken)
    {
        var result = new List<DataRequestScopeGroup>(limit);
        Guid? previous = after is null ? null : after.OrganizationId?.Value ?? Guid.Empty;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (result.Count == limit || reader.IsDBNull(0)) RefuseObservation();
            var tenant = reader.GetGuid(0);
            Guid? organization = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            var key = organization ?? Guid.Empty;
            if (tenant != hostTenant.Value
                || (organization is { } branch && (branch == Guid.Empty || branch.Version != 7))
                || (hostOrganization is { } host && organization != host.Value)
                || (previous is { } anchor && key.CompareTo(anchor) <= 0)
                || (mode == OwnDataRequestLookupMode.Select && organization != selected!.OrganizationId?.Value)) RefuseObservation();
            result.Add(new DataRequestScopeGroup(hostTenant, organization is { } org ? OrganizationId.From(org) : null));
            previous = key;
        }
        return result.AsReadOnly();
    }

    private static void RefuseObservation() => throw new InfrastructureException("Own data request scope observation refused.");
}
