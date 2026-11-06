using System.Security.Cryptography;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Invitations;

public sealed class InvitationEntryAuthorization(IOperationInvocationContext invocations, IOperationPolicyCatalog catalog,
    IUnitOfWork unit, ITenantContext context, IIdentityScopeLifecycleReader lifecycle,
    IInvitationMessageContextReader labels, IInvitationEntryOriginPolicy origins)
{
    internal InvitationEntryScopeWitness? For(object request, Guid id, string code)
    {
        if (request is not (InspectInvitationQuery or AcceptInvitationCommand or GetOwnInvitationResultQuery)
            || invocations.CurrentInvocation is not { IsActive: true, EntryKind: OperationEntryKind.Application } invocation
            || !ReferenceEquals(invocation.Operation, request) || !ReferenceEquals(invocation.Context, context)
            || !catalog.TryGet(request.GetType(), out var policy) || !ReferenceEquals(policy, invocation.Policy)
            || policy.Kind != OperationPolicyKind.InvitationEntry
            || invocations.CurrentDecision is not { IsActive: true, Outcome: ProtectedOperationOutcome.Allowed, InvitationWitness: { } witness }
            || !ReferenceEquals(witness.Invocation, invocation) || witness.Offer.InvitationId != id
            || unit is not IInvitationEntryUnitOfWork owner || !owner.IsInvitationEntryOwner(witness)
            || !InvitationVerifier.TryDigest(id, code, out var digest)) return null;
        try { return CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(witness.VerifierDigest)) ? witness : null; }
        finally { CryptographicOperations.ZeroMemory(digest); }
    }
    internal async Task<Result<InvitationMessageContext>> CurrentScopeAsync(Invitation offer, InvitationEntryScopeWitness witness,
        CancellationToken cancellationToken)
    {
        var state = await lifecycle.ReadAsync(context.TenantId, context.OrganizationId, cancellationToken);
        if (!state.TenantExistsAndNotDeleted || !state.TenantAllowsOrdinaryWork
            || context.OrganizationId is not null && (!state.OrganizationBelongsToTenant || !state.OrganizationAllowsOrdinaryWork))
            return Fail<InvitationMessageContext>("lockey_not_found");
        var current = await labels.ReadEntryAsync(witness.Host, cancellationToken);
        if (current.IsFailure) return current;
        return witness.IsActive && offer.EntryOrigin == origins.ForHost(current.Value.EntryHost)
            ? current : Fail<InvitationMessageContext>("lockey_not_found");
    }
    internal bool CanDelegate(Invitation offer, Role role, IdentityAuthorizationResult facts)
    {
        if (!facts.Covers || facts.Actor != offer.InviterUserId || facts.TenantId != context.TenantId
            || facts.OrganizationId != context.OrganizationId || role.IsDeleted || role.DeletedBy is not null
            || role.TenantId != context.TenantId || role.Id != offer.RoleId || role.Version != offer.PinnedRoleVersion
            || role.PermissionScope != (context.OrganizationId is null ? PermissionScope.Tenant : PermissionScope.Organization)) return false;
        var ceiling = facts.Memberships.Where(member => member.OrganizationId is null || member.OrganizationId == context.OrganizationId)
            .SelectMany(member => member.Roles.Where(held => held.Scope == PermissionScope.Tenant && member.OrganizationId is null
                || held.Scope == PermissionScope.Organization && context.OrganizationId is not null && member.OrganizationId == context.OrganizationId))
            .SelectMany(held => held.Grants).Where(pair => IdentityPolicyEvaluation.IsRegistered(pair, catalog)).ToHashSet();
        return ceiling.Any(pair => pair.Key == IdentityFoundationManifest.MembershipAdmin
                && (pair.Scope == PermissionScope.Tenant || context.OrganizationId is not null && pair.Scope == PermissionScope.Organization))
            && role.Grants.All(grant =>
            {
                var pair = new PermissionDefinition(grant.PermissionScope, grant.PermissionKey);
                return pair.Scope == role.PermissionScope && IdentityPolicyEvaluation.IsRegistered(pair, catalog)
                    && ceiling.Any(held => held.Key == pair.Key && (held.Scope == pair.Scope
                        || pair.Scope == PermissionScope.Organization && held.Scope == PermissionScope.Tenant));
            });
    }
    internal static Result<T> Fail<T>(string key) => InvitationAdministrationAuthorization.Fail<T>(key);
    internal static bool SameOffer(Invitation offer, InvitationEntryScopeWitness witness) =>
        offer.Id.Value == witness.Offer.InvitationId && offer.TenantId == witness.Offer.TenantId
        && offer.OrganizationId == witness.Offer.OrganizationId && offer.GenerationId.Value == witness.Offer.GenerationId
        && offer.Generation == witness.Offer.Generation && offer.Version == witness.Offer.Version;
    internal static bool MatchesRecipient(CurrentInvitationRecipientEvidence evidence, InvitationEntryScopeWitness witness,
        Invitation offer, out string normalized) => InvitationMailbox.TryNormalize(evidence.Email, out normalized)
        && evidence.IsFor(witness.Invocation) && normalized == offer.RecipientEmail;
}

