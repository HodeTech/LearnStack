using LearnStack.SharedKernel;
using LearnStack.SharedKernel.Identifiers;
using Vogen;

namespace LearnStack.Modules.Tenancy.Domain;

/// <summary>
/// Identifies a host this tenant claims.
/// </summary>
/// <remarks>
/// Module-local, unlike <see cref="TenantId"/> and <see cref="OrganizationId"/>:
/// nothing outside Tenancy holds one, so ADR-0023 Amendment 2's cross-cutting
/// placement rule does not apply and it stays with the aggregate it belongs to.
/// </remarks>
[ValueObject<Guid>(LearnStackVogenDefaults.IdMask)]
public readonly partial record struct TenantDomainId : IStronglyTypedId<Guid>;

/// <summary>
/// Identifies one tenant (or organization) configuration entry.
/// </summary>
/// <remarks>
/// The row is addressed in queries by <c>(tenant_id, organization_id, key)</c>;
/// this surrogate exists so the row is an <c>AuditableEntity</c> like any other —
/// a settings change is an audited mutation with a version, not an anonymous
/// upsert.
/// </remarks>
[ValueObject<Guid>(LearnStackVogenDefaults.IdMask)]
public readonly partial record struct TenantSettingId : IStronglyTypedId<Guid>;
