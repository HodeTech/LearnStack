using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Infrastructure.Persistence;

public sealed partial class NpgsqlUnitOfWork
{
    private ConsentSubjectScopeWitness? _consentWitness;

    public bool IsConsentSubjectOwner(ConsentSubjectScopeWitness witness) =>
        ReferenceEquals(_consentWitness, witness) && _transaction is not null
        && witness.IsBoundTo(this, _transaction, _generation) && _mode == witness.Mode
        && _depth == 1 && !_disposed && !_rollbackOnly && _tenantContextIssued;

    public async Task<IUnitOfWorkScope> BeginConsentSubjectTransactionAsync(ConsentSubjectScopeWitness witness,
        ITenantContext context, CancellationToken cancellationToken = default)
    {
        JobApplicationSourceOwner.RefuseExternalEntry();
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireWriteAllowed();
        ArgumentNullException.ThrowIfNull(witness);
        if (HasActiveTransaction || IsGlobalIdentityTransaction || _consentWitness is not null || _invitationWitness is not null
            || _selfDiscoveryWitness is not null || _openingSelfDiscovery || _jobSourceOwner is not null
            || JobApplicationSourceOwner.Current is not null || !witness.IsActive
            || !ReferenceEquals(witness.Invocation.Context, context))
        {
            MarkRollbackOnly();
            throw new InvalidOperationException("Consent entry requires its fresh exact operation owner.");
        }
        IUnitOfWorkScope? frame = null;
        try
        {
            frame = await BeginTransactionCoreAsync(witness.Mode, globalIdentity: false, cancellationToken);
            await SetTenantContextCoreAsync(context, null, false, cancellationToken, consentWitness: witness);
            if (!IsConsentSubjectOwner(witness)) throw new InvalidOperationException("Consent subject provenance closed.");
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
