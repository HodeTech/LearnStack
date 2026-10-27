using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Api.Pagination;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Identity;

/// <summary>Tenant administrative directory; publication requires the common MUST audit commit.</summary>
[Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
    Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.MembershipDirectoryOperation)]
[Route("identity/memberships")]
public sealed class MembershipsController(ISender sender) : ApiControllerBase
{
    [HttpGet(Name = "ListMemberships")]
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
}
