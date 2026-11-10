using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Api.Customization;
using LearnStack.Api.Idempotency;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace LearnStack.Api.Tenancy;

public sealed record OwnFieldConsentCurrentResponse(ConsentDetailsResponse? Consent);
[Route("tenancy/membership-field-consents")]
public sealed class OwnFieldConsentController(ISender sender, ITenantContext tenant) : ApiControllerBase
{
    [HttpGet("own", Name = "GetOwnMembershipFieldConsent")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "tenancy.membership_field_consent.get_own")]
    [OperationPolicy(typeof(GetOwnMembershipFieldConsentQuery))]
    [ProducesResponseType<OwnFieldConsentCurrentResponse>(200)]
    public async Task<IActionResult> Current(CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 3 || Request.Query.Any(p => p.Key is not ("membershipId" or "definitionId" or "definitionVersion") || p.Value.Count != 1)
            || !AdministrationHttpInput.TryId(Request.Query["membershipId"], out var member) || !AdministrationHttpInput.TryId(Request.Query["definitionId"], out var definition)
            || !int.TryParse(Request.Query["definitionVersion"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var revision)
            || revision < 1 || revision.ToString(System.Globalization.CultureInfo.InvariantCulture) != Request.Query["definitionVersion"]
            || !await AdministrationHttpInput.ReadHasNoBodyAsync(HttpContext)) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(new GetOwnMembershipFieldConsentQuery(member, definition, revision), cancellationToken); if (result.IsFailure) return result.ToActionResult();
        var x = result.Value.Consent;
        return Ok(new OwnFieldConsentCurrentResponse(x is null ? null : new(x.EvidenceId.ToString("D"), AdministrationHttpInput.Version(x.Sequence), x.IsGranted,
            x.MembershipId.ToString("D"), x.DefinitionId.ToString("D"), x.DefinitionVersion, x.PurposeKey, x.NoticeLocale, x.NoticeHash, x.NoticeText ?? throw new InvalidOperationException("Consent metadata is unavailable."), x.Labels ?? throw new InvalidOperationException("Consent metadata is unavailable."))));
    }
    [HttpPost("grants", Name = "GrantOwnMembershipFieldConsent")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "tenancy.membership_field_consent.grant")]
    [OperationPolicy(typeof(GrantOwnMembershipFieldConsentCommand))]
    [Idempotent(ProtectedAdministrationOperation.GrantOwnFieldConsent)]
    [ProducesResponseType<FieldConsentReceiptResponse>(200)]
    public async Task<IActionResult> Grant(CancellationToken cancellationToken)
    {
        using var body = await CustomFieldHttpInput.ReadAsync(HttpContext, false, "definitionId", "definitionVersion", "noticeLocale", "expectedNoticeHash", "expectedSequence", "expectedEvidenceId", "confirmed");
        if (body is null || !CustomFieldHttpInput.Id(body.RootElement, "definitionId", false, out var definition)
            || !CustomFieldHttpInput.Int(body.RootElement, "definitionVersion", out var revision) || revision < 1
            || !AdministrationHttpInput.TryText(body.RootElement, "noticeLocale", false, 64, out var locale)
            || !AdministrationHttpInput.TryText(body.RootElement, "expectedNoticeHash", false, 64, out var hash)
            || !CustomFieldHttpInput.Sequence(body.RootElement, "expectedSequence", out var sequence)
            || !CustomFieldHttpInput.Id(body.RootElement, "expectedEvidenceId", true, out var evidence)
            || !CustomFieldHttpInput.Bool(body.RootElement, "confirmed", out var confirmed)) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant, new GrantOwnMembershipFieldConsentCommand(definition!.Value, revision, locale!, hash!, sequence, evidence, confirmed), cancellationToken);
    }
}
