using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Infrastructure.Persistence;

/// <summary>
/// The retained own data-request scope's logical tenant setter (ADR-0106 § 9): a fresh owner in the witness's
/// mode that announces exactly the witness's group through the one private witness-aware core.
/// </summary>
public sealed partial class NpgsqlUnitOfWork
{
    private OwnDataRequestScopeWitness? _dataRequestWitness;

    public bool IsOwnDataRequestOwner(OwnDataRequestScopeWitness witness) =>
        ReferenceEquals(_dataRequestWitness, witness) && _transaction is not null
        && witness.IsBoundTo(this, _transaction, _generation) && _mode == witness.Mode
        && _depth == 1 && !_disposed && !_rollbackOnly && _tenantContextIssued;

    public async Task<IUnitOfWorkScope> BeginOwnDataRequestTransactionAsync(OwnDataRequestScopeWitness witness,
        ITenantContext context, CancellationToken cancellationToken = default)
    {
        JobApplicationSourceOwner.RefuseExternalEntry();
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireWriteAllowed();
        ArgumentNullException.ThrowIfNull(witness);
        if (HasActiveTransaction || IsGlobalIdentityTransaction || _dataRequestWitness is not null || _consentWitness is not null
            || _invitationWitness is not null || _selfDiscoveryWitness is not null || _openingSelfDiscovery
            || _jobSourceOwner is not null || JobApplicationSourceOwner.Current is not null || !witness.IsActive
            || !ReferenceEquals(witness.Invocation.Context, context))
        {
            MarkRollbackOnly();
            throw new InvalidOperationException("Own data request entry requires its fresh exact operation owner.");
        }
        IUnitOfWorkScope? frame = null;
        try
        {
            frame = await BeginTransactionCoreAsync(witness.Mode, globalIdentity: false, cancellationToken);
            await SetTenantContextCoreAsync(context, null, false, cancellationToken, dataRequestWitness: witness);
            if (!IsOwnDataRequestOwner(witness)) throw new InvalidOperationException("Own data request provenance closed.");
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
