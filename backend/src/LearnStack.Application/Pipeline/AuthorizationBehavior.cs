using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Application.Pipeline;

/// <summary>
/// MediatR pipeline behavior — step 5 of the canonical 8-step order
/// (ADR-0032 § Sub-decision 2). Calls
/// <c>IAuthorizationService.AuthorizeAsync</c> against the command's
/// resource; on deny returns <c>Result.Fail(forbidden)</c> rather than
/// throwing.
/// </summary>
/// <remarks>
/// Phase 02a Packet 3 ships the <strong>shell</strong>: there is no
/// permission catalogue yet (it lands together with the Identity module in
/// Phase 03 + Standards 19). The shell passes every request through and
/// preserves the pipeline-order contract. When the permission catalogue
/// arrives the shell flips to consuming <c>IAuthorizationService</c> and
/// per-request <c>[Authorize]</c> metadata; the registration order does not
/// change.
/// </remarks>
public sealed class AuthorizationBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : IResultBase
{
    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        // TODO(2026-05-21, @platform, phase-03): resolve the request's
        // [Authorize(Policy)] attribute, call IAuthorizationService.AuthorizeAsync
        // with the tenant + organization-scoped resource, and return
        // Result.FailFor<TResponse>(forbidden) on deny.

        return next();
    }
}
