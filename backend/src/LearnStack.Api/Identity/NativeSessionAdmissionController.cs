using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Contracts.Entry;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Identity;

[Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
    Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.NativeSessionAdmissionOperation)]
[Route("identity/session-scope/admissions")]
public sealed class NativeSessionAdmissionController(ISender sender) : ApiControllerBase
{
    [HttpPost(Name = "AdmitNativeSessionScope")]
    [OperationPolicy(typeof(AdmitNativeSessionScopeCommand))]
    [ProducesResponseType<NativeSessionScopeAdmission>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Admit(CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || Request.Headers.ContainsKey("Idempotency-Key")
            || !await EmptyNativeJsonBody.ReadAsync(HttpContext))
            return Result.Fail<NativeSessionScopeAdmission>(new Error(new LocalizedMessage("lockey_validation_failed"))).ToActionResult();
        var result = await sender.Send(new AdmitNativeSessionScopeCommand(), cancellationToken);
        if (result.IsFailure) return result.ToActionResult();
        if (HttpContext.Features.Get<NativeScopeEvidence>() is not { } native || !native.IsCurrent(HttpContext)
            || native.Actor != result.Value.ActorId || native.Tenant != result.Value.TenantId
            || native.Organization != result.Value.OrganizationId)
            return Result.Fail<NativeSessionScopeAdmission>(new Error(new LocalizedMessage("lockey_not_found"))).ToActionResult();
        return result.ToActionResult();
    }
}
