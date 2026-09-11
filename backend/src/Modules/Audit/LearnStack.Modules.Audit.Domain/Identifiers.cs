using LearnStack.SharedKernel;
using LearnStack.SharedKernel.Identifiers;
using Vogen;

namespace LearnStack.Modules.Audit.Domain;

/// <summary>
/// Identifies one tenant's override of one <c>(module, operation)</c> classification.
/// </summary>
/// <remarks>
/// Module-local, unlike <c>AuditEntryId</c> and for the reason that one is not: nothing
/// outside this module holds an <see cref="AuditConfigId"/>, and no SharedKernel type
/// names it, so
/// <see href="../../../../../docs/decisions/0023-strongly-typed-id-source-generator.md">ADR-0023
/// Amendment 2</see>'s cross-cutting placement rule does not apply. `AuditEntryId` is in
/// SharedKernel because `AuditIntent` and `AuditEntryDraft` name it and the reverse
/// reference would be a project cycle; nothing of that kind is true here.
/// </remarks>
[ValueObject<Guid>(LearnStackVogenDefaults.IdMask)]
public readonly partial record struct AuditConfigId : IStronglyTypedId<Guid>;
