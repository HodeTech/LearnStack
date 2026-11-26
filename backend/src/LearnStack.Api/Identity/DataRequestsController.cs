using System.Security.Cryptography;
using System.Text.Json;
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
/// The subject's own data requests through a live session (ADR-0106 § 3, § 9; ADR-0107 § 4, § 5): key-less
/// <c>OwnDataRequest</c> operations confined to the session's group. An export, an erasure and an account closure are
/// filed on distinct routes; reading, listing and cancelling a request are the same for every kind. The ticket and the chunk travel only in request
/// and response bodies, never in a URL, and neither is keyed, so no secret reaches an idempotency receipt.
/// </summary>
[Route("identity/me/data-requests")]
public sealed class OwnDataRequestsController(ISender sender, ITenantContext tenant) : ApiControllerBase
{
    [HttpPost("exports", Name = "CreateOwnDataExport")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.OwnDataExportCreateOperation)]
    [OperationPolicy(typeof(CreateOwnDataExportCommand))]
    [Idempotent(ProtectedAdministrationOperation.CreateOwnDataExport)]
    [ProducesResponseType<DataRequestAcceptedResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        using var body = await AdministrationHttpInput.ReadObjectAsync(HttpContext);
        if (body is null) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant, new CreateOwnDataExportCommand(), cancellationToken);
    }

    [HttpPost("erasures", Name = "CreateOwnDataErasure")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.OwnDataErasureCreateOperation)]
    [OperationPolicy(typeof(CreateOwnDataErasureCommand))]
    [Idempotent(ProtectedAdministrationOperation.CreateOwnDataErasure)]
    [ProducesResponseType<DataErasureAcceptedResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> CreateErasure(CancellationToken cancellationToken)
    {
        using var body = await AdministrationHttpInput.ReadObjectAsync(HttpContext);
        if (body is null) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant, new CreateOwnDataErasureCommand(), cancellationToken);
    }

    /// <summary>
    /// ADR-0107 § 7: the person alone files the closure of their whole account from the session's group. Distinct from
    /// an erasure by its route, its operation and its accepted resource; the administrator surface has no closure route.
    /// </summary>
    [HttpPost("closures", Name = "CreateOwnAccountClosure")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.OwnAccountClosureCreateOperation)]
    [OperationPolicy(typeof(CreateOwnAccountClosureCommand))]
    [Idempotent(ProtectedAdministrationOperation.CreateOwnAccountClosure)]
    [ProducesResponseType<AccountClosureAcceptedResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> CreateClosure(CancellationToken cancellationToken)
    {
        using var body = await AdministrationHttpInput.ReadObjectAsync(HttpContext);
        if (body is null) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant, new CreateOwnAccountClosureCommand(), cancellationToken);
    }

    [HttpGet(Name = "ListOwnDataRequests")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.OwnDataRequestListOperation)]
    [OperationPolicy(typeof(ListOwnDataRequestsQuery))]
    [ProducesResponseType<OwnDataRequestPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (!AdministrationHttpInput.TryPage(Request, out var cursor, out var limit)
            || !await AdministrationHttpInput.ReadHasNoBodyAsync(HttpContext)) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(new ListOwnDataRequestsQuery(cursor, limit), cancellationToken);
        return result.IsFailure ? result.ToActionResult()
            : Ok(new OwnDataRequestPage(result.Value.Items.Select(DataRequestProjection.Own).ToArray(), result.Value.PageInfo));
    }

    [HttpGet("{id}", Name = "GetOwnDataRequest")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.OwnDataRequestReadOperation)]
    [OperationPolicy(typeof(GetOwnDataRequestQuery))]
    [ProducesResponseType<OwnDataRequestResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken)
    {
        if (!AdministrationHttpInput.TryId(id, out var target) || Request.Query.Count != 0
            || !await AdministrationHttpInput.ReadHasNoBodyAsync(HttpContext)) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(new GetOwnDataRequestQuery(target), cancellationToken);
        if (result.IsFailure) return result.ToActionResult();
        Response.Headers.ETag = EntityTag.For(result.Value.Version);
        return Ok(DataRequestProjection.Own(result.Value));
    }

    [HttpPost("{id}/cancellations", Name = "CancelOwnDataRequest")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.OwnDataRequestCancelOperation)]
    [OperationPolicy(typeof(CancelOwnDataRequestCommand))]
    [Idempotent(ProtectedAdministrationOperation.CancelOwnDataRequest)]
    [ProducesResponseType<AdministrationVersionReceipt>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(string id, CancellationToken cancellationToken)
    {
        using var body = await AdministrationHttpInput.ReadObjectAsync(HttpContext);
        if (body is null || !AdministrationHttpInput.TryId(id, out var target) || !ProtectedEntityTag.TryRead(Request, out var version)) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant,
            new CancelOwnDataRequestCommand(target, version), cancellationToken);
    }

    [HttpPost("{id}/artifact/revocations", Name = "RevokeOwnDataExport")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.OwnDataExportRevokeOperation)]
    [OperationPolicy(typeof(RevokeOwnDataExportCommand))]
    [Idempotent(ProtectedAdministrationOperation.RevokeOwnDataExport)]
    [ProducesResponseType<AdministrationVersionReceipt>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Revoke(string id, CancellationToken cancellationToken)
    {
        using var body = await AdministrationHttpInput.ReadObjectAsync(HttpContext);
        if (body is null || !AdministrationHttpInput.TryId(id, out var target) || !ProtectedEntityTag.TryRead(Request, out var version)) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant,
            new RevokeOwnDataExportCommand(target, version), cancellationToken);
    }
}

