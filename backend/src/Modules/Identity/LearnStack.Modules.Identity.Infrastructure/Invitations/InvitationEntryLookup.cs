using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.Infrastructure.Email;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Modules.Identity.Infrastructure.Invitations;

/// <summary>Only exact offer lookups; preliminary scope is fully disposed before its observation returns.</summary>
public sealed class InvitationEntryLookup(IServiceScopeFactory scopes, IUnitOfWork unit) : IInvitationEntryLookup
{
    public async Task<InvitationEntryObservation?> LookupAsync(Guid invitationId, ReadOnlyMemory<byte> verifierDigest,
        TenantId hostTenant, GlobalIdentitySubject subject, CancellationToken cancellationToken = default)
    {
        if (invitationId.Version != 7 || verifierDigest.Length != 32 || hostTenant == TenantId.PlatformSentinel)
            throw new ArgumentException("Invitation lookup input refused.");
        try
        {
            await using var childScope = scopes.CreateAsyncScope();
            var child = childScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            if (child.HasActiveTransaction || child.IsRollbackOnly) throw new InfrastructureException("Invitation entry unavailable.");
            await using var owner = await child.BeginTransactionAsync(TransactionMode.ReadOnly, cancellationToken);
            if (!owner.IsOwner || child is IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true })
                throw new InfrastructureException("Invitation entry unavailable.");
            await using var command = new NpgsqlCommand("SELECT * FROM public.fn_identity_invitation_entry(@id,@digest,@tenant,@authority,@subject)",
                (NpgsqlConnection)child.Connection, (NpgsqlTransaction)child.Transaction!)
            { CommandTimeout = 5 };
            await RequirePrivacyAsync(child, cancellationToken);
            Bind(command, invitationId, verifierDigest.ToArray(), hostTenant, subject.AuthorityId, subject.SubjectKey);
            var observed = await ReadAsync(command, cancellationToken);
            await owner.CompleteAsync(cancellationToken);
            return observed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception failure) when (failure is NpgsqlException or TimeoutException or OperationCanceledException)
        { throw new InfrastructureException("Invitation entry unavailable."); }
    }

    public async Task<InvitationEntryObservation?> RecheckAsync(InvitationEntryScopeWitness witness,
        CancellationToken cancellationToken = default)
    {
        if (unit is not IInvitationEntryUnitOfWork entry || !entry.IsInvitationEntryOwner(witness)
            || unit.Mode != TransactionMode.ReadWrite)
            throw new InfrastructureException("Invitation eligibility requires its exact fenced owner.");
        try
        {
            await using var command = new NpgsqlCommand("SELECT * FROM public.fn_identity_invitation_entry(@id,@digest,@tenant,@authority,@subject)",
                (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!)
            { CommandTimeout = 5 };
            await RequirePrivacyAsync(unit, cancellationToken);
            Bind(command, witness.Offer.InvitationId, Convert.FromHexString(witness.VerifierDigest), witness.Offer.TenantId,
                witness.AuthorityId, witness.SubjectKey);
            return await ReadAsync(command, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception failure) when (failure is NpgsqlException or TimeoutException or OperationCanceledException)
        { unit.MarkRollbackOnly(); throw new InfrastructureException("Invitation eligibility unavailable."); }
    }
    private static async Task RequirePrivacyAsync(IUnitOfWork owner, CancellationToken cancellationToken)
    {
        try { await new InvitationEmailDatabasePrivacy(owner).RequireAsync(cancellationToken); }
        catch (InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InfrastructureException("Invitation database privacy unavailable.");
        }
    }
    private static void Bind(NpgsqlCommand command, Guid id, byte[] digest, TenantId tenant,
        IdentityAuthorityId authority, IdentitySubjectKey subject)
    {
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, id);
        command.Parameters.AddWithValue("digest", NpgsqlDbType.Bytea, digest);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenant.Value);
        command.Parameters.AddWithValue("authority", NpgsqlDbType.Uuid, authority.Value);
        command.Parameters.AddWithValue("subject", NpgsqlDbType.Bytea, subject.ToByteArray());
    }
    private static async Task<InvitationEntryObservation?> ReadAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var status = reader.GetString(8);
        if (status is not ("Pending" or "Accepted")) throw new InfrastructureException("Invitation observation refused.");
        var result = new InvitationEntryObservation(reader.GetGuid(0), TenantId.From(reader.GetGuid(1)),
            reader.IsDBNull(2) ? null : OrganizationId.From(reader.GetGuid(2)), reader.GetInt64(3), reader.GetGuid(4), reader.GetInt64(5),
            UserId.From(reader.GetGuid(6)), reader.GetBoolean(7), status == "Accepted", reader.IsDBNull(9) ? null : reader.GetGuid(9),
            reader.IsDBNull(10) ? null : UserId.From(reader.GetGuid(10)), reader.IsDBNull(11) ? null : reader.GetString(11));
        if (await reader.ReadAsync(cancellationToken)) throw new InfrastructureException("Invitation observation refused.");
        return result;
    }
}
