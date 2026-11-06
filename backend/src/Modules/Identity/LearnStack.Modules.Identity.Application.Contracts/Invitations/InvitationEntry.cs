using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Contracts.Invitations;

public sealed record InspectInvitationQuery(Guid InvitationId, [property: PiiSensitive] string Code)
    : IRequest<Result<InvitationInspection>>
{ public override string ToString() => nameof(InspectInvitationQuery); }
public sealed record AcceptInvitationCommand(Guid InvitationId, [property: PiiSensitive] string Code, long ExpectedVersion)
    : IRequest<Result<InvitationAcceptanceResult>>
{ public override string ToString() => nameof(AcceptInvitationCommand); }
public sealed record GetOwnInvitationResultQuery(Guid InvitationId, [property: PiiSensitive] string Code)
    : IRequest<Result<InvitationAcceptanceResult>>
{ public override string ToString() => nameof(GetOwnInvitationResultQuery); }

public sealed record InvitationInspection(Guid InvitationId, long Version, DateTimeOffset ExpiresAt,
    string TenantLabel, string? OrganizationLabel, Guid RoleId, string RoleKey, string RoleScope,
    [property: PiiSensitive] string VerifiedEmail)
{ public override string ToString() => nameof(InvitationInspection); }
public sealed record InvitationAcceptanceResult(Guid InvitationId, long Version, Guid MembershipId,
    string Disposition, TenantId TenantId, OrganizationId? OrganizationId);

/// <summary>The exact fixed-function preliminary read and its fenced acceptance repeat.</summary>
public interface IInvitationEntryLookup
{
    Task<InvitationEntryObservation?> LookupAsync(Guid invitationId, ReadOnlyMemory<byte> verifierDigest,
        TenantId hostTenant, GlobalIdentitySubject subject, CancellationToken cancellationToken = default);
    Task<InvitationEntryObservation?> RecheckAsync(InvitationEntryScopeWitness witness,
        CancellationToken cancellationToken = default);
}
