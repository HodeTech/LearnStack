using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Tenancy.Application.FieldConsents;

/// <summary>Consumes only the exact live consent-subject witness on its physical owner.</summary>
public sealed class OwnConsentEntryAuthorization(IOperationInvocationContext invocations,
    IOperationPolicyCatalog catalog, IUnitOfWork unit, ITenantContext context)
{
    internal ConsentSubjectScopeWitness? For(object request, ConsentSubjectPurpose purpose)
    {
        if (request is not (WithdrawOwnMembershipFieldConsentCommand or ReadOwnWithdrawnFieldConsentReceiptQuery or ListOwnMembershipFieldConsentsQuery or ListOwnFieldConsentScopesQuery)
            || invocations.CurrentInvocation is not { IsActive: true, EntryKind: OperationEntryKind.Application, TrustedSource: null } invocation
            || !ReferenceEquals(invocation.Operation, request) || !ReferenceEquals(invocation.Context, context)
            || !catalog.TryGet(request.GetType(), out var policy) || !ReferenceEquals(policy, invocation.Policy)
            || policy.Kind != OperationPolicyKind.OwnConsentEntry
            || invocations.CurrentDecision is not { IsActive: true, Outcome: ProtectedOperationOutcome.Allowed, ConsentWitness: { } witness } decision
            || !ReferenceEquals(decision.Invocation, invocation) || !ReferenceEquals(witness.Invocation, invocation) || !witness.IsActive || witness.Purpose != purpose
            || unit is not IConsentSubjectUnitOfWork owner || !owner.IsConsentSubjectOwner(witness)
            || context.UserId != witness.Actor || context.TenantId != witness.TenantId || context.OrganizationId != witness.OrganizationId)
            return null;
        return witness;
    }
}
