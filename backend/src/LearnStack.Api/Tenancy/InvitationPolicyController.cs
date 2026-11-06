using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Api.Identity;
using LearnStack.Api.Idempotency;
using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Tenancy;

[Route("tenancy/settings/invitation-policy")]
public sealed class InvitationPolicyController(ISender sender, ITenantContext tenant) : ApiControllerBase
{
    [HttpGet(Name = "GetTenantInvitationPolicy")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "tenancy.setting.read_invitation_policy")]
    [OperationPolicy(typeof(GetTenantInvitationPolicyQuery))]
    [ProducesResponseType<InvitationPolicyResponse>(200)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || !await AdministrationHttpInput.ReadHasNoBodyAsync(HttpContext)) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(new GetTenantInvitationPolicyQuery(), cancellationToken);
        if (result.IsFailure) return result.ToActionResult();
        if (result.Value.Version is { } version) Response.Headers.ETag = EntityTag.For(version);
        return Ok(new InvitationPolicyResponse(result.Value.Days, result.Value.Present,
            result.Value.Version is { } value ? AdministrationHttpInput.Version(value) : null, result.Value.CanEdit));
    }
    [HttpPut(Name = "SetTenantInvitationLifetime")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "tenancy.setting.write_invitation_lifetime")]
    [OperationPolicy(typeof(SetTenantInvitationLifetimeCommand))]
    [Idempotent(ProtectedAdministrationOperation.SetTenantInvitationLifetime)]
    [ProducesResponseType<AdministrationVersionReceipt>(200)]
    public async Task<IActionResult> Set(CancellationToken cancellationToken)
    {
        using var body = await AdministrationHttpInput.ReadObjectAsync(HttpContext, "days");
        if (body is null || body.RootElement.GetProperty("days").ValueKind != System.Text.Json.JsonValueKind.Number || !body.RootElement.GetProperty("days").TryGetInt32(out var days)
            || !ProtectedAdministrationDescriptor.For(ProtectedAdministrationOperation.SetTenantInvitationLifetime).TryReadPrecondition(Request, out var expected)) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant,
            new SetTenantInvitationLifetimeCommand(days, expected.HasValue, expected), cancellationToken);
    }
}
