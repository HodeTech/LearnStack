namespace LearnStack.SharedKernel.Persistence;

/// <summary>
/// Marker for any entity whose updates use optimistic concurrency. EF Core
/// configures <see cref="Version"/> as the row version token so concurrent
/// updates fail with <c>DbUpdateConcurrencyException</c> — translated to a
/// <c>Result.Fail(LocalizedMessage.Of("lockey_concurrency_conflict"))</c>
/// per ADR-0032 § Sub-decision 6.
/// </summary>
public interface IOptimisticConcurrency
{
    /// <summary>
    /// Monotonically-increasing version counter, mapped to the
    /// <c>row_version bigint</c> column.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>EF Core does not bump this.</b> It is incremented in
    /// <c>AuditableEntity</c>, by the same primitive that stamps the audit
    /// columns, so an audited mutation is a versioned mutation
    /// (<see href="../../../../docs/decisions/0039-optimistic-concurrency-token.md">ADR-0039</see>).
    /// The property is configured with
    /// <c>HasDefaultValue(0L).IsConcurrencyToken().ValueGeneratedNever()</c> and
    /// nothing else (ADR-0039 Amendment 2). Adding
    /// <c>ValueGeneratedOnAddOrUpdate()</c> — or the equivalent
    /// <c>IsRowVersion()</c> — tells EF the database generates the value, and
    /// EF then omits the column from the <c>UPDATE</c> entirely. Measured: the
    /// persisted value stays <c>0</c> for the life of the row and every lost
    /// update succeeds.
    /// </para>
    /// </remarks>
    long Version { get; }
}
