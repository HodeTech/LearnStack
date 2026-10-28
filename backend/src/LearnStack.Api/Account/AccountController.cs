using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Modules.Tenancy.Application.Contracts.Account;
using LearnStack.Modules.Tenancy.Application.Authorization;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Account;

/// <summary>The authenticated nonsensitive read retains its independent matching HostScope ceiling.</summary>
[Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
    Policy = OperationPolicyRequirement.Prefix + TenancyOperationPolicySource.AccountSiteOperation)]
[Route("account")]
public sealed class AccountController(ISender sender) : ApiControllerBase
{
    [HttpGet("site", Name = "GetAccountSite")]
    [OperationPolicy(typeof(GetAccountSiteQuery))]
    [ProducesResponseType<AccountSite>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSite(CancellationToken cancellationToken) =>
        (await sender.Send(new GetAccountSiteQuery(Request.Query.Count != 0), cancellationToken)).ToActionResult();
}
