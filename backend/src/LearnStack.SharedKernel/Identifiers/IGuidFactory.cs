namespace LearnStack.SharedKernel.Identifiers;

/// <summary>
/// GUID minting abstraction. Application-side GUIDs flow through this
/// interface so tests can pin the sequence deterministically.
/// </summary>
/// <remarks>
/// Two minting paths exist per ADR-0031 (PostgreSQL 18) + ADR-0023:
/// app-side <see cref="NewUuidV7"/> for aggregates that need the ID before
/// <c>SaveChangesAsync</c>, and DB-side <c>uuidv7()</c> DEFAULT for
/// high-volume append-only tables (<c>audit_log</c>, <c>outbox_messages</c>).
/// This factory covers the app-side path only.
/// </remarks>
public interface IGuidFactory
{
    /// <summary>
    /// Mints a UUIDv7 (.NET 9+ <c>Guid.CreateVersion7</c>). Preferred for
    /// every new aggregate root identifier because the timestamp prefix
    /// keeps DB-side indexes sorted by insertion order.
    /// </summary>
    Guid NewUuidV7();

    /// <summary>
    /// Mints a UUIDv4. Reserved for non-aggregate identifiers that should
    /// not leak insertion-order information (e.g. external-facing tokens).
    /// </summary>
    Guid NewUuidV4();
}
