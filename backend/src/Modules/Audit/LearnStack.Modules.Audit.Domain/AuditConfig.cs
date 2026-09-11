using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Time;

namespace LearnStack.Modules.Audit.Domain;

/// <summary>
/// One tenant's override of one <c>(module, operation)</c>'s audit coverage.
/// </summary>
/// <remarks>
/// <para>
/// <b>It can narrow SHOULD/MAY and can never remove a MUST.</b> The classifier applies
/// the override and then re-applies the in-process catalogue's MUST floor, so a tenant
/// may audit more than the baseline and never less
/// (<see href="../../../../../docs/decisions/0033-audit-durability-model.md">ADR-0033</see>).
/// An attacker who compromises one tenant admin must not be able to switch off the
/// detector that would catch the next cross-tenant probe.
/// </para>
/// <para>
/// <b>Never read on the request path.</b> Overrides reach the classifier as a cached
/// projection whose loader opens its own short transaction and sets
/// <c>app.tenant_id</c> itself. At pipeline step 3 no transaction is open and the GUC is
/// unset, and this table carries <c>ENABLE</c> + <c>FORCE</c> row security — so a lookup
/// there would return <b>zero rows silently</b>, which reads exactly like "this tenant
/// has no overrides" and never trips a fail-closed <c>catch</c>. A failure to read one
/// falls back to the catalogue, which carries the same floor.
/// </para>
/// <para>
/// <b>Nothing writes it in this packet, and the corpus says so rather than leaving a
/// reader to discover it.</b> Packet 9 ships the table, its policy and the cached read;
/// the tenant-admin surface that authors an override lands with the Studio in
/// <see href="../../../../../docs/roadmap/phase-06-renderer-admin-studio.md">Phase 06</see>,
/// on the permission registry
/// <see href="../../../../../docs/roadmap/phase-03-identity-admin.md">Phase 03</see>
/// brings (<see href="../../../../../docs/decisions/0044-audit-write-path.md">ADR-0044
/// Amendment 4 § 2</see>). Nothing is lost by the gap: an absent override reads as "no
/// overrides", which is the safe answer.
/// </para>
/// <para>
/// <b>Tenant-owned, tenant-wide.</b> It has no <c>organization_id</c> — nothing asks a
/// tenant to classify one organization's operations differently from another's — so it
/// takes the tenant term only and no restrictive write guards. <c>audit_log</c>, which
/// does carry the column, is the org-scoped one of the pair (ADR-0044 Amendment 1).
/// </para>
/// </remarks>
[TenantOwned]
public sealed class AuditConfig
    : AuditableEntity<AuditConfigId>, ITenantOwned, IAggregateRoot<AuditConfigId>
{
    private AuditConfig(AuditConfigId id)
        : base(id)
    {
        ModuleName = null!;
        Operation = null!;
    }

    // EF materialization.
    private AuditConfig()
    {
        ModuleName = null!;
        Operation = null!;
    }

    public TenantId TenantId { get; private set; }

    /// <summary>The owning module's short name.</summary>
    public string ModuleName { get; private set; }

    /// <summary>The dotted slug this row overrides.</summary>
    public string Operation { get; private set; }

    /// <summary>
    /// Whether the tenant wants this operation audited.
    /// </summary>
    /// <remarks>
    /// Deliberately not the whole story: <c>false</c> narrows a SHOULD or a MAY and does
    /// nothing to a MUST, because the classifier re-applies the floor after reading it.
    /// </remarks>
    public bool IsEnabled { get; private set; }

    /// <summary>
    /// Declares one tenant's override.
    /// </summary>
    /// <remarks>
    /// Unreachable in Packet 9 — no command calls it — and present because the aggregate
    /// that models the row is what the cached projection materialises, and an aggregate
    /// with no way to come into being would have to be constructed around later. The
    /// caller that will use it arrives with the Studio surface in Phase 06.
    /// </remarks>
    public static AuditConfig Declare(
        AuditConfigId id,
        TenantId tenantId,
        string moduleName,
        string operation,
        bool isEnabled,
        IClock clock,
        UserId declaredBy)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        TenantOwnership.EnsureRealTenant(
            tenantId,
            "An audit override belongs to a real tenant.",
            nameof(tenantId));

        var config = new AuditConfig(id)
        {
            TenantId = tenantId,
            ModuleName = moduleName,
            Operation = operation,
            IsEnabled = isEnabled,
        };

        config.MarkCreated(clock.UtcNow, declaredBy);
        return config;
    }
}
