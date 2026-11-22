using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Api.Idempotency;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Identity;

/// <summary>One retained group of the subject on the real host: its tenant and organization, or none for the tenant-wide group.</summary>
public sealed record RetainedDataRequestScopeResponse(string TenantId, string? OrganizationId);

public sealed record RetainedDataRequestScopePageResponse(IReadOnlyList<RetainedDataRequestScopeResponse> Items, PageInfo PageInfo);

/// <summary>
/// The retained own-scope native entry of the subject's data requests (ADR-0106 § 9, ADR-0107 § 4): six key-less
/// <c>OwnDataRequestEntry</c> operations reached only through the selector-free native session source, for a person
/// whose Membership is Suspended, Revoked or gone. The request each action sends is the exact one the
/// <see cref="OwnDataRequestSubjectSource"/> captured before authorization, never a fresh binding of the input, so the
/// witness admits nothing the source did not see. The ticket and the chunk travel only in request and response bodies.
/// </summary>
[Route("identity/data-request-entry")]
public sealed class DataRequestEntryController(ISender sender, ITenantContext tenant) : ApiControllerBase
{
    [HttpGet("scopes", Name = "ListRetainedDataRequestScopes")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.RetainedDataRequestScopesOperation)]
    [OperationPolicy(typeof(ListRetainedDataRequestScopesQuery))]
    [ProducesResponseType<RetainedDataRequestScopePageResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Scopes(CancellationToken cancellationToken)
    {
        if (HttpContext.Features.Get<OwnDataRequestEntryRequest>()?.Request is not ListRetainedDataRequestScopesQuery request)
            return AdministrationHttpInput.Invalid();
        var result = await sender.Send(request, cancellationToken);
        if (result.IsFailure) return result.ToActionResult();
        return Ok(new RetainedDataRequestScopePageResponse(result.Value.Items.Select(group => new RetainedDataRequestScopeResponse(
                group.TenantId.Value.ToString("D"), group.OrganizationId?.Value.ToString("D"))).ToArray(),
            new(result.Value.NextCursor, null, result.Value.NextCursor is not null, false)));
    }

    [HttpGet("requests", Name = "ListRetainedDataRequests")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.RetainedDataRequestListOperation)]
    [OperationPolicy(typeof(ListRetainedDataRequestsQuery))]
    [ProducesResponseType<OwnDataRequestPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Requests(CancellationToken cancellationToken)
    {
        if (HttpContext.Features.Get<OwnDataRequestEntryRequest>()?.Request is not ListRetainedDataRequestsQuery request)
            return AdministrationHttpInput.Invalid();
        var result = await sender.Send(request, cancellationToken);
        return result.IsFailure ? result.ToActionResult()
            : Ok(new OwnDataRequestPage(result.Value.Items.Select(DataRequestProjection.Own).ToArray(), result.Value.PageInfo));
    }

    [HttpPost("exports", Name = "CreateRetainedDataExport")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.RetainedDataExportCreateOperation)]
    [OperationPolicy(typeof(CreateRetainedDataExportCommand))]
    [Idempotent(ProtectedAdministrationOperation.CreateRetainedDataExport)]
    [ProducesResponseType<DataRequestAcceptedResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> CreateExport(CancellationToken cancellationToken)
    {
        if (HttpContext.Features.Get<OwnDataRequestEntryRequest>()?.Request is not CreateRetainedDataExportCommand request)
            return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant, request, cancellationToken);
    }

    [HttpPost("cancellations", Name = "CancelRetainedDataRequest")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.RetainedDataRequestCancelOperation)]
    [OperationPolicy(typeof(CancelRetainedDataRequestCommand))]
    [Idempotent(ProtectedAdministrationOperation.CancelRetainedDataRequest)]
    [ProducesResponseType<AdministrationVersionReceipt>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(CancellationToken cancellationToken)
    {
        if (HttpContext.Features.Get<OwnDataRequestEntryRequest>()?.Request is not CancelRetainedDataRequestCommand request)
            return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant, request, cancellationToken);
    }

    [HttpPost("tickets", Name = "MintRetainedDataExportTicket")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.RetainedDataExportTicketOperation)]
    [OperationPolicy(typeof(MintRetainedDataExportTicketCommand))]
    [ProducesResponseType<DataExportTicketResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MintTicket(CancellationToken cancellationToken)
    {
        if (HttpContext.Features.Get<OwnDataRequestEntryRequest>()?.Request is not MintRetainedDataExportTicketCommand request)
            return AdministrationHttpInput.Invalid();
        var result = await sender.Send(request, cancellationToken);
        return result.IsFailure ? result.ToActionResult()
            : Ok(new DataExportTicketResponse(result.Value.Ticket, DataRequestProjection.Instant(result.Value.ExpiresAt),
                result.Value.ChunkCount, result.Value.ByteCount));
    }

    [HttpPost("chunks", Name = "ReadRetainedDataExportChunk")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.RetainedDataExportChunkOperation)]
    [OperationPolicy(typeof(ReadRetainedDataExportChunkCommand))]
    [ProducesResponseType<DataExportChunkResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ReadChunk(CancellationToken cancellationToken)
    {
        if (HttpContext.Features.Get<OwnDataRequestEntryRequest>()?.Request is not ReadRetainedDataExportChunkCommand request)
            return AdministrationHttpInput.Invalid();
        var result = await sender.Send(request, cancellationToken);
        return result.IsFailure ? result.ToActionResult() : new DataExportChunkResult(result.Value);
    }
}
