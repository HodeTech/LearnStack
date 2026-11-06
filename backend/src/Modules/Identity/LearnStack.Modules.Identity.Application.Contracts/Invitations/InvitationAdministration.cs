using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Contracts.Invitations;

public sealed record InvitationDto(Guid InvitationId, [property: PiiSensitive] string Email, OrganizationId? OrganizationId,
    Guid RoleId, long RoleVersion, string Status, DateTimeOffset ExpiresAt, long Version, string DeliveryStatus, string? AcceptanceDisposition)
{
    public override string ToString() => nameof(InvitationDto);
}
public sealed record InvitableRoleDto(Guid RoleId, string BuiltInKey, string CapabilityScope, long Version);
public sealed record InvitableRoleDirectory(IReadOnlyList<InvitableRoleDto> Items);
public sealed record InvitationMutationResult(Guid InvitationId, long Version);
public sealed record ListInvitationsQuery(string? Cursor = null, int Limit = 20) : IRequest<Result<Page<InvitationDto>>>;
public sealed record GetInvitationQuery(Guid InvitationId) : IRequest<Result<InvitationDto>>;
public sealed record ListInvitableRolesQuery() : IRequest<Result<InvitableRoleDirectory>>;
public sealed record CreateInvitationCommand([property: PiiSensitive] string Email, Guid RoleId, long ExpectedRoleVersion)
    : IRequest<Result<InvitationMutationResult>>
{
    public override string ToString() => nameof(CreateInvitationCommand);
}
public sealed record RevokeInvitationCommand(Guid InvitationId, long ExpectedVersion) : IRequest<Result<InvitationMutationResult>>;
public sealed record ReissueInvitationCommand(Guid InvitationId, long ExpectedVersion) : IRequest<Result<InvitationMutationResult>>;
public sealed record ReadInvitationReceiptQuery(Guid InvitationId) : IRequest<Result<None>>, IIdempotencyReceiptRead;
