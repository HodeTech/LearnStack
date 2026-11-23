using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Contracts.OperatorActs;

/// <summary>The one limit the operator administrator acts and their routes share.</summary>
public static class OperatorSubjectLimits
{
    /// <summary>The longest provider subject an operator may name.</summary>
    public const int MaximumProviderSubjectLength = 255;
}

/// <summary>
/// Creates one finite Role template in an operator-named Tenant (ADR-0102 § 2, § 6).
/// </summary>
/// <remarks>
/// Reachable only through the operator source. Naming the same role again with the same template is
/// the same act; a tenant that already has any Membership refuses, because a template is a
/// pristine-prefix operation.
/// </remarks>
public sealed record OperatorInitializeRoleCommand(Guid RoleId, BuiltInRoleKey BuiltInKey, PermissionScope CapabilityScope)
    : IRequest<Result<Guid>>;

/// <summary>
/// Creates the first tenant-wide administrator of a Tenant that has never had a Membership
/// (ADR-0102 § 5, § 7).
/// </summary>
/// <param name="MembershipId">The new Membership's id, supplied so a retry is exact.</param>
/// <param name="RoleId">The administrator Role template the operator initialized.</param>
/// <param name="ProviderSubject">
/// The person's <c>sub</c> at the identity authority's issuer. The person must already have signed in
/// once at the tenant's host, so a User exists; an email address is never an identifier.
/// </param>
public sealed record OperatorCreateFirstAdminCommand(
    Guid MembershipId,
    Guid RoleId,
    [property: PiiSensitive] string ProviderSubject) : IRequest<Result<Guid>>;

/// <summary>
/// Creates a replacement tenant-wide administrator for a Tenant that has Membership history but no
/// active administrator (ADR-0102 § 8).
/// </summary>
/// <remarks>
/// The only act that may add an administrator to a tenant with history, and only while none is active.
/// </remarks>
public sealed record OperatorRecoverAdminCommand(
    Guid MembershipId,
    Guid RoleId,
    [property: PiiSensitive] string ProviderSubject) : IRequest<Result<Guid>>;

/// <summary>
/// Fans a begun closure out to one participant group (<c>fan_out_closure</c>, ADR-0107 § 7): creates the closing
/// person's fan-out erasure in the group and enqueues its job, or adopts the erasure the group already has.
/// </summary>
/// <remarks>
/// Reachable only through the operator source, one admission per group, which the closure's Begin owner mints after its
/// transaction commits. The tenant is the executor's: the group's tenant, announced with
/// <paramref name="OrganizationId"/>, which names the same group so the handler can refuse a context the executor did
/// not build for it. The answer is how the group ended; a refusal is the failure result, which the fan-out reports as
/// a failed group.
/// </remarks>
/// <param name="SubjectUserId">The person whose User is Closing.</param>
/// <param name="OrganizationId">The group's organization, or <see langword="null"/> for the tenant-wide group.</param>
public sealed record FanOutClosureCommand(UserId SubjectUserId, OrganizationId? OrganizationId)
    : IRequest<Result<ClosureGroupOutcome>>;
