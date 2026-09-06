using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Customization.Application.Contracts.Customization;

/// <summary>
/// Declares the ambient tenant's level or difficulty vocabulary, with its bands —
/// a draft, not yet live.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bands arrive with the taxonomy rather than one command at a time.</b> An
/// item is inside this aggregate, not beside it: it has no identity outside the
/// revision that names it, and a vocabulary with no bands is not a smaller
/// vocabulary — it is an unfinished one. Editing the bands of a draft afterwards
/// is the Admin Studio's work and lands with the taxonomy editor in
/// <see href="../../../../../../docs/roadmap/phase-05-education-learning-content.md">Phase 05</see>,
/// consolidated into one editing idiom in
/// <see href="../../../../../../docs/roadmap/phase-06-renderer-admin-studio.md">Phase 06</see>.
/// </para>
/// <para>
/// <b>The tenant comes from the context, never from the request</b> — the same rule
/// and the same reason as
/// <see cref="RegisterTenantContentTypeCommand"/>.
/// </para>
/// </remarks>
/// <param name="TaxonomyId">Assigned by the caller, so a retry is idempotent.</param>
/// <param name="Key">Lowercase kebab-case, the concept's name within the tenant.</param>
/// <param name="SchemaVersion">The revision this vocabulary is; never re-issued.</param>
/// <param name="DisplayName">Locale tag to text — a Pattern B localized map.</param>
/// <param name="Items">The bands, in the order the tenant authored them.</param>
public sealed record RegisterTenantLevelTaxonomyCommand(
    Guid TaxonomyId,
    string Key,
    int SchemaVersion,
    IReadOnlyDictionary<string, string> DisplayName,
    IReadOnlyList<TaxonomyItemInput> Items) : IRequest<Result<TenantLevelTaxonomyDto>>;

/// <summary>One band of a level taxonomy, as submitted.</summary>
/// <param name="Key">Lowercase kebab-case, unique within the revision.</param>
/// <param name="DisplayName">
/// The band's name. It is the display name and not a separate label:
/// <see href="../../../../../../docs/decisions/0018-tenant-driven-customization-model.md">ADR-0018's
/// 2026-09-04 Amendment</see> settles that a band's name <i>is</i> its
/// <c>display_name</c>, so `a1` renders as whatever the tenant translated it to.
/// </param>
/// <param name="Sort">Render order; unique within the revision.</param>
/// <param name="Metadata">Opaque tenant-authored JSON, or nothing.</param>
public sealed record TaxonomyItemInput(
    string Key,
    IReadOnlyDictionary<string, string> DisplayName,
    short Sort,
    string? Metadata = null);

/// <summary>
/// Makes a drafted taxonomy the live answer for its key, retiring the revision it
/// succeeds.
/// </summary>
/// <remarks>
/// Same shape and same reason as
/// <see cref="PublishTenantContentTypeCommand"/>: one live revision per key, the
/// incumbent retired in this transaction, and the partial index as the guarantee.
/// </remarks>
public sealed record PublishTenantLevelTaxonomyCommand(
    Guid TaxonomyId) : IRequest<Result<TenantLevelTaxonomyDto>>;

/// <summary>What the caller now has.</summary>
public sealed record TenantLevelTaxonomyDto(
    Guid TaxonomyId,
    TenantId TenantId,
    string Key,
    int SchemaVersion,
    string Status,
    int ItemCount);
