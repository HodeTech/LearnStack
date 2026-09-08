namespace LearnStack.SharedKernel.Audit;

/// <summary>
/// One tracked entity's before / after state, as the interceptor saw it.
/// </summary>
/// <remarks>
/// Produced by <c>AuditChangeTrackerInterceptor</c>, which <b>captures only</b>: it
/// constructs no row and issues no SQL. Several flushes may occur inside one
/// transaction, which is exactly why the row is composed later, once, by the frame that
/// owns the commit.
/// </remarks>
/// <param name="EntityType">The CLR type name of the entity that changed.</param>
/// <param name="EntityId">Its primary key rendered as text, or <c>null</c> for a keyless shape.</param>
/// <param name="BeforeJson">The prior state, or <c>null</c> for an insert.</param>
/// <param name="AfterJson">The new state, or <c>null</c> for a delete.</param>
/// <param name="Fields">The per-property diff. Never polymorphic — see <see cref="CapturedFieldChange"/>.</param>
public sealed record CapturedEntityChange(
    string EntityType,
    string? EntityId,
    string? BeforeJson,
    string? AfterJson,
    IReadOnlyList<CapturedFieldChange> Fields);

/// <summary>
/// One property's transition, addressed by JSON Pointer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every value slot in this file holds JSON text, not a rendered value</b>, which is
/// why each is named <c>…Json</c>. The distinction is not cosmetic: <c>42</c> and
/// <c>"42"</c> are different values, a C# <c>null</c> is the JSON <c>null</c> rather
/// than an absent key, and the redaction sentinel therefore enters quoted as
/// <c>"***REDACTED***"</c>. A slot that held a rendered value would make the
/// <c>changes</c> column unparseable by the two readers that consume it.
/// </para>
/// <para>
/// Serialises into the <c>changes</c> column as a JSON <b>array</b> of
/// <c>{ path, before, after }</c>, single-entity and multi-entity alike, with an
/// entity-qualified RFC 6901 pointer in <see cref="Path"/>. ADR-0016 made the column
/// polymorphic — an object for one entity, an array for several —
/// and <see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044
/// § 7</see> withdraws that: two readers parse this column, and a shape that changes
/// with the row's arity is a shape each of them gets wrong once.
/// </para>
/// <para>
/// The array survives the size cap too. Above
/// <c>JsonValue.MaxRowBytes</c> the value becomes an elision record, and the record is
/// wrapped in a one-element array so the column's JSON <em>type</em> is the same on
/// both sides of the cap.
/// </para>
/// </remarks>
/// <param name="Path">An entity-qualified RFC 6901 pointer.</param>
/// <param name="BeforeJson">The prior value as JSON, already redacted if the property is sensitive.</param>
/// <param name="AfterJson">The new value as JSON, already redacted if the property is sensitive.</param>
public sealed record CapturedFieldChange(string Path, string? BeforeJson, string? AfterJson);
