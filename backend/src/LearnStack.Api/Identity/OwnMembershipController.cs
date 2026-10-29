using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Api.Idempotency;
using LearnStack.Api.Tenancy;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Identity;

[Route("identity/me")]
public sealed class OwnMembershipController(ISender sender, ITenantContext tenant) : ApiControllerBase
{
    [HttpGet("profile", Name = "GetOwnMembershipProfile")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.OwnMembershipProfileReadOperation)]
    [OperationPolicy(typeof(GetOwnMembershipProfileQuery))]
    [ProducesResponseType<OwnMembershipProfileResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetProfile(CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || !await AdministrationHttpInput.ReadHasNoBodyAsync(HttpContext)) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(new GetOwnMembershipProfileQuery(), cancellationToken);
        if (result.IsFailure) return result.ToActionResult();
        Response.Headers.ETag = EntityTag.For(result.Value.Version);
        return Ok(new OwnMembershipProfileResponse(result.Value.DisplayName, result.Value.PreferredLocale,
            AdministrationHttpInput.Version(result.Value.Version)));
    }

    [HttpPut("profile", Name = "SetOwnMembershipProfile")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.OwnMembershipProfileWriteOperation)]
    [OperationPolicy(typeof(SetOwnMembershipProfileCommand))]
    [Idempotent(ProtectedAdministrationOperation.SetOwnMembershipProfile)]
    [ProducesResponseType<AdministrationVersionReceipt>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SetProfile(CancellationToken cancellationToken)
    {
        using var body = await AdministrationHttpInput.ReadObjectAsync(HttpContext, "displayName", "preferredLocale");
        if (body is null || !ProtectedEntityTag.TryRead(Request, out var version)
            || !AdministrationHttpInput.TryText(body.RootElement, "displayName", true, 128, out var name)
            || !AdministrationHttpInput.TryText(body.RootElement, "preferredLocale", true, 35, out var locale)) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant,
            new SetOwnMembershipProfileCommand(version, name, locale), cancellationToken);
    }

    [HttpGet("contexts", Name = "ListOwnMembershipContexts")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.OwnMembershipContextsOperation)]
    [OperationPolicy(typeof(GetOwnMembershipContextsQuery))]
    [ProducesResponseType<OwnMembershipContextPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Contexts(CancellationToken cancellationToken)
    {
        if (!AdministrationHttpInput.TryPage(Request, out var cursor, out var limit)
            || !await AdministrationHttpInput.ReadHasNoBodyAsync(HttpContext)) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(new GetOwnMembershipContextsQuery(cursor, limit), cancellationToken);
        if (result.IsFailure) return result.ToActionResult();
        var host = HttpContext.Features.Get<HostClassification>()
            ?? throw new InvalidOperationException("Context navigation requires classified host provenance.");
        var origins = HttpContext.RequestServices.GetRequiredService<NativeEntryOriginOptions>();
        return Ok(new OwnMembershipContextPage(result.Value.Items.Select(item => new OwnMembershipContextResponse(
            item.TenantId.Value.ToString("D"), item.OrganizationId?.Value.ToString("D"), item.TenantName, item.OrganizationName,
            origins.ForHost(item.EntryHost), host.TenantId == item.TenantId
                && (host.OrganizationId is null || host.OrganizationId == item.OrganizationId))).ToArray(), result.Value.PageInfo));
    }

    [HttpGet("capabilities", Name = "GetOwnCapabilities")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.OwnCapabilitiesOperation)]
    [OperationPolicy(typeof(GetOwnCapabilitiesQuery))]
    [ProducesResponseType<OwnCapabilitiesDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Capabilities(CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || !await AdministrationHttpInput.ReadHasNoBodyAsync(HttpContext)) return AdministrationHttpInput.Invalid();
        return (await sender.Send(new GetOwnCapabilitiesQuery(), cancellationToken)).ToActionResult();
    }
}
