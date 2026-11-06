using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Infrastructure.Persistence;

public sealed partial class NpgsqlUnitOfWork
{
    private InvitationEntryScopeWitness? _invitationWitness;

    public bool IsInvitationEntryOwner(InvitationEntryScopeWitness witness) =>
        ReferenceEquals(_invitationWitness, witness) && _transaction is not null
        && witness.IsBoundTo(this, _transaction, _generation) && _mode == witness.Mode
        && _depth == 1 && !_disposed && !_rollbackOnly && _tenantContextIssued;

    public async Task<IUnitOfWorkScope> BeginInvitationEntryTransactionAsync(InvitationEntryScopeWitness witness,
        ITenantContext context, CancellationToken cancellationToken = default)
    {
        JobApplicationSourceOwner.RefuseExternalEntry();
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireWriteAllowed();
        ArgumentNullException.ThrowIfNull(witness);
        if (HasActiveTransaction || IsGlobalIdentityTransaction || _invitationWitness is not null
            || _selfDiscoveryWitness is not null || _openingSelfDiscovery || _jobSourceOwner is not null
            || JobApplicationSourceOwner.Current is not null || !witness.IsActive
            || !ReferenceEquals(witness.Invocation.Context, context))
        {
            MarkRollbackOnly();
            throw new InvalidOperationException("Invitation entry requires its fresh exact operation owner.");
        }
        IUnitOfWorkScope? frame = null;
        try
        {
            frame = await BeginTransactionCoreAsync(witness.Mode, globalIdentity: false, cancellationToken);
            await SetTenantContextCoreAsync(context, null, false, cancellationToken, witness);
            if (!IsInvitationEntryOwner(witness)) throw new InvalidOperationException("Invitation entry provenance closed.");
            return frame;
        }
        catch
        {
            MarkRollbackOnly();
            try { await RollbackCoreAsync(); } catch { LogSetupCleanupFailure(_logger, null); }
            if (frame is not null) await frame.DisposeAsync();
            throw;
        }
    }
}