public sealed class InspectInvitationQueryHandler(IInvitationAcceptanceReadStore reads, IRoleReadStore roles,
    InvitationEntryAuthorization authorization, ICurrentInvitationRecipientReader recipient)
    : IRequestHandler<InspectInvitationQuery, Result<InvitationInspection>>
{
    public async Task<Result<InvitationInspection>> Handle(InspectInvitationQuery request, CancellationToken cancellationToken)
    {
        var witness = authorization.For(request, request.InvitationId, request.Code);
        if (witness is null || witness.Offer.Accepted) return InvitationEntryAuthorization.Fail<InvitationInspection>("lockey_not_found");
        var offer = await reads.ReadOfferAsync(InvitationId.From(request.InvitationId), cancellationToken);
        if (offer is null || !InvitationEntryAuthorization.SameOffer(offer, witness) || !offer.MatchesPendingVerifier(witness.VerifierDigest)
            || offer.IsExpired(await reads.DatabaseNowAsync(cancellationToken))) return InvitationEntryAuthorization.Fail<InvitationInspection>("lockey_not_found");
        var observed = await recipient.ReadAsync(witness.Invocation, cancellationToken);
        if (observed.IsFailure) return Result<InvitationInspection>.Fail(observed.Error);
        if (!InvitationEntryAuthorization.MatchesRecipient(observed.Value, witness, offer, out var normalized))
            return InvitationEntryAuthorization.Fail<InvitationInspection>("lockey_forbidden");
        var scope = await authorization.CurrentScopeAsync(offer, witness, cancellationToken);
        if (scope.IsFailure) return Result<InvitationInspection>.Fail(scope.Error);
        var role = await roles.ReadAsync(offer.RoleId, cancellationToken);
        if (role is null || role.IsDeleted || role.Version != offer.PinnedRoleVersion
            || role.PermissionScope != (offer.OrganizationId is null ? PermissionScope.Tenant : PermissionScope.Organization)
            || !observed.Value.IsFor(witness.Invocation)) return InvitationEntryAuthorization.Fail<InvitationInspection>("lockey_not_found");
        return Result.Ok(new InvitationInspection(offer.Id.Value, offer.Version, offer.ExpiresAt, scope.Value.TenantLabel,
            scope.Value.OrganizationLabel, role.Id.Value, role.BuiltInKey.ToString(), role.PermissionScope.ToString(), normalized));
    }
}

