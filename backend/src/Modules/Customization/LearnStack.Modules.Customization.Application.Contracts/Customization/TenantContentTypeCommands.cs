using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Customization.Application.Contracts.Customization;

/// <summary>
/// Declares a content shape the ambient tenant's own data will take — a draft,
/// not yet live.
/// </summary>
/// <remarks>
/// <para>
/// <b>The tenant comes from the context, never from the request.</b> This is the
/// ordinary rule that provisioning is the single exception to
/// (<see href="../../../../../../docs/decisions/0042-tenant-provisioning-cross-aggregate-transaction.md">ADR-0042</see>):
/// a request naming its own tenant would let a caller authenticated for tenant A
/// declare a content type in tenant B, and the policy would catch it only because
/// the announcement is A's.
/// </para>
/// <para>
/// <b>It lands as a <c>Draft</c>.</b> Registering and publishing are two commands
/// because they are two decisions: the tenant admin who authors a schema is not
/// necessarily the one who makes it the live answer for a key, and between the two
/// the document exists, is addressable, and constrains nothing.
/// </para>
/// <para>
/// <b><c>JsonSchema</c> passes all four of
/// <see href="../../../../../../docs/decisions/0043-customization-payload-validation.md">ADR-0043</see>'s
/// gates before a row is written</b>, and the handler is where they run — the
/// document is a tenant's, and a rejected one owes the author a refusal naming
/// where it failed, not a <c>500</c>.
/// </para>
/// <para>
/// <b>Why the identifiers here are <c>Guid</c> and not <c>TenantContentTypeId</c>.</b>
/// A command contract is the cross-module surface
/// (<see href="../../../../../../docs/decisions/0010-cross-module-communication.md">ADR-0010</see>),
/// and <c>TenantContentTypeId</c> lives in <c>Customization.Domain</c> — so a
/// contract naming it would put that assembly in the IL of every module that sends
/// the command, which is the forbidden <c>Module A → Module B.Domain</c> edge. The
/// Tenancy contracts have the same shape for the same reason: they name
/// <c>OrganizationId</c>, which is a <c>SharedKernel</c> identifier, and never
/// <c>TenantDomainId</c>, which is not. The handler constructs the typed id one
/// layer in, where the module's own types are in scope.
/// </para>
/// </remarks>
/// <param name="ContentTypeId">Assigned by the caller, so a retry is idempotent.</param>
/// <param name="Key">Lowercase kebab-case, the concept's name within the tenant.</param>
/// <param name="SchemaVersion">The revision this document is; never re-issued.</param>
/// <param name="DisplayName">Locale tag to text — a Pattern B localized map.</param>
/// <param name="JsonSchema">The draft 2020-12 document, as authored.</param>
/// <param name="RendererKey">One of the closed set of composite renderers.</param>
public sealed record RegisterTenantContentTypeCommand(
    Guid ContentTypeId,
    string Key,
    int SchemaVersion,
    IReadOnlyDictionary<string, string> DisplayName,
    string JsonSchema,
    string RendererKey) : IRequest<Result<TenantContentTypeDto>>;

/// <summary>
/// Makes a drafted content type the live answer for its key, retiring the
/// revision it succeeds.
/// </summary>
/// <remarks>
/// <b>It deprecates the incumbent in the same transaction.</b> The partial index
/// <c>UNIQUE (tenant_id, key) WHERE status = 'Active' AND deleted_at IS NULL</c>
/// admits one live revision per key, and an aggregate cannot see its siblings — so
/// the retirement is this command's work, and the index is what catches the case
/// where it did not happen.
/// </remarks>
public sealed record PublishTenantContentTypeCommand(
    Guid ContentTypeId) : IRequest<Result<TenantContentTypeDto>>;

/// <summary>What the caller now has.</summary>
public sealed record TenantContentTypeDto(
    Guid ContentTypeId,
    TenantId TenantId,
    string Key,
    int SchemaVersion,
    string Status);
