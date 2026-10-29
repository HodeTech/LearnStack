using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Api.Idempotency;
using LearnStack.Api.Identity;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Tenancy;

[Route("tenancy/default-organization")]
public sealed class DefaultOrganizationController(ISender sender, ITenantContext tenant) : ApiControllerBase
{
    [HttpPut(Name = "SelectDefaultOrganization")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "tenancy.organization.select_default")]
    [OperationPolicy(typeof(SelectDefaultOrganizationCommand))]
    [Idempotent(ProtectedAdministrationOperation.SelectDefaultOrganization)]
    [ProducesResponseType<AdministrationVersionReceipt>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Select(CancellationToken cancellationToken)
    {
        using var body = await AdministrationHttpInput.ReadObjectAsync(HttpContext, "organizationId");
        if (body is null || !ProtectedEntityTag.TryRead(Request, out var version)
            || !AdministrationHttpInput.TryText(body.RootElement, "organizationId", false, 36, out var value)
            || !AdministrationHttpInput.TryId(value, out var id)) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant,
            new SelectDefaultOrganizationCommand(OrganizationId.From(id), version), cancellationToken);
    }
}