/// <summary>
/// ADR-0107 § 4: the subject mints a download cursor over their own available artifact. Not keyed and not conditional:
/// the answer carries a secret that is returned once and never retained for a replay.
/// </summary>
[Route("identity/me/data-requests")]
public sealed class OwnDataExportTicketsController(ISender sender) : ApiControllerBase
{
    [HttpPost("{id}/artifact/tickets", Name = "MintOwnDataExportTicket")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.OwnDataExportTicketOperation)]
    [OperationPolicy(typeof(MintOwnDataExportTicketCommand))]
    [ProducesResponseType<DataExportTicketResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MintTicket(string id, CancellationToken cancellationToken)
    {
        // The body is the empty object, but it is read through the clearing reader like the chunk body it pairs with.
        using var body = await AdministrationHttpInput.ReadSensitiveObjectAsync(HttpContext);
        if (body is null || !AdministrationHttpInput.TryId(id, out var target) || DataExportDownloadInput.HasPrecondition(Request)) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(new MintOwnDataExportTicketCommand(target), cancellationToken);
        return result.IsFailure ? result.ToActionResult()
            : Ok(DataRequestProjection.Ticket(result.Value));
    }
}

/// <summary>
/// ADR-0107 § 4: one decrypted chunk through a ticket presented in the body, never in the URL. Not keyed and not
/// conditional; the request body's read buffer and the plaintext buffer are cleared once used (best effort: managed
/// strings and the host's own buffers are beyond reach).
/// </summary>
[Route("identity/me/data-requests")]
public sealed class OwnDataExportChunksController(ISender sender) : ApiControllerBase
{
    [HttpPost("{id}/artifact/chunks", Name = "ReadOwnDataExportChunk")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.OwnDataExportChunkOperation)]
    [OperationPolicy(typeof(ReadOwnDataExportChunkCommand))]
    [ProducesResponseType<DataExportChunkResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ReadChunk(string id, CancellationToken cancellationToken)
    {
        // The ticket is a secret: its read buffer is overwritten when the body is disposed.
        using var body = await AdministrationHttpInput.ReadSensitiveObjectAsync(HttpContext, "ticket", "index");
        if (body is null || !AdministrationHttpInput.TryId(id, out var target) || DataExportDownloadInput.HasPrecondition(Request)
            || !AdministrationHttpInput.TryText(body.RootElement, "ticket", false, 64, out var ticket)
            || body.RootElement.GetProperty("index") is not { ValueKind: JsonValueKind.Number } index
            || !index.TryGetInt32(out var position) || position < 0
            || !string.Equals(index.GetRawText(), position.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal)) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(new ReadOwnDataExportChunkCommand(target, ticket!, position), cancellationToken);
        return result.IsFailure ? result.ToActionResult() : new DataExportChunkResult(result.Value);
    }
}

internal static class DataExportDownloadInput
{
    /// <summary>The ticket operations are not conditional and not keyed: a precondition or key on them is a client error.</summary>
    internal static bool HasPrecondition(HttpRequest request) =>
        request.Headers.IfMatch.Count != 0 || request.Headers.IfNoneMatch.Count != 0
        || request.Headers.ContainsKey(IdempotentAttribute.HeaderName);
}

