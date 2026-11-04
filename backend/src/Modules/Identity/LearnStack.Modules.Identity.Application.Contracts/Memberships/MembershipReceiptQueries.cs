using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Contracts.Memberships;

/// <summary>Authorizes only disclosure of a stored receipt for this original Membership.</summary>
public sealed record ReadMembershipReceiptQuery(Guid MembershipId) : IRequest<Result<None>>, IIdempotencyReceiptRead;

/// <summary>Uses current Membership administration disclosure, not the original profile-write permission.</summary>
public sealed record ReadMemberProfileReceiptQuery(Guid MembershipId) : IRequest<Result<None>>, IIdempotencyReceiptRead;

/// <summary>Checks the original contextual Membership without substituting a current successor.</summary>
public sealed record ReadOwnMembershipProfileReceiptQuery(Guid MembershipId) : IRequest<Result<None>>, IIdempotencyReceiptRead;
