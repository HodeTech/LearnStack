using System.Net;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Modules.Audit.Domain;

/// <summary>
/// One row of <c>audit_log</c>: who did what, to which resource, when, from where, with
/// what outcome.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only by construction, and that is the aggregate's whole shape.</b> There is no
/// factory and no mutator. Rows are written by <c>PostgresAuditStore</c> as one
/// parameterised <c>INSERT</c> from an <see cref="AuditEntryDraft"/>, never through this
/// type — mapping it into every module's <c>DbContext</c> would need SharedKernel to
/// reference this assembly, which already references SharedKernel
/// (<see href="../../../../../docs/decisions/0033-audit-durability-model.md">ADR-0033
/// § Implementation Notes</see>). What this type is for is the **read** model the audit
/// admin API projects from, which lands with that API in
/// <see href="../../../../../docs/roadmap/phase-03-identity-admin.md">Phase 03</see>.
/// The only constructor is EF Core's.
/// </para>
/// <para>
/// <b>It inherits <see cref="Entity{TId}"/>, never <c>AuditableEntity</c>.</b> An audit
/// row carrying <c>UpdatedAt</c> / <c>DeletedAt</c> is a mutable audit row, which is a
/// contradiction; <c>AuditEntry_Inherits_Entity_Not_AuditableEntity</c> is the guard. It
/// therefore carries no <c>row_version</c> and no soft-delete pair either — the two
/// sanctioned mutations reach the table as SQL under
/// <c>audit_log_append_only_guard</c>, not through change tracking.
/// </para>
/// <para>
/// <b>Tenant-owned and organization-scoped.</b> The typed identifiers are what put it
/// inside the isolation machinery: <see cref="ITenantOwned"/>'s member is a
/// <see cref="TenantId"/>, and an entity holding a raw <c>Guid</c> could not implement it,
/// would get no query filter, and would drop out of every isolation sweep
/// (<see href="../../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 § 9</see>).
/// </para>
/// </remarks>
[TenantOwned]
[OrganizationScoped]
public sealed class AuditEntry
    : Entity<AuditEntryId>, IOrganizationScoped, IAggregateRoot<AuditEntryId>
{
    // EF materialization, and the only way an instance comes to exist. The write path
    // is PostgresAuditStore's INSERT; nothing in the solution constructs this type.
    private AuditEntry()
    {
        ModuleName = null!;
        Operation = null!;
    }

    /// <summary>
    /// The tenant the ambient transaction announced — never a request payload.
    /// </summary>
    /// <remarks>
    /// A platform-scope row carries <see cref="Identifiers.TenantId.PlatformSentinel"/>,
    /// which no tenant policy admits and only <c>learnstack_platform</c> can read.
    /// </remarks>
    public TenantId TenantId { get; private set; }

    /// <summary>The organization the act belonged to, or <c>null</c> for a tenant-wide one.</summary>
    public OrganizationId? OrganizationId { get; private set; }

    /// <summary>Who acted, when there was a principal.</summary>
    /// <remarks>
    /// <b>Never redacted</b>, unlike <see cref="ActorEmail"/>. Once the <c>users</c> row is
    /// erased this is an orphan surrogate with no path back to a natural person, which is
    /// what keeps the row's existence auditable after erasure and the probe-detection
    /// queries answerable. Redacting it would collapse every erased user's history into
    /// one indistinguishable bucket.
    /// </remarks>
    public UserId? ActorUserId { get; private set; }

    /// <summary>The actor's email at the time. Redactable under the GDPR path.</summary>
    public string? ActorEmail { get; private set; }

    /// <summary>The owning module's short name.</summary>
    public string ModuleName { get; private set; }

    /// <summary>The dotted slug <c>{module}.{resource}.{verb}</c>.</summary>
    public string Operation { get; private set; }

    /// <summary>What kind of act this was.</summary>
    public OperationType OperationType { get; private set; }

    /// <summary>The tier the module's catalogue declared.</summary>
    public OperationClass OperationClass { get; private set; }

    /// <summary>The aggregate the act was about, when there was one.</summary>
    public string? EntityType { get; private set; }

    /// <summary>Its id, rendered as text.</summary>
    public string? EntityId { get; private set; }

    /// <summary>Success, denied, failed, or indeterminate.</summary>
    public AuditOutcome Outcome { get; private set; }

    /// <summary>The refusal's localization key, when it was refused.</summary>
    public string? ErrorKey { get; private set; }

    /// <summary>
    /// Why a cross-tenant access happened, or a denial's cause. Operator-authored,
    /// never caller-supplied.
    /// </summary>
    public string? Reason { get; private set; }

    /// <summary>The prior snapshot, redacted and size-capped. JSON.</summary>
    public string? BeforeState { get; private set; }

    /// <summary>The new snapshot, redacted and size-capped. JSON.</summary>
    public string? AfterState { get; private set; }

    /// <summary>The per-field diff. Always a JSON array, on both sides of the size cap.</summary>
    public string? Changes { get; private set; }

    /// <summary>Matches the trace id in logs and the Problem Details body.</summary>
    public string? CorrelationId { get; private set; }

    /// <summary>Redactable under the GDPR path. <c>inet</c>, not text.</summary>
    public IPAddress? IpAddress { get; private set; }

    /// <summary>Redactable under the GDPR path.</summary>
    public string? UserAgent { get; private set; }

    /// <summary>
    /// When the operation was declared, from <c>IClock</c> — supplied by the store, not
    /// by the column's default.
    /// </summary>
    /// <remarks>
    /// Half of the primary key. Two rows share an <see cref="AuditEntryId"/> exactly when
    /// a <c>COMMIT</c> was in doubt, and they differ here because the standalone re-write
    /// takes a fresh reading.
    /// </remarks>
    public DateTimeOffset Timestamp { get; private set; }

    /// <summary>Structured context the operation supplied. JSON.</summary>
    public string? Metadata { get; private set; }
}
