using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Identity;

[Route("identity/permissions")]
public sealed class PermissionsController(ISender sender) : ApiControllerBase
{
    [HttpGet(Name = "GetPermissionRegistry")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.PermissionRegistryOperation)]
    [OperationPolicy(typeof(GetPermissionRegistryQuery))]
    [ProducesResponseType<PermissionRegistryResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || !await AdministrationHttpInput.ReadHasNoBodyAsync(HttpContext)) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(new GetPermissionRegistryQuery(), cancellationToken);
        if (result.IsFailure) return result.ToActionResult();
        return Ok(new PermissionRegistryResponse(result.Value.Items.Select(item => new PermissionRegistryItemResponse(
            item.Scope.ToString(), item.Key, item.LabelKey, item.CanGrant)).ToArray(), new RoleReleaseResponse(
            result.Value.Release.Version, result.Value.Release.Fingerprint, result.Value.Release.TenantAdminDelta.Select(
                grant => new AdministrationGrantResponse(grant.Scope.ToString(), grant.Key)).ToArray())));
    }
}
