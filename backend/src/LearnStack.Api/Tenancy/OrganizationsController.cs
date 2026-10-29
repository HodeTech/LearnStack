using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Api.Idempotency;
using LearnStack.Api.Identity;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Tenancy;

[Route("tenancy/organizations")]
public sealed class OrganizationsController(ISender sender, ITenantContext tenant) : ApiControllerBase
{
    [HttpGet(Name = "ListOrganizations")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "tenancy.organization.list")]
    [OperationPolicy(typeof(ListOrganizationsQuery))]
    [ProducesResponseType<OrganizationDirectoryResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (!AdministrationHttpInput.TryPage(Request, out var cursor, out var limit)
            || !await AdministrationHttpInput.ReadHasNoBodyAsync(HttpContext)) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(new ListOrganizationsQuery(cursor, limit), cancellationToken);
        if (result.IsFailure) return result.ToActionResult();
        return Ok(new OrganizationDirectoryResponse(result.Value.Items.Select(item => new OrganizationResponse(
            item.OrganizationId.Value.ToString("D"), item.Slug, item.DisplayName, item.Status.ToString(), item.IsDefault,
            AdministrationHttpInput.Version(item.Version))).ToArray(), result.Value.DefaultOrganizationId?.Value.ToString("D"),
            AdministrationHttpInput.Version(result.Value.TenantVersion), result.Value.PageInfo));
    }

    [HttpGet("{id}", Name = "GetOrganizationDetail")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "tenancy.organization.read")]
    [OperationPolicy(typeof(GetOrganizationDetailQuery))]
    [ProducesResponseType<OrganizationResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken)
    {
        if (!AdministrationHttpInput.TryId(id, out var target) || Request.Query.Count != 0
            || !await AdministrationHttpInput.ReadHasNoBodyAsync(HttpContext)) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(new GetOrganizationDetailQuery(OrganizationId.From(target)), cancellationToken);
        if (result.IsFailure) return result.ToActionResult();
        var value = result.Value;
        Response.Headers.ETag = EntityTag.For(value.Version);
        return Ok(new OrganizationResponse(value.OrganizationId.Value.ToString("D"), value.Slug, value.DisplayName,
            value.Status.ToString(), value.IsDefault, AdministrationHttpInput.Version(value.Version)));
    }

    [HttpPost(Name = "CreateManagedOrganization")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "tenancy.organization.create")]
    [OperationPolicy(typeof(CreateManagedOrganizationCommand))]
    [Idempotent(ProtectedAdministrationOperation.CreateOrganization)]
    [ProducesResponseType<AdministrationVersionReceipt>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        using var body = await AdministrationHttpInput.ReadObjectAsync(HttpContext, "slug", "displayName");
        if (body is null || Request.Headers.IfMatch.Count != 0
            || !AdministrationHttpInput.TryText(body.RootElement, "slug", false, UrlSlug.MaxLength, out var slug)
            || !AdministrationHttpInput.TryText(body.RootElement, "displayName", false, MappedLength.DisplayName, out var name))
            return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant,
            new CreateManagedOrganizationCommand(slug!, name!), cancellationToken);
    }

    [HttpPut("{id}", Name = "RenameOrganization")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "tenancy.organization.rename")]
    [OperationPolicy(typeof(RenameOrganizationCommand))]
    [Idempotent(ProtectedAdministrationOperation.RenameOrganization)]
    [ProducesResponseType<AdministrationVersionReceipt>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Rename(string id, CancellationToken cancellationToken)
    {
        using var body = await AdministrationHttpInput.ReadObjectAsync(HttpContext, "displayName");
        if (body is null || !AdministrationHttpInput.TryId(id, out var target) || !ProtectedEntityTag.TryRead(Request, out var version)
            || !AdministrationHttpInput.TryText(body.RootElement, "displayName", false, MappedLength.DisplayName, out var name))
            return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant,
            new RenameOrganizationCommand(OrganizationId.From(target), version, name!), cancellationToken);
    }

    [HttpPost("{id}/archive", Name = "ArchiveOrganization")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "tenancy.organization.archive")]
    [OperationPolicy(typeof(ArchiveOrganizationCommand))]
    [Idempotent(ProtectedAdministrationOperation.ArchiveOrganization)]
    [ProducesResponseType<AdministrationVersionReceipt>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Archive(string id, CancellationToken cancellationToken)
    {
        using var body = await AdministrationHttpInput.ReadObjectAsync(HttpContext);
        if (body is null || !AdministrationHttpInput.TryId(id, out var target) || !ProtectedEntityTag.TryRead(Request, out var version))
            return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant,
            new ArchiveOrganizationCommand(OrganizationId.From(target), version), cancellationToken);
    }
}
