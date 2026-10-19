using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Contracts.Account;

/// <summary>The transport supplies only whether the query string violates the no-parameter contract.</summary>
public sealed record GetAccountSiteQuery(bool HasUnexpectedQuery = false) : IRequest<Result<AccountSite>>;

/// <summary>The authenticated actor and resolved scope with the host's existing public site projection.</summary>
public sealed record AccountSite(
    UserId ActorId, TenantId TenantId, OrganizationId? OrganizationId, PublicSite Site);
