using LearnStack.SharedKernel.Results;

namespace LearnStack.SharedKernel.Validation;

/// <summary>
/// The write path's gate for a tenant-authored JSON Schema and for the entries
/// declared against one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The only door.</b>
/// <see href="../../../../docs/architecture/32-tenant-customization-model.md">Tenant
/// Customization Model § 8.1</see> is explicit that "nothing is schema-validated
/// on the read path", so anything that writes a customization or content-entry row
/// without passing through this port breaks the trust every subsequent read
/// depends on. A migration, a repair script or a bulk importer that skips it is
/// not a shortcut; it is the defect.
/// </para>
/// <para>
/// <b>Why a shared-kernel port rather than a Customization one.</b> This is not
/// the provider-adapter rule —
/// <see href="../../../../docs/standards/01-architecture-standards.md">Architecture
/// Standards § Provider Adapters</see> governs dependencies that cross the
/// LearnStack boundary, and an in-process evaluator crosses nothing. It is here
/// because four modules need it: Customization in
/// <see href="../../../../docs/roadmap/phase-02a-kernel-tenancy.md">Packet 8</see>,
/// Identity for <c>tenant_custom_field_defs</c> in Phase 03, Content for the
/// validating bulk importer in Phase 04, and Education in Phase 05. A port in
/// <c>Customization.Application.Contracts</c> would make three unrelated modules
/// depend on Customization to reach a library wrapper.
/// </para>
/// <para>
/// <b>Nothing here names a library type, and no tenant input throws.</b> Both
/// members return <see cref="Result{T}"/> because a tenant's authoring mistake is
/// a 400 with a JSON pointer, not an exception — and an <c>ArgumentException</c>
/// escaping a handler has no entry in the status map and becomes a 500, which
/// this repository has already paid for once. There is exactly one exception, and
/// it is not about tenant input: <see cref="ValidateInstance"/> throws
/// <see cref="InvalidOperationException"/> when the schema it was handed does not
/// build, because that means a row was written past this gate and a 500 is the
/// honest answer. The compiled schema does not
/// outlive a call: compiling is measured cheaper than evaluating, so
/// <see href="../../../../docs/decisions/0043-customization-payload-validation.md">ADR-0043
/// § 6</see> deletes the cache § 8.2 used to mandate.
/// </para>
/// </remarks>
public interface IJsonSchemaValidator
{
    /// <summary>
    /// Decides whether <paramref name="jsonSchema"/> may be stored as a tenant's
    /// declared shape.
    /// </summary>
    /// <remarks>
    /// Runs the four gates
    /// <see href="../../../../docs/decisions/0043-customization-payload-validation.md">ADR-0043
    /// § 2</see> fixes: the document is JSON, it is inside the LearnStack schema
    /// profile, it satisfies the draft 2020-12 meta-schema, and it builds. The
    /// profile is the gate the library does not provide — see § 3 for what each of
    /// its clauses prevents, all of it measured.
    /// </remarks>
    /// <returns>
    /// <c>Ok</c>, or <c>Fail</c> carrying <c>validation_failed</c> whose details
    /// are keyed by JSON pointer.
    /// </returns>
    Result<None> AdmitSchema(string jsonSchema);

    /// <summary>
    /// Decides whether <paramref name="instanceJson"/> conforms to
    /// <paramref name="admittedSchema"/>.
    /// </summary>
    /// <param name="admittedSchema">
    /// A schema this validator has already admitted. Passing one it has not is a
    /// programmer error rather than a tenant's: the caller read it from a column
    /// only <see cref="AdmitSchema"/> writes.
    /// </param>
    /// <param name="instanceJson">The entry payload.</param>
    Result<None> ValidateInstance(string admittedSchema, string instanceJson);
}
