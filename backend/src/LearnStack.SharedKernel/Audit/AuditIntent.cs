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
/// <para>
/// <b>Everything a writer needs is on the intent, and nothing arrives beside it.</b> The
/// actor and the correlation id were once optional arguments to the composer; the
/// reconcile passed them and the in-transaction write did not, so every
/// <em>successful</em> MUST row — the commonest row in the table — carried neither
/// (<see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 Amendment
/// 6 § 2</see>). A value a writer can forget to pass is a value one writer will.
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
/// <param name="ActorUserId">
/// The principal the request carried, read from <c>ITenantContext.UserId</c> at step 3 —
/// or <c>null</c> when it carried none. Never a substituted identity: a handler writes
/// <c>created_by</c> as <c>UserId ?? UserId.SystemActor</c> because that column is
/// <c>NOT NULL</c>, which is an attribution for the aggregate and not a claim about who
/// was authenticated. An execution that acts <em>as</em> the system already carries
/// <see cref="UserId.SystemActor"/> in its context — <c>EventTenantContext</c> does — and
/// therefore writes it.
/// </param>
/// <param name="CorrelationId">
/// <c>ITenantContext.CorrelationId</c> at step 3 — the trace id in the logs and in a
/// failure's Problem Details body.
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
/// about none. The composer reads the captures of that type made by the request that
/// declared the intent: <c>entity_id</c> is the <b>subject</b> instance —
/// <see cref="SubjectId"/> when the handler designated one, otherwise the one instance
/// captured — <c>before_state</c> that instance's <b>earliest</b> capture and
/// <c>after_state</c> its <b>latest</b>, and <c>changes</c> every capture of the type in
/// capture order
/// (<see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 Amendment
/// 5 § 2, Amendment 6 § 1</see>).
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
    UserId? ActorUserId,
    string? CorrelationId,
    string ModuleName,
    string Operation,
    OperationType OperationType,
    OperationClass OperationClass,
    Type? EntityType,
    DateTimeOffset DeclaredAt)
{
    /// <summary>
    /// The instance the row is about, when the handler designated one through
    /// <see cref="IAuditSubject"/>; <c>null</c> otherwise.
    /// </summary>
    /// <remarks>
    /// Needed only when the request writes more than one instance of
    /// <see cref="EntityType"/> — publishing a revision retires the incumbent and activates
    /// the successor — because the composer cannot tell which of them the operation is
    /// about and refuses rather than guess. Rendered the way the interceptor renders the
    /// captured key, so the two compare as text.
    /// </remarks>
    public string? SubjectId { get; init; }

    /// <summary>
    /// What the request that declared this intent returned, recorded when its frame
    /// closed; <c>null</c> while that frame is still open.
    /// </summary>
    /// <remarks>
    /// Kept apart from the transaction's state because they are different facts. The
    /// state says what the database did with every row in the scope; this says what one
    /// request did — and under ADR-0040's sanctioned nesting a request can be refused
    /// while the transaction it joined commits
    /// (<see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044
    /// Amendment 6 § 3</see>).
    /// </remarks>
    public AuditIntentResult? Result { get; init; }
}
