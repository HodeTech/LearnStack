using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Modules.Tenancy.Application.Contracts.Account;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Account;

/// <summary>The one authenticated, host-backed nonsensitive read before Phase 03.</summary>
[Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme)]
[Route("account")]
public sealed class AccountController(ISender sender) : ApiControllerBase
{
    [HttpGet("site", Name = "GetAccountSite")]
    [ProducesResponseType<AccountSite>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSite(CancellationToken cancellationToken) =>
        (await sender.Send(new GetAccountSiteQuery(Request.Query.Count != 0), cancellationToken)).ToActionResult();
}
