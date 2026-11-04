using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>One retained-history Boolean, on the caller's announced ordinary owner.</summary>
public sealed class SeedIdentityStageReader(IdentityDbContext db, IUnitOfWork unit,
    IIdentityMutationLocks locks, IOperationInvocationContext execution, ITenantContext context)
    : ISeedIdentityStageReader
{
    public async Task<bool> HasNoRoleHistoryAsync(IdentityMutationLockPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var invocation = execution.CurrentInvocation;
        if (!Allowed(invocation) || plan.TenantId != context.TenantId || !plan.UserIds.IsEmpty
            || plan.TenantMode != IdentityTenantLockMode.Exclusive) return false;
        locks.RequireCurrent(plan);
        ContextualIdentityStorageControl.EnrollCurrentTenantOwner(db, unit);
        var transaction = unit.Transaction;
        var announcement = unit.GetTenantContextAnnouncement(transaction);
        if (announcement is not { Generation: > 0 } || announcement.TenantId != context.TenantId) return false;
        // No lifecycle or fixed-ID predicate: deleted and unknown Role identities
        // also prove that the later Identity stage has begun. EF and RLS stay on.
        var present = await ContextualIdentityStorageControl.ReadAsync(() => db.Roles.AsNoTracking().AnyAsync(cancellationToken));
        locks.RequireCurrent(plan);
        return !present && Allowed(invocation) && ReferenceEquals(execution.CurrentInvocation, invocation)
            && ReferenceEquals(unit.Transaction, transaction) && unit.GetTenantContextAnnouncement(transaction) == announcement;
    }

    private bool Allowed(OperationInvocation? invocation) => context.IsResolved && context.HostScope is null
        && unit.HasActiveTransaction && !unit.IsRollbackOnly && unit.Mode == TransactionMode.ReadWrite
        && unit is not IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true }
        && invocation is { IsActive: true, EntryKind: OperationEntryKind.Application }
        && ReferenceEquals(invocation.Context, context) && invocation.Policy.Kind == OperationPolicyKind.FiniteTrusted
        && invocation.Policy.RequestType == invocation.Operation.GetType()
        && invocation.Policy.AllowedSources.Contains(AuthorizationSourceKind.Seed)
        && invocation.TrustedSource is { SourceKind: AuthorizationSourceKind.Seed } source
        && source.Matches(invocation.Operation.GetType(), context)
        && invocation.Policy.Id is "customization.default.initialize_content_type" or "customization.default.initialize_taxonomy"
            or "customization.seed.register_content_type" or "customization.seed.publish_content_type"
            or "customization.seed.register_taxonomy" or "customization.seed.publish_taxonomy"
            or "education.seed.create_course" or "education.seed.add_course_translation" or "education.seed.publish_course"
            or "education.seed.create_lesson" or "education.seed.add_lesson_translation" or "education.seed.publish_lesson";
}