/// <summary>The one named Identity two-root writer; both visible ports share the central owner and plural MUST audit.</summary>
public sealed class AcceptInvitationCommandHandler(IInvitationWriteStore invitations, IMembershipWriteStore memberships,
    IInvitationAcceptanceReadStore reads, IRoleReadStore roles, IInvitationDelegationReader delegation,
    IInvitationEntryLookup lookup, InvitationEntryAuthorization authorization, ICurrentInvitationRecipientReader recipient,
    IIdentityMutationLocks locks, IAuditSubject audit, IGuidFactory ids)
    : IRequestHandler<AcceptInvitationCommand, Result<InvitationAcceptanceResult>>
{
    public async Task<Result<InvitationAcceptanceResult>> Handle(AcceptInvitationCommand request, CancellationToken cancellationToken)
    {
        var witness = authorization.For(request, request.InvitationId, request.Code);
        if (witness is null || witness.Offer.Accepted) return Fail("lockey_not_found");
        if (request.ExpectedVersion < 0) return Fail("lockey_validation_failed");
        try
        {
            var before = await reads.ReadOfferAsync(InvitationId.From(request.InvitationId), cancellationToken);
            if (before is null || before.Status != InvitationStatus.Pending || !before.MatchesPendingVerifier(witness.VerifierDigest)) return Fail("lockey_not_found");
            if (!InvitationEntryAuthorization.SameOffer(before, witness) || before.Version != request.ExpectedVersion) return Fail("lockey_concurrency_conflict");
            if (before.IsExpired(await reads.DatabaseNowAsync(cancellationToken))) return Fail("lockey_not_found");
            // No User/Tenant/row lock has been acquired during this fresh provider observation.
            var observed = await recipient.ReadAsync(witness.Invocation, cancellationToken);
            if (observed.IsFailure) return Result<InvitationAcceptanceResult>.Fail(observed.Error);
            if (!InvitationEntryAuthorization.MatchesRecipient(observed.Value, witness, before, out _)) return Fail("lockey_forbidden");
            var memberBefore = await reads.ReadMemberAsync(witness.Offer.Actor, cancellationToken);
            var roleBefore = await roles.ReadAsync(before.RoleId, cancellationToken);
            if (roleBefore is null || roleBefore.IsDeleted) return Fail("lockey_not_found");
            var plan = Plan(before, memberBefore, roleBefore, witness.Offer.Actor);
            await locks.AcquireAsync(plan, cancellationToken);
            await roles.LockAsync(before.RoleId, cancellationToken);
            await reads.LockInvitationAsync(before.Id, cancellationToken);
            // Discover the current exact member after the User fence, then retain its row too.
            var currentMember = await reads.ReadMemberAsync(witness.Offer.Actor, cancellationToken);
            if (currentMember is not null) await memberships.LockAsync(currentMember.Id, cancellationToken);
            var postWait = await lookup.RecheckAsync(witness, cancellationToken);
            if (postWait is null) return Fail("lockey_concurrency_conflict");
            if (postWait.Actor != witness.Offer.Actor || !postWait.InviterGloballyAdmitted) return Fail("lockey_not_found");
            if (postWait.Accepted || postWait.Version != before.Version || postWait.GenerationId != before.GenerationId.Value) return Fail("lockey_concurrency_conflict");
            var role = await roles.ReadAsync(before.RoleId, cancellationToken);
            if (role is null || role.IsDeleted || role.Version != before.PinnedRoleVersion) return Fail("lockey_concurrency_conflict");
            var facts = await delegation.ReadAsync(before.InviterUserId, cancellationToken);
            if (facts.Outcome == IdentityAuthorizationOutcome.Unavailable) return Fail("lockey_dependency_unavailable");
            if (!authorization.CanDelegate(before, role, facts)) return Fail("lockey_forbidden");
            var scope = await authorization.CurrentScopeAsync(before, witness, cancellationToken);
            if (scope.IsFailure) return Result<InvitationAcceptanceResult>.Fail(scope.Error);
            var graph = await reads.ReadFinalAsync(before.Id, witness.Offer.Actor, cancellationToken);
            var target = graph.Invitation;
            var member = graph.Membership;
            if (target is null || target.Status != InvitationStatus.Pending || !InvitationEntryAuthorization.SameOffer(target, witness)
                || target.Version != request.ExpectedVersion || !target.MatchesPendingVerifier(witness.VerifierDigest)
                || member?.Id != memberBefore?.Id || member?.Version != memberBefore?.Version
                || !SamePlan(plan, Plan(target, member, role, witness.Offer.Actor))) return Fail("lockey_concurrency_conflict");
            if (member?.Status == MembershipStatus.Suspended) return Fail("lockey_concurrency_conflict");
            var at = await reads.DatabaseNowAsync(cancellationToken);
            if (target.IsExpired(at)) return Fail("lockey_not_found");
            if (!InvitationEntryAuthorization.MatchesRecipient(observed.Value, witness, target, out _)) return Fail("lockey_dependency_unavailable");
            locks.RequireCurrent(plan);
            var disposition = member is null ? InvitationAcceptanceDisposition.Created : InvitationAcceptanceDisposition.AlreadyMember;
            if (member is null)
            {
                var created = Membership.Create(MembershipId.From(ids.NewUuidV7()), target.TenantId, target.OrganizationId,
                    witness.Offer.Actor, role, at, witness.Offer.Actor);
                if (created.IsFailure) return Result<InvitationAcceptanceResult>.Fail(created.Error);
                member = created.Value;
                audit.Designate(member);
                // Persist the contained graph first so the Invitation trigger can observe its exact result.
                await memberships.AddAsync(member, cancellationToken);
            }
            else audit.Designate(member);
            audit.Designate(target);
            var accepted = target.Accept(member, disposition, witness.VerifierDigest, at, witness.Offer.Actor);
            if (accepted.IsFailure) return Fail("lockey_concurrency_conflict");
            await invitations.UpdateAsync(target, cancellationToken);
            await memberships.ValidateGraphAsync(cancellationToken);
            return Result.Ok(InvitationEntryResultProjection.From(target));
        }
        catch (IdentityFoundationStorageException failure)
        { return Fail(failure.Failure == IdentityFoundationStorageFailure.Conflict ? "lockey_concurrency_conflict" : "lockey_dependency_unavailable"); }
        catch (Exception failure) when (failure is IdentityMutationLockException or IdentityScopeLifecycleReadException)
        { return Fail("lockey_dependency_unavailable"); }
    }
    private static IdentityMutationLockPlan Plan(Invitation offer, Membership? member, Role role, UserId actor) =>
        new([actor, offer.InviterUserId], offer.TenantId, member is null && role.PermissionScope == PermissionScope.Tenant
            && role.BuiltInKey == BuiltInRoleKey.TenantAdmin ? IdentityTenantLockMode.Exclusive : IdentityTenantLockMode.Shared);
    private static bool SamePlan(IdentityMutationLockPlan first, IdentityMutationLockPlan second) => first.TenantId == second.TenantId
        && first.TenantMode == second.TenantMode && first.UserIds.SequenceEqual(second.UserIds);
    private static Result<InvitationAcceptanceResult> Fail(string key) => InvitationEntryAuthorization.Fail<InvitationAcceptanceResult>(key);
}

