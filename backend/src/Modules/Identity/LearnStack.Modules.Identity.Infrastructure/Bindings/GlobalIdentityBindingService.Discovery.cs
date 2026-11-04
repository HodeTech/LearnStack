using System.Collections.Immutable;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.Modules.Identity.Application.Contracts.Entry;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Modules.Identity.Infrastructure.Bindings;

public sealed partial class GlobalIdentityBindingService
{
    public async Task<IdentityBindingResult> ReadSelfAsync(GlobalIdentitySubject subject, UserId? claimedActor = null,
        CancellationToken cancellationToken = default)
    {
        if (!selfAdmission.Allows(subject, IdentitySelfOperation.State, claimedActor))
            return Refused(IdentityBindingOutcome.AdmissionDenied);
        var result = await ResolveExistingAsync(subject, claimedActor, cancellationToken);
        return selfAdmission.Allows(subject, IdentitySelfOperation.State, claimedActor)
            ? result : Refused(IdentityBindingOutcome.AdmissionDenied);
    }

    public async Task<OwnMembershipScopePage> ReadScopesAsync(GlobalIdentitySubject subject, UserId? claimedActor,
        Guid? afterMembershipId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (limit is < 1 or > 100 || afterMembershipId == Guid.Empty)
            throw new ArgumentException("Own membership page bounds are invalid.");
        if (HasInheritedAuthority() || !selfAdmission.Allows(subject, IdentitySelfOperation.Contexts, claimedActor))
            return EmptyPage(IdentityBindingOutcome.AdmissionDenied);
        if (qualification.Current?.Matches(subject) is not true)
            return EmptyPage(IdentityBindingOutcome.AuthorityUnavailable);
        var descriptor = ForSelfDiscovery(subject, typeof(IdentityDbContext), typeof(User));
        IUnitOfWorkScope? frame = null;
        var source = new ScopeSource(selfAdmission, subject, claimedActor);
        IdentityBindingResult binding;
        ImmutableArray<OwnMembershipScopeWitness> scopes = [];
        var hasNext = false;
        bool cleaned;
        try
        {
            frame = await unit.BeginGlobalIdentityTransactionAsync(descriptor, TransactionMode.ReadOnly, cancellationToken);
            binding = Resolve(await store().FindExactAsync(subject, cancellationToken), subject, claimedActor);
            if (binding.Actor is { } actor)
            {
                scopes = await ReadScopeKeysAsync(descriptor, source, actor, afterMembershipId, limit, cancellationToken);
                if (scopes.Length == limit)
                    hasNext = await HasMoreScopesAsync(scopes[^1].MembershipId, cancellationToken);
            }
            await frame.CompleteAsync(cancellationToken);
        }
        catch (NpgsqlException) { binding = Refused(IdentityBindingOutcome.StorageUnavailable); }
        catch (UserBindingStorageException) { binding = Refused(IdentityBindingOutcome.StorageUnavailable); }
        catch (TimeoutException) { binding = Refused(IdentityBindingOutcome.StorageUnavailable); }
        catch (ObjectDisposedException) when (!selfAdmission.Allows(subject, IdentitySelfOperation.Contexts, claimedActor))
        { binding = Refused(IdentityBindingOutcome.AdmissionDenied); }
        catch
        {
            source.Dispose();
            throw;
        }
        finally { cleaned = await DisposeOwnerAsync(frame); }
        if (!cleaned || !selfAdmission.Allows(subject, IdentitySelfOperation.Contexts, claimedActor))
            binding = Refused(cleaned ? IdentityBindingOutcome.AdmissionDenied : IdentityBindingOutcome.StorageUnavailable);
        if (!binding.IsBound)
        {
            source.Dispose();
            return new(binding, [], null);
        }
        return new(binding, scopes, source, hasNext);
    }

    private async Task<bool> HasMoreScopesAsync(Guid after, CancellationToken cancellationToken)
    {
        // The same subject-confined function supplies a one-key lookahead on
        // this global owner; its 100-key maximum and tuple projection do not grow.
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS(SELECT FROM public.fn_identity_self_membership_scopes(@after,1))
            """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("after", after);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private async Task<ImmutableArray<OwnMembershipScopeWitness>> ReadScopeKeysAsync(
        GlobalIdentityOwnerDescriptor descriptor, ScopeSource source, UserId actor, Guid? after, int limit,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(unit.GetGlobalIdentityAnnouncement(unit.Transaction)?.Descriptor, descriptor))
            throw new InvalidOperationException("Own membership discovery requires its exact global owner.");
        await using var command = new NpgsqlCommand(
            "SELECT membership_id,tenant_id,organization_id FROM public.fn_identity_self_membership_scopes(@after,@limit)",
            (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!)
        { CommandTimeout = 5 };
        command.Parameters.AddWithValue("after", NpgsqlDbType.Uuid, after is { } value ? value : DBNull.Value);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var scopes = ImmutableArray.CreateBuilder<OwnMembershipScopeWitness>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (scopes.Count == limit) throw new UserBindingStorageException();
            scopes.Add(source.CaptureScope(reader.GetGuid(0), actor, TenantId.From(reader.GetGuid(1)),
                reader.IsDBNull(2) ? null : OrganizationId.From(reader.GetGuid(2))));
        }
        return scopes.ToImmutable();
    }

    private static OwnMembershipScopePage EmptyPage(IdentityBindingOutcome outcome) => new(Refused(outcome), [], null);

    private sealed class ScopeSource(IIdentitySelfEntryAdmission admission, GlobalIdentitySubject subject, UserId? actor)
        : OwnMembershipScopeWitness.MintingSource
    {
        protected override bool IsEvidenceCurrent => admission.Allows(subject, IdentitySelfOperation.Contexts, actor);
        internal OwnMembershipScopeWitness CaptureScope(Guid membership, UserId user, TenantId tenant, OrganizationId? org) =>
            Capture(membership, user, tenant, org);
    }
}
