using System.Globalization;
using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Api.Idempotency;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Identity;

[Route("identity/invitations")]
public sealed class InvitationsController(ISender sender, ITenantContext tenant) : ApiControllerBase
{
    [HttpGet(Name = "ListInvitations")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "identity.invitation.list")]
    [OperationPolicy(typeof(ListInvitationsQuery))]
    [ProducesResponseType<InvitationPage>(200)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (!AdministrationHttpInput.TryPage(Request, out var cursor, out var limit) || !await AdministrationHttpInput.ReadHasNoBodyAsync(HttpContext)) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(new ListInvitationsQuery(cursor, limit), cancellationToken);
        return result.IsFailure ? result.ToActionResult() : Ok(new InvitationPage(result.Value.Items.Select(Project).ToArray(), result.Value.PageInfo));
    }
    [HttpGet("{id}", Name = "GetInvitation")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "identity.invitation.read")]
    [OperationPolicy(typeof(GetInvitationQuery))]
    [ProducesResponseType<InvitationResponse>(200)]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken)
    {
        if (!AdministrationHttpInput.TryId(id, out var target) || Request.Query.Count != 0 || !await AdministrationHttpInput.ReadHasNoBodyAsync(HttpContext)) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(new GetInvitationQuery(target), cancellationToken);
        if (result.IsFailure) return result.ToActionResult();
        Response.Headers.ETag = EntityTag.For(result.Value.Version); return Ok(Project(result.Value));
    }
    [HttpPost(Name = "CreateInvitation")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "identity.invitation.create")]
    [OperationPolicy(typeof(CreateInvitationCommand))]
    [Idempotent(ProtectedAdministrationOperation.CreateInvitation)]
    [ProducesResponseType<AdministrationVersionReceipt>(201)]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        using var body = await AdministrationHttpInput.ReadObjectAsync(HttpContext, "email", "roleId", "roleVersion");
        if (body is null || !AdministrationHttpInput.TryText(body.RootElement, "email", false, 254, out var email)
            || !AdministrationHttpInput.TryText(body.RootElement, "roleId", false, 36, out var role) || !AdministrationHttpInput.TryId(role, out var roleId)
            || !AdministrationHttpInput.TryText(body.RootElement, "roleVersion", false, 19, out var version) || !ProtectedEntityTag.TryParseVersion(version, out var parsed)) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant, new CreateInvitationCommand(email!, roleId, parsed), cancellationToken);
    }
    [HttpPost("{id}/revocations", Name = "RevokeInvitation")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "identity.invitation.revoke")]
    [OperationPolicy(typeof(RevokeInvitationCommand))]
    [Idempotent(ProtectedAdministrationOperation.RevokeInvitation)]
    [ProducesResponseType<AdministrationVersionReceipt>(200)]
    public async Task<IActionResult> Revoke(string id, CancellationToken cancellationToken)
    {
        using var body = await AdministrationHttpInput.ReadObjectAsync(HttpContext);
        if (body is null || !AdministrationHttpInput.TryId(id, out var target) || !ProtectedEntityTag.TryRead(Request, out var version)) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant, new RevokeInvitationCommand(target, version), cancellationToken);
    }
    [HttpPost("{id}/reissues", Name = "ReissueInvitation")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "identity.invitation.reissue")]
    [OperationPolicy(typeof(ReissueInvitationCommand))]
    [Idempotent(ProtectedAdministrationOperation.ReissueInvitation)]
    [ProducesResponseType<AdministrationVersionReceipt>(200)]
    public async Task<IActionResult> Reissue(string id, CancellationToken cancellationToken)
    {
        using var body = await AdministrationHttpInput.ReadObjectAsync(HttpContext);
        if (body is null || !AdministrationHttpInput.TryId(id, out var target) || !ProtectedEntityTag.TryRead(Request, out var version)) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant, new ReissueInvitationCommand(target, version), cancellationToken);
    }
    private static InvitationResponse Project(InvitationDto row) => new(row.InvitationId.ToString("D"), row.Email, row.OrganizationId?.Value.ToString("D"),
        row.RoleId.ToString("D"), AdministrationHttpInput.Version(row.RoleVersion), row.Status, row.ExpiresAt.ToString("O", CultureInfo.InvariantCulture),
        AdministrationHttpInput.Version(row.Version), row.DeliveryStatus, row.AcceptanceDisposition);
}

[Route("identity/invitable-roles")]
public sealed class InvitableRolesController(ISender sender) : ApiControllerBase
{
    [HttpGet(Name = "ListInvitableRoles")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "identity.invitation.list_invitable_roles")]
    [OperationPolicy(typeof(ListInvitableRolesQuery))]
    [ProducesResponseType<InvitableRoleDirectory>(200)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || !await AdministrationHttpInput.ReadHasNoBodyAsync(HttpContext)) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(new ListInvitableRolesQuery(), cancellationToken);
        return result.IsFailure ? result.ToActionResult() : Ok(new InvitableRoleDirectory(result.Value.Items.Select(row =>
            new RoleSummaryResponse(row.RoleId.ToString("D"), row.BuiltInKey, row.CapabilityScope, AdministrationHttpInput.Version(row.Version))).ToArray()));
    }
}
