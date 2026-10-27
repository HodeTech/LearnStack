using System.Data;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Modules.Identity.Application.Authorization;

/// <summary>Committed facts from separate module statements on one isolated child, closed before continuation.</summary>
public sealed class CommittedIdentityAuthorizationReader(
    IIdentityAuthorizationSessionFactory sessions, IIdentityScopeLifecycleReader lifecycle) : IIdentityAuthorizationReader
{
    public async Task<IdentityAuthorizationResult> ReadAsync(AuthorizationCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        IIdentityAuthorizationSession? owner = null;
        var result = Empty(candidate, IdentityAuthorizationOutcome.Unavailable);
        var cleaned = true;
        try
        {
            if (candidate.IsActive)
            {
                owner = await sessions.OpenAsync(candidate, cancellationToken);
                if (owner is not null)
                {
                    var transaction = owner.Unit.Transaction;
                    var announced = RequireAnnouncement(owner.Unit, candidate, transaction);
                    var memberships = await owner.ReadMembershipsAsync(owner.DeadlineToken);
                    RequireSameAnnouncement(owner.Unit, candidate, transaction, announced);
                    // Tenancy owns this separate statement. READ COMMITTED gives each statement its own committed view.
                    var state = await lifecycle.ReadAuthorizationAsync(owner.Unit, candidate, owner.DeadlineToken);
                    RequireSameAnnouncement(owner.Unit, candidate, transaction, announced);
                    var covers = memberships.Length > 0 && state.TenantExistsAndNotDeleted && state.TenantAllowsOrdinaryWork
                        && (candidate.OrganizationId is null || (state.OrganizationBelongsToTenant && state.OrganizationAllowsOrdinaryWork));
                    result = covers
                        ? new(IdentityAuthorizationOutcome.Covered, candidate.Actor, candidate.TenantId, candidate.OrganizationId, memberships)
                        : Empty(candidate, IdentityAuthorizationOutcome.NotCovered);
                    await owner.CompleteAsync();
                }
            }
        }
        catch (IdentityScopeLifecycleReadException exception)
        {
            if (exception.CleanupFailed) owner?.MarkCleanupFailed();
            result = Empty(candidate, IdentityAuthorizationOutcome.Unavailable);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            // Storage, internal deadline and owner faults expose no provider payload or false noncoverage.
            result = Empty(candidate, IdentityAuthorizationOutcome.Unavailable);
        }
        finally
        {
            if (owner is not null)
            {
                try { await owner.DisposeAsync(); }
                catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException)) { cleaned = false; }
            }
        }

        // Owned cleanup finishes (or retains its slot) before request cancellation leaves the adapter.
        cancellationToken.ThrowIfCancellationRequested();
        return cleaned && candidate.IsActive && owner?.DeadlineToken.IsCancellationRequested != true
            ? result : Empty(candidate, IdentityAuthorizationOutcome.Unavailable);
    }

    private static TenantContextAnnouncement RequireAnnouncement(IUnitOfWork unit, AuthorizationCandidate candidate,
        System.Data.Common.DbTransaction? transaction)
    {
        if (!candidate.IsActive || !unit.HasActiveTransaction || unit.Mode != TransactionMode.ReadOnly || unit.IsRollbackOnly
            || transaction is not { IsolationLevel: IsolationLevel.ReadCommitted }
            || unit.GetTenantContextAnnouncement(transaction) is not { Generation: > 0 } announced
            || announced.TenantId != candidate.TenantId || announced.OrganizationId != candidate.OrganizationId)
            throw new InfrastructureException("Identity authorization is unavailable.");
        return announced;
    }

    private static void RequireSameAnnouncement(IUnitOfWork unit, AuthorizationCandidate candidate,
        System.Data.Common.DbTransaction? transaction, TenantContextAnnouncement announced)
    {
        if (RequireAnnouncement(unit, candidate, transaction) != announced)
            throw new InfrastructureException("Identity authorization is unavailable.");
    }

    private static IdentityAuthorizationResult Empty(AuthorizationCandidate candidate, IdentityAuthorizationOutcome outcome) =>
        new(outcome, candidate.Actor, candidate.TenantId, candidate.OrganizationId, []);
}
