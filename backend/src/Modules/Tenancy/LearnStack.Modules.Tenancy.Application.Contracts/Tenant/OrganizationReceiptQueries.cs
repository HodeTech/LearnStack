using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Contracts.Tenant;

/// <summary>Authorizes only disclosure of a stored receipt for this original Organization.</summary>
public sealed record ReadOrganizationReceiptQuery(OrganizationId OrganizationId) : IRequest<Result<None>>, IIdempotencyReceiptRead;

/// <summary>The original default-selection receipt belongs to Tenant, regardless of its later default.</summary>
public sealed record ReadDefaultOrganizationReceiptQuery(TenantId TenantId) : IRequest<Result<None>>, IIdempotencyReceiptRead;