/// <summary>
/// An administrator's data requests for the group (ADR-0106 § 3, § 6, § 13): Protected on the existing
/// <c>identity.membership.admin</c> alternatives. Status and counts only, never content, never a ticket.
/// </summary>
[Route("identity/data-requests")]
public sealed class DataRequestsController(ISender sender, ITenantContext tenant) : ApiControllerBase
{
    [HttpPost("exports", Name = "CreateDataExportForSubject")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.DataExportCreateOperation)]
    [OperationPolicy(typeof(CreateDataExportForSubjectCommand))]
    [Idempotent(ProtectedAdministrationOperation.CreateDataExportForSubject)]
    [ProducesResponseType<DataRequestAcceptedResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        using var body = await AdministrationHttpInput.ReadObjectAsync(HttpContext, "membershipId");
        if (body is null || !AdministrationHttpInput.TryText(body.RootElement, "membershipId", false, 36, out var text)
            || !AdministrationHttpInput.TryId(text, out var membership)) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant,
            new CreateDataExportForSubjectCommand(membership), cancellationToken);
    }

    [HttpPost("erasures", Name = "CreateDataErasureForSubject")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.DataErasureCreateOperation)]
    [OperationPolicy(typeof(CreateDataErasureForSubjectCommand))]
    [Idempotent(ProtectedAdministrationOperation.CreateDataErasureForSubject)]
    [ProducesResponseType<DataErasureAcceptedResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> CreateErasure(CancellationToken cancellationToken)
    {
        using var body = await AdministrationHttpInput.ReadObjectAsync(HttpContext, "membershipId");
        if (body is null || !AdministrationHttpInput.TryText(body.RootElement, "membershipId", false, 36, out var text)
            || !AdministrationHttpInput.TryId(text, out var membership)) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant,
            new CreateDataErasureForSubjectCommand(membership), cancellationToken);
    }

    [HttpGet(Name = "ListDataRequests")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.DataRequestListOperation)]
    [OperationPolicy(typeof(ListDataRequestsQuery))]
    [ProducesResponseType<DataRequestPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (!AdministrationHttpInput.TryPage(Request, out var cursor, out var limit)
            || !await AdministrationHttpInput.ReadHasNoBodyAsync(HttpContext)) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(new ListDataRequestsQuery(cursor, limit), cancellationToken);
        return result.IsFailure ? result.ToActionResult()
            : Ok(new DataRequestPage(result.Value.Items.Select(DataRequestProjection.Administration).ToArray(), result.Value.PageInfo));
    }

    [HttpGet("{id}", Name = "GetDataRequest")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.DataRequestReadOperation)]
    [OperationPolicy(typeof(GetDataRequestQuery))]
    [ProducesResponseType<DataRequestResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken)
    {
        if (!AdministrationHttpInput.TryId(id, out var target) || Request.Query.Count != 0
            || !await AdministrationHttpInput.ReadHasNoBodyAsync(HttpContext)) return AdministrationHttpInput.Invalid();
        var result = await sender.Send(new GetDataRequestQuery(target), cancellationToken);
        if (result.IsFailure) return result.ToActionResult();
        Response.Headers.ETag = EntityTag.For(result.Value.Version);
        return Ok(DataRequestProjection.Administration(result.Value));
    }

    [HttpPost("{id}/cancellations", Name = "CancelDataRequest")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.DataRequestCancelOperation)]
    [OperationPolicy(typeof(CancelDataRequestCommand))]
    [Idempotent(ProtectedAdministrationOperation.CancelDataRequest)]
    [ProducesResponseType<AdministrationVersionReceipt>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(string id, CancellationToken cancellationToken)
    {
        using var body = await AdministrationHttpInput.ReadObjectAsync(HttpContext);
        if (body is null || !AdministrationHttpInput.TryId(id, out var target) || !ProtectedEntityTag.TryRead(Request, out var version)) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant,
            new CancelDataRequestCommand(target, version), cancellationToken);
    }

    [HttpPost("{id}/artifact/revocations", Name = "RevokeDataExport")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.DataExportRevokeOperation)]
    [OperationPolicy(typeof(RevokeDataExportCommand))]
    [Idempotent(ProtectedAdministrationOperation.RevokeDataExport)]
    [ProducesResponseType<AdministrationVersionReceipt>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Revoke(string id, CancellationToken cancellationToken)
    {
        using var body = await AdministrationHttpInput.ReadObjectAsync(HttpContext);
        if (body is null || !AdministrationHttpInput.TryId(id, out var target) || !ProtectedEntityTag.TryRead(Request, out var version)) return AdministrationHttpInput.Invalid();
        return await ProtectedAdministrationResult.ExecuteAsync(HttpContext, sender, tenant,
            new RevokeDataExportCommand(target, version), cancellationToken);
    }
}

/// <summary>
/// Writes one decrypted chunk as <c>{index, count, bytes}</c> straight from the plaintext buffer, then clears the
/// buffer (ADR-0107 § 4): the base64 is encoded into the response without an intermediate string, and the buffer is
/// zeroed whether or not the write completes.
/// </summary>
internal sealed class DataExportChunkResult(DataExportChunkView chunk) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var response = context.HttpContext.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "application/json; charset=utf-8";
            response.Headers.CacheControl = "no-store";
            await using var writer = new Utf8JsonWriter(response.Body);
            writer.WriteStartObject();
            writer.WriteNumber("index", chunk.Index);
            writer.WriteNumber("count", chunk.Count);
            writer.WriteBase64String("bytes", chunk.Bytes);
            writer.WriteEndObject();
            await writer.FlushAsync(context.HttpContext.RequestAborted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(chunk.Bytes);
        }
    }
}
