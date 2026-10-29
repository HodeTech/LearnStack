using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Api.Pagination;
using LearnStack.Api.Idempotency;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Identity;

/// <summary>Tenant administrative directory; publication requires the common MUST audit commit.</summary>
[Route("identity/memberships")]
public sealed class MembershipsController(ISender sender, ITenantContext tenant) : ApiControllerBase
{
    [HttpGet(Name = "ListMemberships")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.MembershipDirectoryOperation)]
    [OperationPolicy(typeof(ListMembershipsQuery))]
    [ProducesResponseType<Page<MembershipDirectoryItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] CursorPaginationRequest pagination, CancellationToken cancellationToken)
    {
        if (Request.Query.Any(pair => pair.Key is not ("cursor" or "limit") || pair.Value.Count != 1
            || string.IsNullOrWhiteSpace(pair.Value[0])))
            return Result.Fail<Page<MembershipDirectoryItem>>(new Error(new LocalizedMessage("lockey_validation_failed"))).ToActionResult();
        return (await sender.Send(new ListMembershipsQuery(pagination.Cursor,
            pagination.Limit ?? CursorPagination.DefaultLimit), cancellationToken)).ToActionResult();
    }

    [HttpGet("{id}", Name = "GetMembershipDetail")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.MembershipDetailOperation)]
    [OperationPolicy(typeof(GetMembershipDetailQuery))]
    [ProducesResponseType<MembershipDetailResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken)
    {
        if (!AdministrationHttpInput.TryId(id, out var target) || Request.Query.Count != 0
            || !await AdministrationHttpInput.ReadHasNoBodyAsync(HttpContext)) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(new GetMembershipDetailQuery(target), cancellationToken);
        if (result.IsFailure) return result.ToActionResult();
        var value = result.Value;
        Response.Headers.ETag = EntityTag.For(value.Version);
        return Ok(new MembershipDetailResponse(value.MembershipId.ToString("D"), value.UserId.Value.ToString("D"),
            value.OrganizationId?.Value.ToString("D"), value.Status.ToString(), value.DisplayName, value.PreferredLocale,
            value.RoleIds.Select(role => role.ToString("D")).ToArray(), AdministrationHttpInput.Version(value.Version)));
    }

    [HttpPut("{id}/profile", Name = "SetMembershipProfile")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.MembershipProfileWriteOperation)]
    [OperationPolicy(typeof(SetMembershipProfileCommand))]
    [Idempotent(ProtectedAdministrationOperation.SetMembershipProfile)]
    [ProducesResponseType<AdministrationVersionReceipt>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SetProfile(string id, CancellationToken cancellationToken)
    {
        using var body = await AdministrationHttpInput.ReadObjectAsync(HttpContext, "displayName", "preferredLocale");
        if (body is null || !AdministrationHttpInput.TryId(id, out var target) || !ProtectedEntityTag.TryRead(Request, out var version)
            || !AdministrationHttpInput.TryText(body.RootElement, "displayName", true, 128, out var name)
            || !AdministrationHttpInput.TryText(body.RootElement, "preferredLocale", true, 35, out var locale)) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant,
            new SetMembershipProfileCommand(target, version, name, locale), cancellationToken);
    }

    [HttpPut("{id}/role-assignments", Name = "ReplaceMembershipRoleAssignments")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.MembershipAssignmentsOperation)]
    [OperationPolicy(typeof(ReplaceMembershipRoleAssignmentsCommand))]
    [Idempotent(ProtectedAdministrationOperation.ReplaceMembershipRoleAssignments)]
    [ProducesResponseType<AdministrationVersionReceipt>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ReplaceRoles(string id, CancellationToken cancellationToken)
    {
        using var body = await AdministrationHttpInput.ReadObjectAsync(HttpContext, "roleIds");
        if (body is null || !AdministrationHttpInput.TryId(id, out var target) || !ProtectedEntityTag.TryRead(Request, out var version)
            || !AdministrationHttpInput.TryRoles(body.RootElement, out var roles)) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant,
            new ReplaceMembershipRoleAssignmentsCommand(target, version, roles), cancellationToken);
    }

    [HttpPut("{id}/status", Name = "ChangeMembershipStatus")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.MembershipStatusOperation)]
    [OperationPolicy(typeof(ChangeMembershipStatusCommand))]
    [Idempotent(ProtectedAdministrationOperation.ChangeMembershipStatus)]
    [ProducesResponseType<AdministrationVersionReceipt>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ChangeStatus(string id, CancellationToken cancellationToken)
    {
        using var body = await AdministrationHttpInput.ReadObjectAsync(HttpContext, "status");
        if (body is null || !AdministrationHttpInput.TryId(id, out var target) || !ProtectedEntityTag.TryRead(Request, out var version)
            || !AdministrationHttpInput.TryText(body.RootElement, "status", false, 9, out var status)
            || status is not ("Active" or "Suspended" or "Revoked")) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant,
            new ChangeMembershipStatusCommand(target, version, Enum.Parse<ManagedMembershipStatus>(status)), cancellationToken);
    }
}
