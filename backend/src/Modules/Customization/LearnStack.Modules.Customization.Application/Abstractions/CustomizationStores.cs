using LearnStack.Modules.Customization.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Modules.Customization.Application.Abstractions;

/// <summary>The <c>TenantContentType</c> aggregate's writes, and the two reads a
/// publish needs.</summary>
/// <remarks>
/// <para>
/// Declared here and implemented across the boundary, for the reason
/// <see cref="IAggregateWriteStore{TRoot,TId}"/> gives: <c>Application →
/// Infrastructure</c> is a forbidden edge and the reverse reference already
/// exists.
/// </para>
/// <para>
/// <b>The reads live on the write port rather than on a second one.</b>
/// <see cref="IAggregateWriteStore{TRoot,TId}"/>'s own remarks say reads arrive
/// with the first handler that has something to read, and this is it — but they
/// arrive <i>here</i>, on the module's own port, because the reads are by natural
/// key rather than by id, which is a Customization fact and not a shared one. A
/// handler holding one port for reading and one for writing is still writing one
/// aggregate, which the cross-aggregate census says in as many words; fusing them
/// is what keeps that obvious rather than something a reader has to re-derive.
/// </para>
/// <para>
/// It is <b>not</b> the census that forces the shape, and it is worth being exact
/// about why: <c>Every_Write_Port_Is_Countable_Or_Enumerated</c> inspects a
/// method's <i>parameters</i>, so a read port <i>returning</i> a domain type is
/// invisible to it however it is declared. The reason to put the reads here is the
/// aggregate boundary, not the rule.
/// </para>
/// </remarks>
public interface ITenantContentTypeStore
    : IAggregateWriteStore<TenantContentType, TenantContentTypeId>
{
    /// <summary>The revision with this id, tracked, or nothing.</summary>
    Task<TenantContentType?> FindAsync(
        TenantContentTypeId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The live revision of <paramref name="key"/>, tracked, or nothing.
    /// </summary>
    /// <remarks>
    /// The one a publish has to retire. Soft-deleted rows are excluded, matching
    /// the partial index that holds the same rule in the database — a retired
    /// definition is not an incumbent.
    /// </remarks>
    Task<TenantContentType?> FindActiveAsync(
        string key, CancellationToken cancellationToken = default);
}

/// <summary>The <c>TenantLevelTaxonomy</c> aggregate's writes, and the two reads a
/// publish needs.</summary>
/// <remarks>
/// A second port rather than one fused with the first: the rule that confines
/// cross-aggregate writes counts the roots a handler's ports reach, and a fused
/// port is one constructor parameter reaching two — measured, and the escape that
/// rule closed.
/// </remarks>
public interface ITenantLevelTaxonomyStore
    : IAggregateWriteStore<TenantLevelTaxonomy, TenantLevelTaxonomyId>
{
    /// <summary>The revision with this id and its bands, tracked, or nothing.</summary>
    Task<TenantLevelTaxonomy?> FindAsync(
        TenantLevelTaxonomyId id, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="ITenantContentTypeStore.FindActiveAsync"/>
    Task<TenantLevelTaxonomy?> FindActiveAsync(
        string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// Whether the ambient tenant has declared a level taxonomy under a key.
/// </summary>
/// <remarks>
/// <para>
/// <b>A second port rather than a member on <see cref="ITenantLevelTaxonomyStore"/>,
/// and the reason is the census.</b> An <c>x-taxonomy</c> inside a content type's
/// schema has to resolve before the content type is written
/// (<see href="../../../../../../docs/architecture/32-tenant-customization-model.md">§
/// 8.1</see>), so the content type's handler asks this question — and a handler
/// holding a second <c>IAggregateWriteStore</c> is a handler
/// <c>Cross_Aggregate_Writes_Are_Confined_To_Tenant_Provisioning</c> counts as
/// writing two roots, which is exactly the thing
/// <see href="../../../../../../docs/decisions/0042-tenant-provisioning-cross-aggregate-transaction.md">ADR-0042</see>
/// keeps at one entry. Asking a yes/no question about another aggregate is not
/// writing it, and the port's shape is what makes that difference visible.
/// </para>
/// <para>
/// <b>By key, across revisions and statuses.</b> An <c>x-taxonomy</c> names the
/// concept — <c>cefr</c> — and the runtime resolves the live revision when it
/// renders. Requiring an <c>Active</c> revision here would mean a tenant could not
/// author a content type and its taxonomy in one sitting without publishing the
/// taxonomy first, which is an ordering the model does not otherwise impose.
/// Soft-deleted rows are excluded, for the reason the two <c>Find</c> methods
/// exclude them: a thrown-away taxonomy is not a vocabulary to reference.
/// </para>
/// <para>
/// The tenant is not a parameter. The query filter and the row-security policy
/// scope it, so another tenant's <c>cefr</c> is invisible here — which is the
/// answer this check needs and the one a parameter could get wrong.
/// </para>
/// </remarks>
public interface ITenantLevelTaxonomyCatalog
{
    /// <summary>Whether any non-deleted revision of <paramref name="key"/> exists.</summary>
    Task<bool> ContainsAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// Advances the ambient tenant's customization generation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not an aggregate port, deliberately.</b>
/// <see href="../../../../../../docs/decisions/0043-customization-payload-validation.md">ADR-0043
/// § 7</see> places the counter in the same class as <c>outbox_messages</c> and
/// <c>idempotency_keys</c>: a durable non-aggregate row that
/// <see href="../../../../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040</see>
/// already puts inside the business transaction. It does not derive from
/// <see cref="IAggregateWriteStore{TRoot,TId}"/>, so a handler that bumps it still
/// counts one aggregate; and its member takes a <c>SharedKernel</c> identifier and
/// no <c>Customization.Domain</c> type, so the census that enumerates non-deriving
/// write ports has nothing to say about it either. Both of those are conditions
/// the ADR states, not accidents of this signature.
/// </para>
/// <para>
/// <b>One statement, not a read-modify-write.</b> The row does not exist for a
/// tenant that has never had one, and two concurrent customization writes that
/// each read-then-write lose one bump — so every stale cache key one of them was
/// meant to strand stays reachable.
/// </para>
/// </remarks>
public interface ICustomizationGenerationStore
{
    /// <summary>The generation after the bump.</summary>
    Task<long> BumpAsync(TenantId tenantId, CancellationToken cancellationToken = default);
}
