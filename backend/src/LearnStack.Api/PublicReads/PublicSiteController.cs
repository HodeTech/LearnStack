using LearnStack.Api.Common;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.PublicReads;

/// <summary>Anonymous institution bootstrap; host authority and read-only access are enforced by the pipeline.</summary>
[AllowAnonymous]
[Route("public/site")]
public sealed class PublicSiteController(ISender sender) : ApiControllerBase
{
    [HttpGet(Name = "GetPublicSite")]
    [ProducesResponseType<PublicSite>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        (await sender.Send(new GetPublicSiteQuery(Request.Query.Count != 0), cancellationToken)).ToActionResult();

    [HttpHead(Name = "HeadPublicSite")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Head(CancellationToken cancellationToken) =>
        (await sender.Send(new GetPublicSiteQuery(Request.Query.Count != 0), cancellationToken)).ToActionResult();
}
