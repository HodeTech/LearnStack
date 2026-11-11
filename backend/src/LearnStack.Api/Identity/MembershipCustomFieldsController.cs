using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Api.Customization;
using LearnStack.Api.Idempotency;
using LearnStack.Modules.Identity.Application.Contracts.CustomFields;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace LearnStack.Api.Identity;

[Route("identity")]
public sealed class MembershipCustomFieldsController(ISender sender, ITenantContext tenant) : ApiControllerBase
{
    [HttpGet("memberships/{id}/custom-fields", Name = "GetMembershipCustomFields")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "identity.membership.custom_fields_read")]
    [OperationPolicy(typeof(GetMembershipCustomFieldsQuery))]
    [ProducesResponseType<MembershipCustomFieldsResponse>(200)]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken)
    {
        if (!AdministrationHttpInput.TryId(id, out var target) || Request.Query.Count != 0 || !await AdministrationHttpInput.ReadHasNoBodyAsync(HttpContext)) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(new GetMembershipCustomFieldsQuery(target), cancellationToken); if (result.IsFailure) return result.ToActionResult();
        Response.Headers.ETag = EntityTag.For(result.Value.Version); return Ok(CustomFieldResponseProjection.Values(result.Value));
    }
    [HttpGet("me/custom-fields", Name = "GetOwnMembershipCustomFields")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "identity.membership.custom_fields_read_own")]
    [OperationPolicy(typeof(GetOwnMembershipCustomFieldsQuery))]
    [ProducesResponseType<MembershipCustomFieldsResponse>(200)]
    public async Task<IActionResult> GetOwn(CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || !await AdministrationHttpInput.ReadHasNoBodyAsync(HttpContext)) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(new GetOwnMembershipCustomFieldsQuery(), cancellationToken); if (result.IsFailure) return result.ToActionResult();
        Response.Headers.ETag = EntityTag.For(result.Value.Version); return Ok(CustomFieldResponseProjection.Values(result.Value));
    }
    [HttpPatch("memberships/{id}/custom-fields", Name = "SetMembershipCustomFields")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "identity.membership.custom_fields_write")]
    [OperationPolicy(typeof(SetMembershipCustomFieldsCommand))]
    [Idempotent(ProtectedAdministrationOperation.SetMemberCustomFields)]
    [ProducesResponseType<CustomFieldReceiptResponse>(200)]
    public async Task<IActionResult> Set(string id, CancellationToken cancellationToken)
    {
        using var body = await CustomFieldHttpInput.ReadAsync(HttpContext, false, "set", "remove");
        if (body is null || !AdministrationHttpInput.TryId(id, out var target) || !ProtectedEntityTag.TryRead(Request, out var version) || !CustomFieldHttpInput.Patch(body.RootElement, out var patch)) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant, new SetMembershipCustomFieldsCommand(target, version, patch!), cancellationToken);
    }
    [HttpPatch("me/custom-fields", Name = "SetOwnMembershipCustomFields")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "identity.membership.custom_fields_write_own")]
    [OperationPolicy(typeof(SetOwnMembershipCustomFieldsCommand))]
    [Idempotent(ProtectedAdministrationOperation.SetOwnCustomFields)]
    [ProducesResponseType<CustomFieldReceiptResponse>(200)]
    public async Task<IActionResult> SetOwn(CancellationToken cancellationToken)
    {
        using var body = await CustomFieldHttpInput.ReadAsync(HttpContext, false, "set", "remove");
        if (body is null || !ProtectedEntityTag.TryRead(Request, out var version) || !CustomFieldHttpInput.Patch(body.RootElement, out var patch)) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant, new SetOwnMembershipCustomFieldsCommand(version, patch!), cancellationToken);
    }
}