public sealed class GetOwnInvitationResultQueryHandler(IInvitationAcceptanceReadStore reads, InvitationEntryAuthorization authorization)
    : IRequestHandler<GetOwnInvitationResultQuery, Result<InvitationAcceptanceResult>>
{
    public async Task<Result<InvitationAcceptanceResult>> Handle(GetOwnInvitationResultQuery request, CancellationToken cancellationToken)
    {
        var witness = authorization.For(request, request.InvitationId, request.Code);
        if (witness is null || !witness.Offer.Accepted) return InvitationEntryAuthorization.Fail<InvitationAcceptanceResult>("lockey_not_found");
        var offer = await reads.ReadOfferAsync(InvitationId.From(request.InvitationId), cancellationToken);
        if (offer is null || offer.AcceptedBy != witness.Offer.Actor || !offer.MatchesConsumedVerifier(witness.VerifierDigest)
            || offer.ResultMembershipId is not { } memberId || memberId.Value != witness.Offer.ResultMembershipId
            || await reads.ReadResultAsync(memberId, witness.Offer.Actor, cancellationToken) is null || !witness.IsActive)
            return InvitationEntryAuthorization.Fail<InvitationAcceptanceResult>("lockey_not_found");
        var scope = await authorization.CurrentScopeAsync(offer, witness, cancellationToken);
        if (scope.IsFailure) return Result<InvitationAcceptanceResult>.Fail(scope.Error);
        return Result.Ok(InvitationEntryResultProjection.From(offer));
    }
}

internal static class InvitationEntryResultProjection
{
    internal static InvitationAcceptanceResult From(Invitation offer) => new(offer.Id.Value, offer.Version,
        offer.ResultMembershipId!.Value.Value, offer.AcceptanceDisposition == InvitationAcceptanceDisposition.Created ? "created" : "already_member",
        offer.TenantId, offer.OrganizationId);
}
