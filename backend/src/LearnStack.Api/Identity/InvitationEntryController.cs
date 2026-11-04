using System.Globalization;
using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Identity;

public sealed record InvitationInspectionResponse(string InvitationId, string Version, string ExpiresAt,
    string TenantLabel, string? OrganizationLabel, string RoleId, string RoleKey, string RoleScope,
    [property: PiiSensitive] string VerifiedEmail)
{ public override string ToString() => nameof(InvitationInspectionResponse); }
public sealed record InvitationAcceptanceResponse(string InvitationId, string Version, string MembershipId,
    string Disposition, string TenantId, string? OrganizationId);

[Route("identity/invitation-entry")]
public sealed class InvitationEntryController(ISender sender) : ApiControllerBase
{
    [HttpPost("inspections", Name = "InspectInvitation")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "identity.invitation.inspect")]
    [OperationPolicy(typeof(InspectInvitationQuery))]
    [ProducesResponseType<InvitationInspectionResponse>(200)]
    public async Task<IActionResult> Inspect(CancellationToken cancellationToken)
    {
        if (HttpContext.Features.Get<InvitationEntryRequest>() is not { } source
            || source.Request is not InspectInvitationQuery request) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(request, cancellationToken);
        if (result.IsFailure) return result.ToActionResult();
        var value = result.Value;
        Response.Headers.ETag = EntityTag.For(value.Version);
        return Ok(new InvitationInspectionResponse(value.InvitationId.ToString("D"), AdministrationHttpInput.Version(value.Version),
            value.ExpiresAt.ToString("O", CultureInfo.InvariantCulture), value.TenantLabel, value.OrganizationLabel,
            value.RoleId.ToString("D"), value.RoleKey, value.RoleScope, value.VerifiedEmail));
    }
    [HttpPost("acceptances", Name = "AcceptInvitation")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "identity.invitation.accept")]
    [OperationPolicy(typeof(AcceptInvitationCommand))]
    [ProducesResponseType<InvitationAcceptanceResponse>(200)]
    public async Task<IActionResult> Accept(CancellationToken cancellationToken)
    {
        if (HttpContext.Features.Get<InvitationEntryRequest>() is not { } source) return AdministrationHttpInput.Invalid();
        var result = source.Request switch
        {
            AcceptInvitationCommand pending => await sender.Send(pending, cancellationToken),
            GetOwnInvitationResultQuery accepted => await sender.Send(accepted, cancellationToken),
            _ => LearnStack.SharedKernel.Results.Result<InvitationAcceptanceResult>.Fail(new Error(LocalizedMessage.Of("lockey_not_found"))),
        };
        if (result.IsFailure) return result.ToActionResult();
        Response.Headers.ETag = EntityTag.For(result.Value.Version);
        return Ok(Project(result.Value));
    }
    [HttpPost("results", Name = "GetOwnInvitationResult")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "identity.invitation.read_own_result")]
    [OperationPolicy(typeof(GetOwnInvitationResultQuery))]
    [ProducesResponseType<InvitationAcceptanceResponse>(200)]
    public async Task<IActionResult> Result(CancellationToken cancellationToken)
    {
        if (HttpContext.Features.Get<InvitationEntryRequest>() is not { } source
            || source.Request is not GetOwnInvitationResultQuery request) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(request, cancellationToken);
        if (result.IsFailure) return result.ToActionResult();
        Response.Headers.ETag = EntityTag.For(result.Value.Version);
        return Ok(Project(result.Value));
    }
    private static InvitationAcceptanceResponse Project(InvitationAcceptanceResult value) => new(value.InvitationId.ToString("D"),
        AdministrationHttpInput.Version(value.Version), value.MembershipId.ToString("D"), value.Disposition,
        value.TenantId.Value.ToString("D"), value.OrganizationId?.Value.ToString("D"));
}
