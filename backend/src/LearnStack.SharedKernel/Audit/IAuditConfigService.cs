using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Audit;

/// <summary>
/// Answers what a given operation's coverage <b>effectively</b> is, for one tenant.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two types, one distinction</b>
/// (<see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 Amendment
/// 3 § 4</see>): <see cref="OperationClass"/> is the tier the catalogue and the matrix
/// <em>declare</em>, and <see cref="AuditClassification"/> is what this returns after the
/// tenant's <c>audit_config</c> override and the MUST floor. They are not one enum with a
/// spare member — <c>OperationClass</c> is a persisted column and no row can legally hold
/// a value meaning "no row".
/// </para>
/// <para>
/// <b>It never returns <see cref="AuditClassification.Unclassified"/>.</b> That answer
/// belongs to <see cref="IAuditCatalog.TryGet"/> returning <c>false</c>, before this is
/// ever called — an unregistered request is refused rather than classified, and a
/// classifier that could also produce it would give the same rejection two sources.
/// </para>
/// <para>
/// <b>The override narrows and never elevates</b>
/// (<see href="../../../../docs/decisions/0033-audit-durability-model.md">ADR-0033
/// Amendment 4 § 1</see>): <c>is_enabled = false</c> silences a SHOULD or a MAY and does
/// nothing to a MUST; <c>true</c> is the baseline, which is also what an absent row means.
/// So the answer is either the declared tier or <see cref="AuditClassification.Off"/>.
/// </para>
/// </remarks>
public interface IAuditConfigService
{
    /// <summary>
    /// The effective coverage of <paramref name="entry"/> for <paramref name="tenantId"/>.
    /// </summary>
    /// <param name="tenantId">
    /// The tenant whose overrides apply, or <c>null</c> when the request has no resolved
    /// tenant — a provisioning command before its tenant exists. A null tenant has no
    /// overrides to read, so the declared tier stands.
    /// </param>
    /// <param name="entry">The catalogue entry being classified.</param>
    /// <param name="cancellationToken">The request's token.</param>
    /// <remarks>
    /// <b>A read failure does not reject the operation.</b> It falls back to the declared
    /// tier — the in-process catalogue carries the same MUST floor — logged at
    /// <c>Error</c> and surfaced on the audit health check. Rejecting every request
    /// platform-wide because a cache is unavailable is a worse compliance outcome than
    /// losing one tenant's narrowing, and nothing proceeds unaudited either way.
    /// </remarks>
    Task<AuditClassification> ClassifyAsync(
        TenantId? tenantId, AuditCatalogEntry entry, CancellationToken cancellationToken = default);
}
