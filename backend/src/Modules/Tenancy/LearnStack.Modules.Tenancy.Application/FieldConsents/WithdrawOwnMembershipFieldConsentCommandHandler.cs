using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.FieldConsents;

public sealed class WithdrawOwnMembershipFieldConsentCommandHandler(OwnConsentEntryAuthorization authorization,
    IOwnMembershipFieldConsentHistory history, IMembershipFieldConsentWriteStore store,
    IIdentityMutationLocks locks, IAuditSubject audit, IClock clock, IGuidFactory ids)
    : IRequestHandler<WithdrawOwnMembershipFieldConsentCommand, Result<MembershipFieldConsentReceipt>>
{
    public async Task<Result<MembershipFieldConsentReceipt>> Handle(WithdrawOwnMembershipFieldConsentCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Confirmed || request.EvidenceId == Guid.Empty || request.EvidenceId.Version != 7 || request.ExpectedEvidenceId == Guid.Empty || request.ExpectedEvidenceId.Version != 7
            || request.ExpectedSequence is < 1 or > MembershipFieldConsentTransition.MaximumSequence)
            return Fail("lockey_validation_failed");
        var witness = authorization.For(request, ConsentSubjectPurpose.Withdraw);
        if (witness?.MembershipId is not { } member) return Fail("lockey_forbidden");
        try
        {
            var plan = new IdentityMutationLockPlan([], witness.TenantId, IdentityTenantLockMode.Exclusive);
            await locks.AcquireAsync(plan, cancellationToken);
            if (!ReferenceEquals(authorization.For(request, ConsentSubjectPurpose.Withdraw), witness)) return Fail("lockey_forbidden");
            locks.RequireCurrent(plan);
            var retained = await history.ReadEvidenceAsync(witness.Actor, member, request.EvidenceId, cancellationToken);
            if (retained is null || retained.MembershipId != member) return Fail("lockey_not_found");
            var tuple = new MembershipFieldConsentTuple(member, witness.Actor, retained.DefinitionId,
                retained.DefinitionVersion, retained.PurposeKey);
            var state = await store.ReadForAppendAsync(plan, tuple, cancellationToken);
            var expected = MembershipFieldConsentId.From(request.ExpectedEvidenceId);
            var decision = MembershipFieldConsentTransition.Decide(state.Current, state.LatestGrant, request.ExpectedSequence,
                expected, MembershipFieldConsentState.Withdrawn, DateTimeOffset.MaxValue);
            if (decision == MembershipFieldConsentTransitionDecision.Unchanged)
            {
                if (!ReferenceEquals(authorization.For(request, ConsentSubjectPurpose.Withdraw), witness)) return Fail("lockey_forbidden");
                audit.Designate(state.Current!);
                return Result.Ok(GrantOwnMembershipFieldConsentCommandHandler.Receipt(state.Current!));
            }
            if (decision != MembershipFieldConsentTransitionDecision.Append || state.Current is null)
                return Fail(decision == MembershipFieldConsentTransitionDecision.Stale ? "lockey_concurrency_conflict" : "lockey_business_rule_violation");
            if (!ReferenceEquals(authorization.For(request, ConsentSubjectPurpose.Withdraw), witness)) return Fail("lockey_forbidden");
            locks.RequireCurrent(plan);
            var current = state.Current;
            var withdrawal = MembershipFieldConsent.Create(MembershipFieldConsentId.From(ids.NewUuidV7()), witness.TenantId,
                witness.OrganizationId, member, witness.Actor, witness.Actor, current.DefinitionId, current.DefinitionVersion,
                current.PurposeKey, current.NoticeLocale, current.NoticeHash, request.ExpectedSequence + 1,
                MembershipFieldConsentState.Withdrawn, expected, request.ExpectedSequence, clock.UtcNow);
            audit.Designate(withdrawal);
            await store.AddAsync(withdrawal, cancellationToken);
            return Result.Ok(GrantOwnMembershipFieldConsentCommandHandler.Receipt(withdrawal));
        }
        catch (MembershipFieldConsentStorageException failure) { return Fail(GrantOwnMembershipFieldConsentCommandHandler.Map(failure.Failure)); }
        catch (TenancyAdministrationStorageException) { return Fail("lockey_dependency_unavailable"); }
        catch (IdentityMutationLockException) { return Fail("lockey_dependency_unavailable"); }
    }
    private static Result<MembershipFieldConsentReceipt> Fail(string code) => OwnFieldConsentAuthorization.Failure<MembershipFieldConsentReceipt>(code);
}
