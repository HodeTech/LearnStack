using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Customization.Application.Customization;

/// <summary>
/// The refusals every customization handler shares, as RFC 7807 entries.
/// </summary>
/// <remarks>
/// The top-level code stays a cross-cutting one — <c>validation_failed</c>,
/// <c>business_rule_violation</c>, <c>tenant_mismatch</c> — because
/// <c>HttpStatusMap</c> is a closed table and a module-specific key there falls
/// through to <c>500</c>: the one answer a refusal must never give. What is
/// module-specific goes in the field entry, where a client reads it.
/// </remarks>
internal static class CustomizationFailures
{
    /// <summary>The context carried no tenant, so there is nothing to write into.</summary>
    /// <remarks>
    /// <c>TenantContextBehavior</c> has already refused an unresolved context for
    /// these commands — none carries <c>[AllowsUnresolvedTenantContext]</c> — so
    /// this is a guard against a future wiring change rather than a reachable
    /// state. It fails closed rather than writing a customization under the all-zero
    /// tenant, and it answers with the code that behavior answers with, so the
    /// status a client sees does not depend on which layer caught it.
    /// </remarks>
    internal static Result<T> Unresolved<T>() =>
        Result.FailFor<Result<T>>(new Error(new LocalizedMessage("lockey_tenant_mismatch")));

    /// <summary>No definition with that id belongs to the ambient tenant.</summary>
    /// <remarks>
    /// <para>
    /// A row belonging to another tenant is invisible here — the query filter and
    /// the policy both drop it — so "not this tenant's" and "does not exist" are
    /// one answer by construction, and there is no lookup that could tell a caller
    /// which. That is the isolation working, not information withheld.
    /// </para>
    /// <para>
    /// <c>not_found</c> and therefore <c>404</c>, not <c>business_rule_violation</c>
    /// and <c>409</c>: nothing conflicts, and a client told 409 for an id it cannot
    /// see is being asked to resolve a conflict it cannot observe.
    /// </para>
    /// </remarks>
    internal static Result<T> NotFound<T>(string field) =>
        Field<T>("lockey_not_found", field, "lockey_customization_not_found");

    /// <summary>The document did not pass ADR-0043's gates.</summary>
    /// <remarks>
    /// The gate's own error, re-emitted rather than rebuilt.
    /// <c>IJsonSchemaValidator.AdmitSchema</c> already answers
    /// <c>validation_failed</c> with details keyed by <b>JSON pointer</b> — which
    /// is the whole reason the gates are ordered the way ADR-0043 § 2 orders them,
    /// so the ones that can name a location run before the one that cannot.
    /// Restating it under a field name would throw away the location a tenant
    /// needs to find the mistake.
    /// </remarks>
    internal static Result<T> SchemaRefused<T>(Error gate) => Result.FailFor<Result<T>>(gate);

    /// <summary>The aggregate changed after this handler read it.</summary>
    /// <remarks>
    /// Two callers publishing successors for one key both load the incumbent and
    /// both deprecate it; one wins and the other's <c>UPDATE</c> matches nothing.
    /// <c>concurrency_conflict</c> is what tells the loser to re-read and retry —
    /// the answer <c>IOptimisticConcurrency</c>'s own remarks promise.
    /// </remarks>
    internal static Result<T> Stale<T>() =>
        Result.FailFor<Result<T>>(new Error(new LocalizedMessage("lockey_concurrency_conflict")));

    internal static Result<T> Field<T>(string code, string field, string reason) =>
        Result.FailFor<Result<T>>(new Error(
            new LocalizedMessage(code),
            new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
            {
                [field] = [new LocalizedMessage(reason)],
            }));

    /// <summary>Which uniqueness a customization write collided with.</summary>
    /// <remarks>
    /// The versioned key and the one-live-revision index fail differently and a
    /// tenant admin fixes them differently: the first means "that revision number
    /// is taken", the second means "something is already live under that key". A
    /// single message for both would send half the callers to change the wrong
    /// thing.
    /// </remarks>
    internal static (string Field, string Reason) Conflict(string? constraintName) =>
        constraintName switch
        {
            "ux_tenant_content_types_tenant_id_key_schema_version"
                or "ux_tenant_level_taxonomies_tenant_id_key_schema_version" =>
                ("SchemaVersion", "lockey_schema_version_taken"),
            "ux_tenant_content_types_tenant_id_key_active"
                or "ux_tenant_level_taxonomies_tenant_id_key_active" =>
                ("Key", "lockey_customization_key_already_live"),
            "pk_tenant_content_types" or "pk_tenant_level_taxonomies" =>
                ("$", "lockey_identifier_taken"),
            "pk_tenant_level_taxonomy_items" =>
                ("Items", "lockey_taxonomy_item_key_duplicated"),
            "ux_tenant_level_taxonomy_items_taxonomy_sort" =>
                ("Items", "lockey_taxonomy_item_sort_duplicated"),
            _ => ("$", "lockey_business_rule_violation"),
        };
}
