using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Modules.Identity.Application.Contracts.Federation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.PublicReads;

/// <summary>
/// ADR-0099 § Sign-in exposure, owned by Identity: the host tenant's Active, entitled federation option or null.
/// Anonymous because the sign-in page precedes any session; host authority and read-only access are the pipeline's.
/// </summary>
[AllowAnonymous]
[Route("public/sign-in")]
public sealed class PublicSignInController(ISender sender) : ApiControllerBase
{
    [HttpGet(Name = "GetPublicSignIn")]
    [OperationPolicy(typeof(GetPublicSignInQuery))]
    [ProducesResponseType<PublicSignIn>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        (await sender.Send(new GetPublicSignInQuery(Request.Query.Count != 0), cancellationToken)).ToActionResult();

    [HttpHead(Name = "HeadPublicSignIn")]
    [OperationPolicy(typeof(GetPublicSignInQuery))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Head(CancellationToken cancellationToken) =>
        (await sender.Send(new GetPublicSignInQuery(Request.Query.Count != 0), cancellationToken)).ToActionResult();
}
