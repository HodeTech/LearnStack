using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Contracts.Roles;

/// <summary>Uses current Role disclosure without rerunning grant replacement or manifest adoption.</summary>
public sealed record ReadRoleReceiptQuery(Guid RoleId) : IRequest<Result<None>>, IIdempotencyReceiptRead;
