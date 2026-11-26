using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Api.Idempotency;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Identity;

/// <summary>
/// The retained own-scope native entry of the subject's data requests (ADR-0106 § 9, ADR-0107 § 4, § 5, § 7): nine key-less
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
        if (HttpContext.Features.Get<OwnDataRequestEntryRequest>()?.Request is not ListRetainedDataRequestScopesQuery request) return AdministrationHttpInput.Invalid();
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
        if (HttpContext.Features.Get<OwnDataRequestEntryRequest>()?.Request is not ListRetainedDataRequestsQuery request) return AdministrationHttpInput.Invalid();
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
        if (HttpContext.Features.Get<OwnDataRequestEntryRequest>()?.Request is not CreateRetainedDataExportCommand request) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant, request, cancellationToken);
    }

    [HttpPost("erasures", Name = "CreateRetainedDataErasure")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.RetainedDataErasureCreateOperation)]
    [OperationPolicy(typeof(CreateRetainedDataErasureCommand))]
    [Idempotent(ProtectedAdministrationOperation.CreateRetainedDataErasure)]
    [ProducesResponseType<DataErasureAcceptedResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> CreateErasure(CancellationToken cancellationToken)
    {
        if (HttpContext.Features.Get<OwnDataRequestEntryRequest>()?.Request is not CreateRetainedDataErasureCommand request) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant, request, cancellationToken);
    }

    [HttpPost("closures", Name = "CreateRetainedAccountClosure")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.RetainedAccountClosureCreateOperation)]
    [OperationPolicy(typeof(CreateRetainedAccountClosureCommand))]
    [Idempotent(ProtectedAdministrationOperation.CreateRetainedAccountClosure)]
    [ProducesResponseType<AccountClosureAcceptedResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> CreateClosure(CancellationToken cancellationToken)
    {
        if (HttpContext.Features.Get<OwnDataRequestEntryRequest>()?.Request is not CreateRetainedAccountClosureCommand request) return AdministrationHttpInput.Invalid();
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
        if (HttpContext.Features.Get<OwnDataRequestEntryRequest>()?.Request is not CancelRetainedDataRequestCommand request) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant, request, cancellationToken);
    }

    [HttpPost("revocations", Name = "RevokeRetainedDataExport")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.RetainedDataExportRevokeOperation)]
    [OperationPolicy(typeof(RevokeRetainedDataExportCommand))]
    [Idempotent(ProtectedAdministrationOperation.RevokeRetainedDataExport)]
    [ProducesResponseType<AdministrationVersionReceipt>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RevokeExport(CancellationToken cancellationToken)
    {
        if (HttpContext.Features.Get<OwnDataRequestEntryRequest>()?.Request is not RevokeRetainedDataExportCommand request) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant, request, cancellationToken);
    }

    [HttpPost("tickets", Name = "MintRetainedDataExportTicket")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.RetainedDataExportTicketOperation)]
    [OperationPolicy(typeof(MintRetainedDataExportTicketCommand))]
    [ProducesResponseType<DataExportTicketResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MintTicket(CancellationToken cancellationToken)
    {
        if (HttpContext.Features.Get<OwnDataRequestEntryRequest>()?.Request is not MintRetainedDataExportTicketCommand request) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(request, cancellationToken);
        return result.IsFailure ? result.ToActionResult()
            : Ok(DataRequestProjection.Ticket(result.Value));
    }

    [HttpPost("chunks", Name = "ReadRetainedDataExportChunk")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.RetainedDataExportChunkOperation)]
    [OperationPolicy(typeof(ReadRetainedDataExportChunkCommand))]
    [ProducesResponseType<DataExportChunkResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ReadChunk(CancellationToken cancellationToken)
    {
        if (HttpContext.Features.Get<OwnDataRequestEntryRequest>()?.Request is not ReadRetainedDataExportChunkCommand request) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(request, cancellationToken);
        return result.IsFailure ? result.ToActionResult() : new DataExportChunkResult(result.Value);
    }
}
