using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Audit;

/// <summary>
/// One audited <c>(resource, operation)</c>, declared at pipeline step 3 and written
/// immediately before <c>COMMIT</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Plural, and that is the correction.</b> ADR-0033 read as one intent per request;
/// <see href="../../../../docs/decisions/0033-audit-durability-model.md">its Amendment
/// 2 § 1</see> makes it one per audited resource, because <c>ProvisionTenantCommand</c>
/// writes two aggregate roots on one transaction and
/// <see href="../../../../docs/modules/tenancy/audit.md">the Tenancy matrix</see>
/// classifies both MUST. Under the singular reading the row it promises for
/// <c>Organization</c> would simply never exist.
/// </para>
/// <para>
/// <b>It carries the tenant, and the store never resolves one.</b>
/// <see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 Amendment 3
/// § 2</see>: step 3 is the only place all four of § 2's cases are decidable, and it is
/// where the id is already minted. Reading the tenant in the store instead would throw
/// on the one case the rule exists for — a provisioning command runs with an
/// <em>unresolved</em> context by construction, and the transaction has announced the
/// tenant it is creating.
/// </para>
/// </remarks>
/// <param name="Id">
/// Minted here so the in-transaction row and any standalone replacement carry one
/// identity — which is what makes the commit-in-doubt pair legible instead of two
/// unrelated rows.
/// </param>
/// <param name="TenantId">
/// The tenant the ambient transaction <b>announced</b>, never a request payload: the
/// resolved context's, or <c>IProvisionsTenant.ProvisioningTenantId</c>, or
/// <see cref="Identifiers.TenantId.PlatformSentinel"/> for a platform-scope row.
/// </param>
/// <param name="OrganizationId">
/// The organization the row belongs to, or <c>null</c> for a tenant-wide operation.
/// <c>audit_log</c> is org-scoped, so this is a policy predicate and not decoration.
/// </param>
/// <param name="ModuleName">The owning module's short name — <c>tenancy</c>, <c>customization</c>.</param>
/// <param name="Operation">
/// The dotted slug <c>{module}.{resource}.{verb}</c>. It borrows a permission key's
/// shape and shares its first two segments, but not its closed action set: a
/// permission bounds what a principal may do, an audit verb records what happened, and
/// a closed set there would make the record lie (ADR-0044 § 6).
/// </param>
/// <param name="OperationType">What kind of act this was.</param>
/// <param name="OperationClass">The tier the module's catalogue declared.</param>
/// <param name="EntityType">
/// The aggregate this row is about, from the catalogue entry, or <c>null</c> when it is
/// about none. The store selects every <see cref="CapturedEntityChange"/> whose
/// <c>EntityType</c> matches its name: <c>entity_id</c> is the captured id,
/// <c>before_state</c> the <b>earliest</b> such capture's and <c>after_state</c> the
/// <b>latest</b>, with <c>changes</c> their concatenation in capture order. The merge is
/// not hypothetical — <c>ProvisionTenantCommand</c> saves three times and captures
/// <c>Tenant</c> twice, and picking one arbitrarily records half of what happened
/// (<see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 Amendment
/// 5 § 2</see>).
/// </param>
/// <param name="DeclaredAt">
/// Stamped from <c>IClock</c> at step 3 and written to <c>timestamp</c> for the
/// in-transaction row. A standalone re-write takes a <b>fresh</b> reading, which is
/// what keeps the duplicate-id pair legal under the composite primary key.
/// </param>
public sealed record AuditIntent(
    AuditEntryId Id,
    TenantId TenantId,
    OrganizationId? OrganizationId,
    string ModuleName,
    string Operation,
    OperationType OperationType,
    OperationClass OperationClass,
    Type? EntityType,
    DateTimeOffset DeclaredAt);
