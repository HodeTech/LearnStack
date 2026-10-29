using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.SharedKernel.Pagination;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Identity;

[Route("identity/organization-memberships")]
public sealed class OrganizationMembershipsController(ISender sender) : ApiControllerBase
{
    [HttpGet(Name = "ListOrganizationMemberships")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.OrganizationMembershipDirectoryOperation)]
    [OperationPolicy(typeof(ListOrganizationMembershipsQuery))]
    [ProducesResponseType<Page<MembershipDirectoryItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (!AdministrationHttpInput.TryPage(Request, out var cursor, out var limit)
            || !await AdministrationHttpInput.ReadHasNoBodyAsync(HttpContext)) return AdministrationHttpInput.Invalid();
        return (await sender.Send(new ListOrganizationMembershipsQuery(cursor, limit), cancellationToken)).ToActionResult();
    }
}
