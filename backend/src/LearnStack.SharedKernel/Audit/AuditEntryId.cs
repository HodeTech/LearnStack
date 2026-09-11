using LearnStack.SharedKernel.Identifiers;
using Vogen;

namespace LearnStack.SharedKernel.Audit;

/// <summary>
/// The identity of one <c>audit_log</c> row.
/// </summary>
/// <remarks>
/// <para>
/// Lives in <see cref="LearnStack.SharedKernel"/> rather than in the Audit module,
/// per <see href="../../../../docs/decisions/0023-strongly-typed-id-source-generator.md">ADR-0023
/// Amendment 9</see>. It is the fourth cross-cutting identifier, and here the
/// placement rule is not a matter of degree but of compilation: <c>AuditIntent</c>
/// and <c>AuditEntryDraft</c> are SharedKernel records that name this type, and
/// <c>LearnStack.Modules.Audit.Domain</c> already references SharedKernel — so a
/// module-local id would need the reference back.
/// </para>
/// <para>
/// <b>Minted app-side, and this is the one append-only table that is.</b> ADR-0023
/// routes UUIDv7 two ways — app-side through <c>IGuidFactory</c> for aggregates,
/// DB-side for high-volume append-only tables — and <c>audit_log</c> is squarely the
/// second shape. It takes the first anyway, because the id has to exist *before* the
/// row does: <c>AuditLogBehavior</c> mints it at pipeline step 3 to declare the
/// intent, and
/// <see href="../../../../docs/decisions/0033-audit-durability-model.md">ADR-0033</see>'s
/// commit-in-doubt case requires the standalone re-write to carry the *same* id as
/// the in-transaction attempt. Two inserts on two connections cannot share a
/// server-generated default.
/// </para>
/// <para>
/// No <c>New()</c> static, per
/// <see href="../../../../docs/standards/02-backend-coding.md">Backend Coding
/// Standards</see>: the call site is
/// <c>AuditEntryId.From(guidFactory.NewUuidV7())</c>, so a test can fix it.
/// </para>
/// </remarks>
[ValueObject<Guid>(LearnStackVogenDefaults.IdMask)]
public readonly partial record struct AuditEntryId : IStronglyTypedId<Guid>;
