using System.Globalization;
using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Api.Idempotency;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Tenancy;

public sealed record ConsentScopeResponse([property: PiiSensitive] string MembershipId, string TenantId, string? OrganizationId)
{ public override string ToString() => nameof(ConsentScopeResponse); }
public sealed record ConsentScopePageResponse(IReadOnlyList<ConsentScopeResponse> Items, PageInfo PageInfo);
public sealed record ConsentDetailsResponse([property: PiiSensitive] string EvidenceId, string Sequence, bool IsGranted,
    [property: PiiSensitive] string MembershipId, [property: PiiSensitive] string DefinitionId, int DefinitionVersion,
    [property: PiiSensitive] string PurposeKey, [property: PiiSensitive] string NoticeLocale,
    [property: PiiSensitive] string NoticeHash, [property: PiiSensitive] string NoticeText,
    [property: PiiSensitive] IReadOnlyDictionary<string, string> Labels)
{ public override string ToString() => nameof(ConsentDetailsResponse); }
public sealed record ConsentHistoryPageResponse(IReadOnlyList<ConsentDetailsResponse> Items, PageInfo PageInfo);
public sealed record ConsentWithdrawalResponse([property: PiiSensitive] string EvidenceId, string Sequence, bool IsGranted)
{ public override string ToString() => nameof(ConsentWithdrawalResponse); }

[Route("tenancy/membership-field-consents")]
public sealed class ConsentSubjectController(ISender sender, ITenantContext tenant) : ApiControllerBase
{
    [HttpGet("own/scopes", Name = "ListOwnFieldConsentScopes")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "tenancy.membership_field_consent.scopes")]
    [OperationPolicy(typeof(ListOwnFieldConsentScopesQuery))]
    [ProducesResponseType<ConsentScopePageResponse>(200)]
    public async Task<IActionResult> Scopes(CancellationToken cancellationToken)
    {
        if (HttpContext.Features.Get<ConsentSubjectRequest>()?.Request is not ListOwnFieldConsentScopesQuery request) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(request, cancellationToken);
        if (result.IsFailure) return result.ToActionResult();
        return Ok(new ConsentScopePageResponse(result.Value.Items.Select(x => new ConsentScopeResponse(x.MembershipId.ToString("D"),
            x.TenantId.Value.ToString("D"), x.OrganizationId?.Value.ToString("D"))).ToArray(),
            new(result.Value.NextCursor, null, result.Value.NextCursor is not null, false)));
    }
    [HttpGet("own/history", Name = "ListOwnMembershipFieldConsents")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "tenancy.membership_field_consent.history")]
    [OperationPolicy(typeof(ListOwnMembershipFieldConsentsQuery))]
    [ProducesResponseType<ConsentHistoryPageResponse>(200)]
    public async Task<IActionResult> History(CancellationToken cancellationToken)
    {
        if (HttpContext.Features.Get<ConsentSubjectRequest>()?.Request is not ListOwnMembershipFieldConsentsQuery request) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(request, cancellationToken);
        if (result.IsFailure) return result.ToActionResult();
        return Ok(new ConsentHistoryPageResponse(result.Value.Items.Select(x => new ConsentDetailsResponse(x.EvidenceId.ToString("D"),
            x.Sequence.ToString(CultureInfo.InvariantCulture), x.IsGranted, x.MembershipId.ToString("D"), x.DefinitionId.ToString("D"),
            x.DefinitionVersion, x.PurposeKey, x.NoticeLocale, x.NoticeHash, x.NoticeText ?? throw new InvalidOperationException("Consent metadata is unavailable."), x.Labels ?? throw new InvalidOperationException("Consent metadata is unavailable."))).ToArray(),
            new(result.Value.NextCursor, null, result.Value.NextCursor is not null, false)));
    }
    [HttpPost("withdrawals", Name = "WithdrawOwnMembershipFieldConsent")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "tenancy.membership_field_consent.withdraw")]
    [OperationPolicy(typeof(WithdrawOwnMembershipFieldConsentCommand))]
    [Idempotent(ProtectedAdministrationOperation.WithdrawOwnFieldConsent)]
    [ProducesResponseType<ConsentWithdrawalResponse>(200)]
    public async Task<IActionResult> Withdraw(CancellationToken cancellationToken)
    {
        if (HttpContext.Features.Get<ConsentSubjectRequest>()?.Request is not WithdrawOwnMembershipFieldConsentCommand request) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant, request, cancellationToken);
    }
}
