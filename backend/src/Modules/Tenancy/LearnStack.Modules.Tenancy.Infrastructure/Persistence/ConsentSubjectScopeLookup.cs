using LearnStack.Infrastructure.Email;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>One preliminary fixed-function read, disposed before any scope observation escapes.</summary>
public sealed class ConsentSubjectScopeLookup(IServiceScopeFactory scopes, IUnitOfWork ambient) : IConsentSubjectScopeLookup
{
    public async Task<IReadOnlyList<ConsentSubjectScopeObservation>> LookupAsync(UserId actor, TenantId hostTenant,
        OrganizationId? hostOrganization, ConsentSubjectLookupMode mode, Guid? membershipId,
        Guid? afterMembership, int limit, CancellationToken cancellationToken = default)
    {
        if (!StronglyTypedId.IsAssigned(actor) || actor == UserId.SystemActor || actor.Value.Version != 7
            || !StronglyTypedId.IsAssigned(hostTenant) || hostTenant == TenantId.PlatformSentinel || hostTenant.Value.Version != 7
            || !StronglyTypedId.IsAssignedWhenPresent(hostOrganization)
            || (hostOrganization is { } hostOrg && hostOrg.Value.Version != 7) || !Enum.IsDefined(mode)
            || limit is < 1 or > 20 || (membershipId is { } id && (id == Guid.Empty || id.Version != 7))
            || (afterMembership is { } after && (after == Guid.Empty || after.Version != 7))
            || (mode == ConsentSubjectLookupMode.List && membershipId is not null)
            || (mode == ConsentSubjectLookupMode.Select && (membershipId is null || afterMembership is not null || limit != 1)))
            throw new ArgumentException("Consent scope lookup input refused.");

        if (ambient.HasActiveTransaction || ambient.IsRollbackOnly)
            throw new InfrastructureException("Consent scope lookup unavailable.");
        try
        {
            IReadOnlyList<ConsentSubjectScopeObservation> observed;
            await using (var childScope = scopes.CreateAsyncScope())
            {
                var child = childScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                if (child.HasActiveTransaction || child.IsRollbackOnly)
                    throw new InfrastructureException("Consent scope lookup unavailable.");
                await using var owner = await child.BeginTransactionAsync(TransactionMode.ReadOnly, cancellationToken);
                if (!owner.IsOwner || child is IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true })
                    throw new InfrastructureException("Consent scope lookup unavailable.");
                await RequirePrivacyAsync(child, cancellationToken);
                await using var command = new NpgsqlCommand("""
                    SELECT membership_id, tenant_id, organization_id
                    FROM public.fn_tenancy_own_field_consent_scope(@actor,@tenant,@organization,@mode,@membership,@after,@limit)
                    """, (NpgsqlConnection)child.Connection, (NpgsqlTransaction)child.Transaction!) { CommandTimeout = 5 };
                command.Parameters.AddWithValue("actor", NpgsqlDbType.Uuid, actor.Value);
                command.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, hostTenant.Value);
                command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, hostOrganization is { } org ? org.Value : DBNull.Value);
                command.Parameters.AddWithValue("mode", NpgsqlDbType.Text, mode == ConsentSubjectLookupMode.List ? "list" : "select");
                command.Parameters.AddWithValue("membership", NpgsqlDbType.Uuid, membershipId is { } member ? member : DBNull.Value);
                command.Parameters.AddWithValue("after", NpgsqlDbType.Uuid, afterMembership is { } cursor ? cursor : DBNull.Value);
                command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit);
                observed = await ReadAsync(command, hostTenant, hostOrganization, mode, membershipId, afterMembership, limit, cancellationToken);
                await owner.CompleteAsync(cancellationToken);
            }
            return observed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception failure) when (failure is NpgsqlException or TimeoutException or OperationCanceledException or InvalidCastException or InvalidOperationException)
        {
            throw new InfrastructureException("Consent scope lookup unavailable.");
        }
    }

    private static async Task RequirePrivacyAsync(IUnitOfWork owner, CancellationToken cancellationToken)
    {
        try { await new InvitationEmailDatabasePrivacy(owner).RequireAsync(cancellationToken); }
        catch (InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InfrastructureException("Consent database privacy unavailable.");
        }
    }

    private static async Task<IReadOnlyList<ConsentSubjectScopeObservation>> ReadAsync(NpgsqlCommand command,
        TenantId hostTenant, OrganizationId? hostOrganization, ConsentSubjectLookupMode mode, Guid? requested,
        Guid? after, int limit, CancellationToken cancellationToken)
    {
        var result = new List<ConsentSubjectScopeObservation>(limit);
        var seen = new HashSet<Guid>();
        Guid? previous = after;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (result.Count == limit || reader.IsDBNull(0) || reader.IsDBNull(1)) RefuseObservation();
            var member = reader.GetGuid(0);
            var tenant = reader.GetGuid(1);
            Guid? organization = reader.IsDBNull(2) ? null : reader.GetGuid(2);
            if (member == Guid.Empty || member.Version != 7 || tenant != hostTenant.Value
                || (organization is { } branch && (branch == Guid.Empty || branch.Version != 7))
                || (hostOrganization is { } host && organization != host.Value)
                || !seen.Add(member) || (previous is { } anchor && member.CompareTo(anchor) <= 0)
                || (mode == ConsentSubjectLookupMode.Select && member != requested)) RefuseObservation();
            result.Add(new ConsentSubjectScopeObservation(member, hostTenant,
                organization is { } org ? OrganizationId.From(org) : null));
            previous = member;
        }
        return result.AsReadOnly();
    }

    private static void RefuseObservation() => throw new InfrastructureException("Consent scope observation refused.");
}
