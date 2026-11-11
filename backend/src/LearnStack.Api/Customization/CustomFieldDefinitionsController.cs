using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Api.Idempotency;
using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Identifiers;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace LearnStack.Api.Customization;

[Route("customization/custom-field-definitions")]
public sealed class CustomFieldDefinitionsController(ISender sender, ITenantContext tenant, IGuidFactory ids) : ApiControllerBase
{
    [HttpGet(Name = "ListCustomFieldDefinitions")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "customization.custom_field.list")]
    [OperationPolicy(typeof(ListCustomFieldDefinitionsQuery))]
    [ProducesResponseType<CustomFieldDefinitionPageResponse>(200)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (Request.Query.Any(p => p.Key is not ("cursor" or "status") || p.Value.Count != 1) || !await AdministrationHttpInput.ReadHasNoBodyAsync(HttpContext)) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(new ListCustomFieldDefinitionsQuery(Request.Query["cursor"].FirstOrDefault(), Request.Query["status"].FirstOrDefault()), cancellationToken);
        if (result.IsFailure) return result.ToActionResult();
        return Ok(new CustomFieldDefinitionPageResponse(result.Value.Items.Select(item => CustomFieldResponseProjection.Definition(item.LatestDefinition ?? throw new InvalidOperationException("Custom field metadata is unavailable."))).ToArray(), CustomFieldResponseProjection.Page(result.Value.NextCursor)));
    }
    [HttpGet("{id}", Name = "GetCustomFieldDefinition")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "customization.custom_field.read")]
    [OperationPolicy(typeof(GetCustomFieldDefinitionQuery))]
    [ProducesResponseType<CustomFieldDefinitionResponse>(200)]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken)
    {
        if (!AdministrationHttpInput.TryId(id, out var target) || Request.Query.Count != 0 || !await AdministrationHttpInput.ReadHasNoBodyAsync(HttpContext)) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(new GetCustomFieldDefinitionQuery(target), cancellationToken); if (result.IsFailure) return result.ToActionResult();
        Response.Headers.ETag = EntityTag.For(result.Value.Version); return Ok(CustomFieldResponseProjection.Definition(result.Value));
    }
    [HttpGet("{id}/revisions", Name = "ListCustomFieldDefinitionRevisions")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "customization.custom_field.list_revisions")]
    [OperationPolicy(typeof(ListCustomFieldDefinitionRevisionsQuery))]
    [ProducesResponseType<CustomFieldDefinitionPageResponse>(200)]
    public async Task<IActionResult> Revisions(string id, CancellationToken cancellationToken)
    {
        if (!AdministrationHttpInput.TryId(id, out var target) || Request.Query.Any(p => p.Key != "cursor" || p.Value.Count != 1) || !await AdministrationHttpInput.ReadHasNoBodyAsync(HttpContext)) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(new ListCustomFieldDefinitionRevisionsQuery(target, Request.Query["cursor"].FirstOrDefault()), cancellationToken); if (result.IsFailure) return result.ToActionResult();
        return Ok(new CustomFieldDefinitionPageResponse(result.Value.Items.Select(CustomFieldResponseProjection.Definition).ToArray(), CustomFieldResponseProjection.Page(result.Value.NextCursor)));
    }
    [HttpPost(Name = "RegisterCustomFieldDefinition")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "customization.custom_field.register")]
    [OperationPolicy(typeof(RegisterCustomFieldDefinitionCommand))]
    [Idempotent(ProtectedAdministrationOperation.RegisterCustomFieldDefinition)]
    [ProducesResponseType<CustomFieldReceiptResponse>(201)]
    public async Task<IActionResult> Register(CancellationToken cancellationToken)
    {
        using var body = await CustomFieldHttpInput.ReadAsync(HttpContext, false, CustomFieldHttpInput.DefinitionFields);
        if (body is null || !CustomFieldHttpInput.TryDefinition(body.RootElement, out var input)) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant, new RegisterCustomFieldDefinitionCommand(ids.NewUuidV7(), input!), cancellationToken);
    }
    [HttpPut("{id}", Name = "ReviseCustomFieldDefinitionDraft")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "customization.custom_field.revise")]
    [OperationPolicy(typeof(ReviseCustomFieldDefinitionDraftCommand))]
    [Idempotent(ProtectedAdministrationOperation.ReviseCustomFieldDraft)]
    [ProducesResponseType<CustomFieldReceiptResponse>(200)]
    public async Task<IActionResult> Revise(string id, CancellationToken cancellationToken)
    {
        using var body = await CustomFieldHttpInput.ReadAsync(HttpContext, false, CustomFieldHttpInput.DefinitionFields);
        if (body is null || !AdministrationHttpInput.TryId(id, out var target) || !ProtectedEntityTag.TryRead(Request, out var version) || !CustomFieldHttpInput.TryDefinition(body.RootElement, out var input)) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant, new ReviseCustomFieldDefinitionDraftCommand(target, version, input!), cancellationToken);
    }
    [HttpPost("{id}/publication", Name = "PublishCustomFieldDefinition")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "customization.custom_field.publish")]
    [OperationPolicy(typeof(PublishCustomFieldDefinitionCommand))]
    [Idempotent(ProtectedAdministrationOperation.PublishCustomFieldRevision)]
    [ProducesResponseType<CustomFieldReceiptResponse>(200)]
    public async Task<IActionResult> Publish(string id, CancellationToken cancellationToken)
    {
        using var body = await CustomFieldHttpInput.ReadAsync(HttpContext, false, "expectedIncumbentId", "expectedIncumbentVersion");
        if (body is null || !AdministrationHttpInput.TryId(id, out var target) || !ProtectedEntityTag.TryRead(Request, out var version)
            || !CustomFieldHttpInput.Id(body.RootElement, "expectedIncumbentId", true, out var incumbent)) return AdministrationHttpInput.Invalid();
        var raw = body.RootElement.GetProperty("expectedIncumbentVersion"); long? expected = null;
        if (raw.ValueKind != System.Text.Json.JsonValueKind.Null) { if (raw.ValueKind != System.Text.Json.JsonValueKind.String || !ProtectedEntityTag.TryParseVersion(raw.GetString(), out var pin)) return AdministrationHttpInput.Invalid(); expected = pin; }
        if ((expected is null) != (incumbent is null)) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant, new PublishCustomFieldDefinitionCommand(target, version, incumbent, expected), cancellationToken);
    }
    [HttpPost("{id}/retirement", Name = "RetireCustomFieldDefinition")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "customization.custom_field.retire")]
    [OperationPolicy(typeof(RetireCustomFieldDefinitionCommand))]
    [Idempotent(ProtectedAdministrationOperation.RetireCustomFieldRevision)]
    [ProducesResponseType<CustomFieldReceiptResponse>(200)]
    public async Task<IActionResult> Retire(string id, CancellationToken cancellationToken) => await Transition(id, false, cancellationToken);
    [HttpPost("{id}/discard", Name = "DiscardCustomFieldDefinitionDraft")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "customization.custom_field.discard")]
    [OperationPolicy(typeof(DiscardCustomFieldDefinitionDraftCommand))]
    [Idempotent(ProtectedAdministrationOperation.DiscardCustomFieldDraft)]
    [ProducesResponseType<CustomFieldReceiptResponse>(200)]
    public async Task<IActionResult> Discard(string id, CancellationToken cancellationToken) => await Transition(id, true, cancellationToken);
    private async Task<IActionResult> Transition(string id, bool discard, CancellationToken token)
    {
        using var body = await CustomFieldHttpInput.ReadAsync(HttpContext, false);
        if (body is null || !AdministrationHttpInput.TryId(id, out var target) || !ProtectedEntityTag.TryRead(Request, out var version)) return AdministrationHttpInput.Invalid();
        object command = discard ? new DiscardCustomFieldDefinitionDraftCommand(target, version) : new RetireCustomFieldDefinitionCommand(target, version);
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant, command, token);
    }
}
