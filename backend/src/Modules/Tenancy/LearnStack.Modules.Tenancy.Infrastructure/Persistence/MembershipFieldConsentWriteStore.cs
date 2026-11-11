using System.Data.Common;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>Append-only EF store; its plan is the actual retained lock object, not a reconstructed assertion.</summary>
public sealed class MembershipFieldConsentWriteStore(TenancyDbContext db, IIdentityMutationLocks locks, IUnitOfWork unit)
    : IMembershipFieldConsentWriteStore
{
    private IdentityMutationLockPlan? _plan;
    private MembershipFieldConsentTuple? _tuple;

    public async Task<MembershipFieldConsentAppendState> ReadForAppendAsync(IdentityMutationLockPlan plan,
        MembershipFieldConsentTuple tuple, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(tuple);
        RequirePlan(plan);
        if (_plan is not null && (!ReferenceEquals(_plan, plan) || _tuple != tuple))
            throw new InvalidOperationException("Consent store is bound to one tuple.");
        _plan = plan;
        _tuple = tuple;
        using var scope = TenancyAdministrationStorageScope.Enter(db);
        try { return await ReadStateAsync(tuple, cancellationToken); }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            unit.MarkRollbackOnly();
            throw new MembershipFieldConsentStorageException(Classify(exception));
        }
    }

    public async Task AddAsync(MembershipFieldConsent aggregate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        if (_plan is null || _tuple is null) throw new InvalidOperationException("Consent store has no retained plan.");
        RequirePlan(_plan);
        if (aggregate.TenantId != _plan.TenantId || aggregate.OrganizationId != db.CurrentOrganizationId
            || aggregate.MembershipId != _tuple.MembershipId || aggregate.SubjectUserId != _tuple.SubjectUserId
            || aggregate.DefinitionId != _tuple.DefinitionId || aggregate.DefinitionVersion != _tuple.DefinitionVersion
            || aggregate.PurposeKey != _tuple.PurposeKey)
            throw new InvalidOperationException("Consent aggregate is outside the bound tuple.");
        using var scope = TenancyAdministrationStorageScope.Enter(db);
        try
        {
            var state = await ReadStateAsync(_tuple, cancellationToken);
            var decision = MembershipFieldConsentTransition.Decide(state.Current, state.LatestGrant,
                aggregate.PredecessorSequence, aggregate.PredecessorId, aggregate.State, DateTimeOffset.MaxValue);
            if (decision != MembershipFieldConsentTransitionDecision.Append)
                throw new MembershipFieldConsentStorageException(decision switch
                {
                    MembershipFieldConsentTransitionDecision.Stale => MembershipFieldConsentStorageFailure.Stale,
                    MembershipFieldConsentTransitionDecision.GrantCooldown => MembershipFieldConsentStorageFailure.Cooldown,
                    _ => MembershipFieldConsentStorageFailure.State,
                });
            RequirePlan(_plan);
            db.MembershipFieldConsents.Add(aggregate);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            unit.MarkRollbackOnly();
            throw new MembershipFieldConsentStorageException(Classify(exception));
        }
        catch
        {
            unit.MarkRollbackOnly();
            throw;
        }
    }

    public Task UpdateAsync(MembershipFieldConsent aggregate, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Consent evidence is immutable.");

    private void RequirePlan(IdentityMutationLockPlan plan)
    {
        if (plan.TenantMode != IdentityTenantLockMode.Exclusive || plan.TenantId != db.CurrentTenantId)
            throw new InvalidOperationException("Consent requires the exclusive tenant plan.");
        locks.RequireCurrent(plan);
    }

    private async Task<MembershipFieldConsentAppendState> ReadStateAsync(MembershipFieldConsentTuple tuple,
        CancellationToken cancellationToken)
    {
        var query = db.MembershipFieldConsents.AsNoTracking().Where(row =>
            row.OrganizationId == db.CurrentOrganizationId && row.MembershipId == tuple.MembershipId
            && row.SubjectUserId == tuple.SubjectUserId && row.DefinitionId == tuple.DefinitionId
            && row.DefinitionVersion == tuple.DefinitionVersion && row.PurposeKey == tuple.PurposeKey);
        var current = await query.OrderByDescending(row => row.Sequence).FirstOrDefaultAsync(cancellationToken);
        var grant = current?.State == MembershipFieldConsentState.Granted ? current
            : await query.Where(row => row.State == MembershipFieldConsentState.Granted)
                .OrderByDescending(row => row.Sequence).FirstOrDefaultAsync(cancellationToken);
        return new MembershipFieldConsentAppendState(current, grant);
    }

    private static bool IsProviderFailure(Exception exception) => Provider(exception) is not null;

    private static Exception? Provider(Exception exception) => exception switch
    {
        DbException or TimeoutException => exception,
        DbUpdateException { InnerException: DbException or TimeoutException } update => update.InnerException,
        InvalidOperationException { InnerException: DbException or TimeoutException } wrapped => wrapped.InnerException,
        InvalidOperationException { InnerException: DbUpdateException { InnerException: DbException or TimeoutException } update } => update.InnerException,
        _ => null,
    };

    private static MembershipFieldConsentStorageFailure Classify(Exception exception) => Provider(exception) switch
    {
        PostgresException { SqlState: "P0301" or "P0321" } => MembershipFieldConsentStorageFailure.Plan,
        PostgresException { SqlState: "P0322" } => MembershipFieldConsentStorageFailure.Stale,
        PostgresException { SqlState: "P0323" } => MembershipFieldConsentStorageFailure.Cooldown,
        PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure
            or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.LockNotAvailable
        } => MembershipFieldConsentStorageFailure.Conflict,
        PostgresException { SqlState: PostgresErrorCodes.CheckViolation or "P0320" } => MembershipFieldConsentStorageFailure.State,
        _ => MembershipFieldConsentStorageFailure.Provider,
    };
}
