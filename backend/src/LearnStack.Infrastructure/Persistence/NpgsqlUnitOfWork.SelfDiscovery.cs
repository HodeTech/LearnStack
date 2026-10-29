using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Infrastructure.Persistence;

public sealed partial class NpgsqlUnitOfWork
{
    private OwnMembershipScopeWitness? _selfDiscoveryWitness;
    private bool _openingSelfDiscovery;

    public bool IsSelfDiscoveryOwner(OwnMembershipScopeWitness witness) =>
        ReferenceEquals(_selfDiscoveryWitness, witness) && witness.IsBoundTo(this)
        && _mode == TransactionMode.ReadOnly && _transaction is not null
        && !_disposed && !_rollbackOnly && _tenantContextIssued;

    public async Task<IUnitOfWorkScope> BeginSelfDiscoveryTransactionAsync(OwnMembershipScopeWitness witness,
        CancellationToken cancellationToken = default)
    {
        JobApplicationSourceOwner.RefuseExternalEntry();
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireWriteAllowed();
        ArgumentNullException.ThrowIfNull(witness);
        if (HasActiveTransaction || IsGlobalIdentityTransaction || _selfDiscoveryWitness is not null
            || _openingSelfDiscovery || _jobSourceOwner is not null || JobApplicationSourceOwner.Current is not null
            || !witness.TryBindTo(this))
            throw new InvalidOperationException("Self discovery requires a fresh captured-scope owner.");
        _openingSelfDiscovery = true;
        IUnitOfWorkScope? frame = null;
        try
        {
            frame = await BeginTransactionCoreAsync(TransactionMode.ReadOnly, globalIdentity: false, cancellationToken);
            _selfDiscoveryWitness = witness;
            await ExecuteAsync("""
                SELECT set_config('app.tenant_id', @tenant, true),
                    set_config('app.organization_id', @organization, true),
                    set_config('app.scope', '', true), set_config('app.resolving_host', '', true),
                    set_config('app.identity_authority_id', '', true), set_config('app.identity_subject_key', '', true)
                """, cancellationToken, ("tenant", witness.TenantId.Value.ToString("D")),
                ("organization", witness.OrganizationId?.Value.ToString("D") ?? string.Empty));
            if (!witness.IsBoundTo(this)) throw new InvalidOperationException("Self discovery provenance is closed.");
            _tenantContextIssued = true;
            _tenantContextAnnouncement = new TenantContextAnnouncement(_generation, witness.TenantId, witness.OrganizationId);
            return frame;
        }
        catch
        {
            MarkRollbackOnly();
            try { await RollbackCoreAsync(); }
            catch { LogSetupCleanupFailure(_logger, null); }
            if (frame is not null) await frame.DisposeAsync();
            throw;
        }
        finally { _openingSelfDiscovery = false; }
    }
}
