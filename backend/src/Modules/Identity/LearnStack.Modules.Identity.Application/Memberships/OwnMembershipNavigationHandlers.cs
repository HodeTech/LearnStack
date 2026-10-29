using FluentValidation;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Memberships;

public sealed class GetOwnCapabilitiesQueryHandler(IdentityAdministrationReadAuthorization authorization, IOperationPolicyCatalog catalog)
    : IRequestHandler<GetOwnCapabilitiesQuery, Result<OwnCapabilitiesDto>>
{
    public async Task<Result<OwnCapabilitiesDto>> Handle(GetOwnCapabilitiesQuery request, CancellationToken cancellationToken)
    {
        var admission = await authorization.AdmitAsync(request, cancellationToken);
        if (admission.IsFailure) return Result<OwnCapabilitiesDto>.Fail(admission.Error);
        var facts = await authorization.ReadFactsAsync(request, admission.Value, cancellationToken);
        return facts.IsSuccess ? Result.Ok(OwnCapabilityProjection.Create(facts.Value, catalog))
            : Result<OwnCapabilitiesDto>.Fail(facts.Error);
    }
}

public sealed class GetOwnMembershipContextsQueryHandler(IdentityAdministrationReadAuthorization authorization, IOwnTenantNavigationReader reader)
    : IRequestHandler<GetOwnMembershipContextsQuery, Result<Page<OwnMembershipContextDto>>>
{
    public async Task<Result<Page<OwnMembershipContextDto>>> Handle(GetOwnMembershipContextsQuery request, CancellationToken cancellationToken)
    {
        var admission = await authorization.AdmitAsync(request, cancellationToken);
        if (admission.IsFailure) return Result<Page<OwnMembershipContextDto>>.Fail(admission.Error);
        if (request.Limit <= 0) return IdentityAdministrationReadAuthorization.Fail<Page<OwnMembershipContextDto>>("lockey_validation_failed");
        var page = await reader.ReadAsync(request.Cursor, Math.Min(request.Limit, CursorPagination.MaxLimit), cancellationToken);
        var check = await authorization.RecheckAsync(request, admission.Value, cancellationToken);
        if (check.IsFailure) return Result<Page<OwnMembershipContextDto>>.Fail(check.Error);
        if (page.IsFailure) return Result<Page<OwnMembershipContextDto>>.Fail(page.Error);
        return Result.Ok(new Page<OwnMembershipContextDto>(page.Value.Items.Select(item => new OwnMembershipContextDto(
            item.TenantId, item.OrganizationId, item.TenantName, item.OrganizationName, item.EntryHost)).ToArray(), page.Value.PageInfo));
    }
}

/// <summary>Only this exact active own-navigation invocation can ask Tenancy to derive branches from current own coverage.</summary>
public sealed class OwnTenantNavigationCoverage(IdentityAdministrationReadAuthorization authorization, IOperationInvocationContext invocations)
    : IOwnTenantNavigationCoverage
{
    public async Task<Result<OwnTenantNavigationAccess>> ReadCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (invocations.CurrentInvocation is not { Operation: GetOwnMembershipContextsQuery request } invocation
            || !ReferenceEquals(authorization.Current(request), invocation))
            return IdentityAdministrationReadAuthorization.Fail<OwnTenantNavigationAccess>("lockey_forbidden");
        var facts = await authorization.ReadFactsAsync(request, invocation, cancellationToken);
        if (facts.IsFailure) return Result<OwnTenantNavigationAccess>.Fail(facts.Error);
        var tenant = facts.Value.Memberships.Any(member => member.OrganizationId is null);
        if (!tenant && !facts.Value.Memberships.Any(member => member.OrganizationId == invocation.OrganizationId))
            return IdentityAdministrationReadAuthorization.Fail<OwnTenantNavigationAccess>("lockey_not_found");
        return Result.Ok(new OwnTenantNavigationAccess(invocation, tenant));
    }
}

internal sealed class GetOwnMembershipContextsQueryValidator : AbstractValidator<GetOwnMembershipContextsQuery>
{
    public GetOwnMembershipContextsQueryValidator()
    {
        RuleFor(query => query.Limit).GreaterThan(0).WithErrorCode("lockey_invalid_value");
        RuleFor(query => query.Cursor).Must(cursor => cursor is null || cursor.Length is > 0 and <= 1024)
            .WithErrorCode("lockey_invalid_value");
    }
}
