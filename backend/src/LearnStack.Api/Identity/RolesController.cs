using System.Text.Json;
using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Api.Idempotency;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Identity;

[Route("identity/roles")]
public sealed class RolesController(ISender sender, ITenantContext tenant) : ApiControllerBase
{
    [HttpGet(Name = "ListRoles")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.RoleDirectoryOperation)]
    [OperationPolicy(typeof(ListRolesQuery))]
    [ProducesResponseType<RoleDirectoryResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || !await AdministrationHttpInput.ReadHasNoBodyAsync(HttpContext)) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(new ListRolesQuery(), cancellationToken);
        if (result.IsFailure) return result.ToActionResult();
        return Ok(new RoleDirectoryResponse(result.Value.Items.Select(role => new RoleSummaryResponse(role.RoleId.ToString("D"),
            role.BuiltInKey, role.CapabilityScope.ToString(), AdministrationHttpInput.Version(role.Version))).ToArray()));
    }

    [HttpGet("{id}", Name = "GetRoleDetail")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.RoleDetailOperation)]
    [OperationPolicy(typeof(GetRoleDetailQuery))]
    [ProducesResponseType<RoleDetailResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken)
    {
        if (!AdministrationHttpInput.TryId(id, out var target) || Request.Query.Count != 0
            || !await AdministrationHttpInput.ReadHasNoBodyAsync(HttpContext)) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(new GetRoleDetailQuery(target), cancellationToken);
        if (result.IsFailure) return result.ToActionResult();
        var role = result.Value;
        Response.Headers.ETag = EntityTag.For(role.Version);
        return Ok(new RoleDetailResponse(role.RoleId.ToString("D"), role.BuiltInKey, role.CapabilityScope.ToString(),
            AdministrationHttpInput.Version(role.Version), role.Grants.Select(grant => new AdministrationGrantResponse(
                grant.Scope.ToString(), grant.Key)).ToArray(), role.AdoptedReleaseVersion, role.AdoptedReleaseFingerprint));
    }

    [HttpPut("{id}/grants", Name = "ChangeRoleGrants")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + "identity.role.change_grants")]
    [OperationPolicy(typeof(ChangeRoleGrantsCommand))]
    [Idempotent(ProtectedAdministrationOperation.ChangeRoleGrants)]
    [ProducesResponseType<AdministrationVersionReceipt>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ChangeGrants(string id, CancellationToken cancellationToken)
    {
        using var body = await AdministrationHttpInput.ReadObjectAsync(HttpContext, "grants");
        if (body is null || !AdministrationHttpInput.TryId(id, out var target) || !ProtectedEntityTag.TryRead(Request, out var version)
            || !AdministrationHttpInput.TryGrants(body.RootElement, out var grants)) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant,
            new ChangeRoleGrantsCommand(target, version, grants), cancellationToken);
    }

    [HttpPost("{id}/manifest-adoptions", Name = "AdoptRoleGrantManifest")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + "identity.role.adopt_manifest")]
    [OperationPolicy(typeof(AdoptRoleGrantManifestCommand))]
    [Idempotent(ProtectedAdministrationOperation.AdoptRoleGrantManifest)]
    [ProducesResponseType<AdministrationVersionReceipt>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AdoptManifest(string id, CancellationToken cancellationToken)
    {
        using var body = await AdministrationHttpInput.ReadObjectAsync(HttpContext, "manifestVersion", "manifestFingerprint");
        if (body is null || !AdministrationHttpInput.TryId(id, out var target) || !ProtectedEntityTag.TryRead(Request, out var version)
            || body.RootElement.GetProperty("manifestVersion") is not { ValueKind: JsonValueKind.Number } release
            || !release.TryGetInt32(out var manifestVersion) || manifestVersion <= 0
            || release.GetRawText() != manifestVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || !AdministrationHttpInput.TryText(body.RootElement, "manifestFingerprint", false, 64, out var fingerprint)
            || fingerprint is not { Length: 64 } || fingerprint.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant,
            new AdoptRoleGrantManifestCommand(target, version, manifestVersion, fingerprint), cancellationToken);
    }
}
