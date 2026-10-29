using FluentValidation;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Roles;

public sealed class ListRolesQueryHandler(IIdentityAdministrationReadStore reader, IdentityAdministrationReadAuthorization authorization)
    : IRequestHandler<ListRolesQuery, Result<RoleDirectoryDto>>
{
    public async Task<Result<RoleDirectoryDto>> Handle(ListRolesQuery request, CancellationToken cancellationToken)
    {
        var admission = await authorization.AdmitAsync(request, cancellationToken);
        if (admission.IsFailure) return Result<RoleDirectoryDto>.Fail(admission.Error);
        var roles = await reader.ReadRolesAsync(cancellationToken);
        var check = await authorization.RecheckAsync(request, admission.Value, cancellationToken);
        if (check.IsFailure) return Result<RoleDirectoryDto>.Fail(check.Error);
        if (roles.Count > IdentityFoundationManifest.Version1.Templates.Length || roles.Any(role =>
                IdentityFoundationManifest.Version1.FindTemplate(role.BuiltInKey, role.PermissionScope) is null)
            || roles.GroupBy(role => (role.BuiltInKey, role.PermissionScope)).Any(group => group.Count() != 1))
            return IdentityAdministrationReadAuthorization.Fail<RoleDirectoryDto>("lockey_dependency_unavailable");
        return Result.Ok(new RoleDirectoryDto([.. roles.Select(role => new RoleDirectoryItemDto(role.Id.Value,
            role.BuiltInKey.ToString(), role.PermissionScope, role.Version))]));
    }
}

public sealed class GetRoleDetailQueryHandler(IIdentityAdministrationReadStore reader,
    IdentityAdministrationReadAuthorization authorization, IAuditSubject subject)
    : IRequestHandler<GetRoleDetailQuery, Result<RoleDetailDto>>
{
    public async Task<Result<RoleDetailDto>> Handle(GetRoleDetailQuery request, CancellationToken cancellationToken)
    {
        var admission = await authorization.AdmitAsync(request, cancellationToken);
        if (admission.IsFailure) return Result<RoleDetailDto>.Fail(admission.Error);
        if (request.RoleId == Guid.Empty || request.RoleId.Version != 7)
            return IdentityAdministrationReadAuthorization.Fail<RoleDetailDto>("lockey_validation_failed");
        var role = await reader.ReadRoleAsync(RoleId.From(request.RoleId), cancellationToken);
        var check = await authorization.RecheckAsync(request, admission.Value, cancellationToken);
        if (check.IsFailure) return Result<RoleDetailDto>.Fail(check.Error);
        if (role is null) return IdentityAdministrationReadAuthorization.Fail<RoleDetailDto>("lockey_not_found");
        if (!IdentityRoleRelease.Version2.IsValidAdoptionState(role)
            || IdentityFoundationManifest.Version1.FindTemplate(role.BuiltInKey, role.PermissionScope) is null
            || role.Grants.Any(grant => !IdentityRoleRelease.Version2.IsActive(grant.PermissionScope, grant.PermissionKey)))
            return IdentityAdministrationReadAuthorization.Fail<RoleDetailDto>("lockey_dependency_unavailable");
        subject.Designate(role);
        return Result.Ok(new RoleDetailDto(role.Id.Value, role.BuiltInKey.ToString(), role.PermissionScope, role.Version,
            [.. role.Grants.OrderBy(grant => grant.PermissionKey, StringComparer.Ordinal)
                .Select(grant => new PermissionDefinition(grant.PermissionScope, grant.PermissionKey))],
            role.AdoptedReleaseVersion, role.AdoptedReleaseFingerprint));
    }
}

public sealed class GetPermissionRegistryQueryHandler(IdentityAdministrationReadAuthorization authorization, IOperationPolicyCatalog catalog)
    : IRequestHandler<GetPermissionRegistryQuery, Result<PermissionRegistryDto>>
{
    public async Task<Result<PermissionRegistryDto>> Handle(GetPermissionRegistryQuery request, CancellationToken cancellationToken)
    {
        var admission = await authorization.AdmitAsync(request, cancellationToken);
        if (admission.IsFailure) return Result<PermissionRegistryDto>.Fail(admission.Error);
        var facts = await authorization.ReadFactsAsync(request, admission.Value, cancellationToken);
        if (facts.IsFailure) return Result<PermissionRegistryDto>.Fail(facts.Error);
        var check = await authorization.RecheckAsync(request, admission.Value, cancellationToken);
        if (check.IsFailure) return Result<PermissionRegistryDto>.Fail(check.Error);
        var release = IdentityRoleRelease.Version2;
        return Result.Ok(new PermissionRegistryDto([.. release.Definitions.Select(definition =>
            new PermissionRegistryItemDto(definition.Scope, definition.Key,
                "lockey_permission_" + definition.Scope.ToString().ToLowerInvariant() + "_" + definition.Key.Replace('.', '_'),
                OwnCapabilityProjection.CanGrant(facts.Value, catalog, definition)))],
            new RoleReleaseDto(release.Version, release.Fingerprint, release.TenantAdminDelta)));
    }
}

internal sealed class GetRoleDetailQueryValidator : AbstractValidator<GetRoleDetailQuery>
{
    public GetRoleDetailQueryValidator() => RuleFor(query => query.RoleId)
        .Must(id => id != Guid.Empty && id.Version == 7).WithErrorCode("lockey_identifier_required");
}
