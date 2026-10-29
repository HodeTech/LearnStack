using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using static LearnStack.Modules.Customization.Application.Seeding.SeedCustomizationEvidence;

namespace LearnStack.Modules.Customization.Application.Seeding;

/// <summary>Closed prerequisite on the caller's fenced narrow owner; no dependency on another writer.</summary>
public sealed class SeedCustomizationPrerequisiteReader(ISeedCustomizationContinuationSource source,
    ISeedCustomizationState state, IIdentityMutationLocks locks, IOperationInvocationContext execution,
    ITenantContext context, IUnitOfWork unit) : ISeedCustomizationPrerequisiteReader
{
    public async Task<bool> IsCompleteAsync(IdentityMutationLockPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var invocation = execution.CurrentInvocation;
        if (plan.TenantId != context.TenantId || !plan.UserIds.IsEmpty
            || plan.TenantMode != IdentityTenantLockMode.Exclusive || !HasOwner() || invocation is not { IsActive: true, EntryKind: OperationEntryKind.Application }
            || !ReferenceEquals(invocation.Context, context) || invocation.Policy.Kind != OperationPolicyKind.FiniteTrusted
            || invocation.Policy.RequestType != invocation.Operation.GetType()
            || invocation.TrustedSource is not { SourceKind: AuthorizationSourceKind.Seed } trusted
            || !trusted.Matches(invocation.Operation.GetType(), context)
            || invocation.Policy.Id is not ("education.seed.create_course" or "education.seed.add_course_translation"
                or "education.seed.publish_course" or "education.seed.create_lesson" or "education.seed.add_lesson_translation"
                or "education.seed.publish_lesson" or "tenancy.seed.set_branding")
            || !invocation.Policy.AllowedSources.Contains(AuthorizationSourceKind.Seed)
            || source.ForTenant(context.TenantId) is not { } declaration || declaration.TenantId != context.TenantId) return false;
        locks.RequireCurrent(plan);
        var transaction = unit.Transaction;
        var announcement = unit.GetTenantContextAnnouncement(transaction);
        var content = await state.ReadContentTypesAsync(cancellationToken);
        if (!invocation.IsActive || !ReferenceEquals(invocation, execution.CurrentInvocation) || !HasOwner()
            || !ReferenceEquals(unit.Transaction, transaction) || unit.GetTenantContextAnnouncement(transaction) != announcement
            || !ReferenceEquals(source.ForTenant(context.TenantId), declaration)) return false;
        var taxonomies = await state.ReadTaxonomiesAsync(cancellationToken);
        locks.RequireCurrent(plan);
        return invocation.IsActive && ReferenceEquals(invocation, execution.CurrentInvocation) && HasOwner()
            && ReferenceEquals(unit.Transaction, transaction) && unit.GetTenantContextAnnouncement(transaction) == announcement
            && ReferenceEquals(source.ForTenant(context.TenantId), declaration)
            && content.Count == 2 && taxonomies.Count == 2
            && Content(content, declaration.BuiltInContentType, declaration.TenantId, active: true)
            && Content(content, declaration.ContentType, declaration.TenantId, active: true)
            && Taxonomy(taxonomies, declaration.BuiltInTaxonomy, declaration.TenantId, active: true)
            && Taxonomy(taxonomies, declaration.Taxonomy, declaration.TenantId, active: true);
    }

    private bool HasOwner() => context.IsResolved && context.HostScope is null && unit.HasActiveTransaction && !unit.IsRollbackOnly
        && unit.Mode == TransactionMode.ReadWrite && unit is not IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true }
        && unit.GetTenantContextAnnouncement(unit.Transaction) is { Generation: > 0 } announcement
        && announcement.TenantId == context.TenantId && announcement.OrganizationId == context.OrganizationId
        && unit is not IOperationReadScopeView { IsTenantWideRead: true };
}
