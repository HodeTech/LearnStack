using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Platform;

namespace LearnStack.SharedKernel.Audit;

/// <summary>
/// The one executor of privileged audit redaction
/// (<see href="../../../../docs/decisions/0108-privileged-audit-redaction.md">ADR-0108 § 2</see>).
/// </summary>
/// <remarks>
/// <para>
/// <b>One caller, one site.</b> The platform scope's handle is the only caller, and it passes the operation's
/// admission, so the executor refuses unless that admission names the operation ADR-0108 sanctions for the plan's
/// pass: <c>audit.redaction.apply</c> for an erasure's, and the closure's Finalize (ADR-0107 § 7) for a closure's tenant
/// and sentinel passes. The implementation lives in
/// <c>LearnStack.Infrastructure.Audit</c>, is resolved by dependency injection so
/// <c>LearnStack.Infrastructure</c> gains no reference to it, and is the single file the append-only architecture
/// test names as a sanctioned redaction site. Modules never write <c>audit_log</c>; they only declare where their
/// person references live (<c>IUserReferenceLocator</c>).
/// </para>
/// <para>
/// <b>The caller's transaction.</b> The executor runs on the scope's own connection and work transaction, so the
/// redaction commits or rolls back with what that transaction carries: an erasure's status change and result row, or a
/// closure Finalize's intent row (ADR-0108 § 8). Its statements are fixed and parameterized, idempotent, and touch only
/// the six columns the platform role may update.
/// </para>
/// </remarks>
public interface IAuditRedactor
{
    /// <summary>Redacts the subject's audit rows for the plan and reports what changed and what survives.</summary>
    /// <exception cref="InvalidOperationException">The admission does not name a sanctioned operation.</exception>
    Task<AuditRedactionReport> RedactAsync(
        AuditRedactionPlan plan,
        PlatformAdmission admission,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default);
}

/// <summary>The closed kinds of redaction: an erasure's privileged step, and a closure's two kinds of pass.</summary>
/// <remarks>Zero is deliberately undefined, so an unassigned pass is never a valid one.</remarks>
public enum AuditRedactionPass
{
    /// <summary>
    /// An erasure request's privileged step (<c>complete_erasure</c>): one group's request, reaching the whole tenant or
    /// the group's organization by who asked (ADR-0108 § 4).
    /// </summary>
    Erasure = 1,

    /// <summary>
    /// A closure's pass over one participant tenant (the closure's Finalize, ADR-0107 § 7): the whole tenant, by the
    /// subject and every Membership the person holds there in any group.
    /// </summary>
    ClosureTenant = 2,

    /// <summary>
    /// A closure's pass over the platform sentinel tenant (the closure's Finalize): the subject's global creation and its
    /// failed and undetermined attempts, found by the subject key in their metadata (ADR-0108 § 4).
    /// </summary>
    ClosureSentinel = 3,
}

/// <summary>
/// What one redaction concerns: an erasure request's immutable subject, group, origin and frozen Membership
/// identifiers, or one pass of a closure's Finalize (ADR-0108 § 4).
/// </summary>
/// <remarks>
/// <para>
/// <b>One record, three closed shapes.</b> <see cref="Pass"/> says which; the erasure's is built from the request's
/// subject by the constructor, and a closure's two passes by <see cref="ForClosureTenant"/> and
/// <see cref="ForClosureSentinel"/>, the only other ways to build one. It stays one sealed record rather than a
/// hierarchy because the executor port and the scope handle take one plan type, which test doubles implement, and an
/// erasure plan keeps exactly the members it had: a closure's pass is not one request's, so only the erasure carries a
/// <see cref="Subject"/>, and every plan carries what the selection reads (<see cref="TenantId"/>,
/// <see cref="OrganizationId"/>, <see cref="WholeTenant"/>, <see cref="SubjectUserId"/>, <see cref="MembershipIds"/> and,
/// for the sentinel pass, <see cref="SubjectKey"/>).
/// </para>
/// <para>
/// <b>The reach.</b> An erasure the person filed, and the fan-out erasure of a closure, selects across the whole tenant;
/// an administrator's request selects only rows whose <c>organization_id</c> is the group's (<see cref="WholeTenant"/> is
/// <see langword="false"/>), so an organization administrator never alters another branch's records. A row is selected
/// when its <c>actor_user_id</c> is the subject, its <c>entity_id</c> is the subject or one of the Memberships, or a
/// declared reference holds one of those identifiers. A closure's tenant pass is that whole-tenant selection with every
/// Membership the person holds in the tenant, so one pass covers all of the person's groups there; its sentinel pass
/// reaches the platform sentinel tenant, where a row is selected by the subject key in its metadata, by the subject as
/// its actor, or by the subject's <c>User</c> as its entity.
/// </para>
/// </remarks>
public sealed record AuditRedactionPlan
{
    private readonly UserDataSubject? _subject;

    /// <summary>The plan of an erasure request's privileged step, from the request's own subject.</summary>
    /// <exception cref="ArgumentException">
    /// The subject is not an erasure's: an export is never redacted, and a closure's passes are built by
    /// <see cref="ForClosureTenant"/> and <see cref="ForClosureSentinel"/>.
    /// </exception>
    public AuditRedactionPlan(UserDataSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (subject.Kind != DataRequestKind.Erasure)
        {
            throw new ArgumentException(
                "Only an erasure request's subject is a redaction plan: an export is never redacted, and a closure's passes are built by their own factories.",
                nameof(subject));
        }

        _subject = subject;
        Pass = AuditRedactionPass.Erasure;
        TenantId = subject.Group.TenantId;
        OrganizationId = subject.Group.OrganizationId;
        WholeTenant = subject.Origin != DataRequestOrigin.Administrator;
        SubjectUserId = subject.SubjectUserId;
        MembershipIds = subject.MembershipIds;
    }

    private AuditRedactionPlan(AuditRedactionPass pass, TenantId tenantId, UserId subjectUserId,
        ImmutableArray<Guid> membershipIds, IdentitySubjectKey? subjectKey)
    {
        Pass = pass;
        TenantId = tenantId;
        OrganizationId = null;
        WholeTenant = true;
        SubjectUserId = subjectUserId;
        MembershipIds = membershipIds;
        SubjectKey = subjectKey;
    }

    /// <summary>
    /// A closure's pass over one participant tenant: the whole tenant, by the subject and every Membership the person
    /// holds there, of any status and in any group (ADR-0107 § 7, ADR-0108 § 4).
    /// </summary>
    /// <param name="tenantId">The participant tenant; never the platform sentinel, whose pass is <see cref="ForClosureSentinel"/>.</param>
    /// <param name="subjectUserId">The closing person.</param>
    /// <param name="membershipIds">
    /// Every Membership the person holds in the tenant, tombstones included: at least one, distinct, and at most
    /// <see cref="DataRequestLimits.MaximumSnapshotMemberships"/>, the bound an erasure's snapshot already carries, so a
    /// pass compares each row with no more identifiers than an erasure does.
    /// </param>
    /// <exception cref="ArgumentException">An identifier is unassigned or a system one, or the Memberships are empty, duplicated or too many.</exception>
    public static AuditRedactionPlan ForClosureTenant(TenantId tenantId, UserId subjectUserId, IEnumerable<Guid> membershipIds)
    {
        ArgumentNullException.ThrowIfNull(membershipIds);
        if (!StronglyTypedId.IsAssigned(tenantId) || tenantId == TenantId.PlatformSentinel)
        {
            throw new ArgumentException("A closure's tenant pass names a real participant tenant, never the platform sentinel.", nameof(tenantId));
        }

        RequirePerson(subjectUserId);
        var memberships = membershipIds.ToImmutableArray();
        if (memberships.IsEmpty || memberships.Length > DataRequestLimits.MaximumSnapshotMemberships
            || memberships.Contains(Guid.Empty) || memberships.Distinct().Count() != memberships.Length)
        {
            throw new ArgumentException("A closure's tenant pass names the person's bounded, distinct Memberships in the tenant.", nameof(membershipIds));
        }

        return new AuditRedactionPlan(AuditRedactionPass.ClosureTenant, tenantId, subjectUserId, memberships, subjectKey: null);
    }

    /// <summary>
    /// A closure's pass over the platform sentinel tenant: the subject's global creation and its failed and undetermined
    /// attempts, which carry the subject key in their metadata (ADR-0108 § 4).
    /// </summary>
    /// <param name="subjectUserId">The closing person.</param>
    /// <param name="subjectKey">The person's subject key, as the <c>users</c> row holds it.</param>
    /// <exception cref="ArgumentException">The subject is unassigned or a system one.</exception>
    public static AuditRedactionPlan ForClosureSentinel(UserId subjectUserId, IdentitySubjectKey subjectKey)
    {
        ArgumentNullException.ThrowIfNull(subjectKey);
        RequirePerson(subjectUserId);
        return new AuditRedactionPlan(AuditRedactionPass.ClosureSentinel, TenantId.PlatformSentinel, subjectUserId, [], subjectKey);
    }

    /// <summary>Which of the closed shapes this plan is, and so which operation may redact it.</summary>
    public AuditRedactionPass Pass { get; }

    /// <summary>The erasure request's frozen subject, group, origin and Membership identifiers.</summary>
    /// <exception cref="InvalidOperationException">The plan is a closure's pass, which is not one request's.</exception>
    public UserDataSubject Subject =>
        _subject ?? throw new InvalidOperationException("A closure's pass is not one request's and has no request subject.");

    /// <summary>The tenant the selection reaches: the request's, the participant's, or the platform sentinel.</summary>
    public TenantId TenantId { get; }

    /// <summary>The erasure group's organization; <see langword="null"/> for the tenant-wide group and every closure pass.</summary>
    public OrganizationId? OrganizationId { get; }

    /// <summary>
    /// Whether the selection spans the whole tenant (a person's own request, a closure's fan-out erasure and every
    /// closure pass) rather than the group's organization alone (an administrator's request).
    /// </summary>
    public bool WholeTenant { get; }

    /// <summary>The person whose rows are redacted.</summary>
    public UserId SubjectUserId { get; }

    /// <summary>The Membership identifiers the selection compares; empty for the sentinel pass, whose tenant has none.</summary>
    public ImmutableArray<Guid> MembershipIds { get; }

    /// <summary>The person's subject key; set for the sentinel pass alone, which selects by it.</summary>
    public IdentitySubjectKey? SubjectKey { get; }

    /// <summary>Never prints the subject, the tenant, the Memberships or the key.</summary>
    public override string ToString() => nameof(AuditRedactionPlan);

    private static void RequirePerson(UserId subjectUserId)
    {
        if (!StronglyTypedId.IsAssigned(subjectUserId) || subjectUserId == UserId.SystemActor)
        {
            throw new ArgumentException("A closure's pass redacts a real person's rows.", nameof(subjectUserId));
        }
    }
}

/// <summary>The six <c>audit_log</c> columns the platform role may update, and so the only ones a redaction touches.</summary>
public enum AuditRedactedColumn
{
    IpAddress = 1,
    UserAgent = 2,
    ActorEmail = 3,
    BeforeState = 4,
    AfterState = 5,
    Changes = 6,
}

/// <summary>The stored spelling of <see cref="AuditRedactedColumn"/>, which is the column name.</summary>
public static class AuditRedactionCodes
{
    public static ClosedCodeTable<AuditRedactedColumn> Columns { get; } = new(
        (AuditRedactedColumn.IpAddress, "ip_address"),
        (AuditRedactedColumn.UserAgent, "user_agent"),
        (AuditRedactedColumn.ActorEmail, "actor_email"),
        (AuditRedactedColumn.BeforeState, "before_state"),
        (AuditRedactedColumn.AfterState, "after_state"),
        (AuditRedactedColumn.Changes, "changes"));
}

/// <summary>
/// What a redaction did and what survives it, as closed counts and never a person identifier (ADR-0108 § 3, § 6).
/// </summary>
/// <remarks>
/// <see cref="Retained"/> is a partial list: a kind absent from it is not zero. Its kinds come from ADR-0107 § 6's
/// closed vocabulary (the audit actor, entity and metadata references and the rows the selection could not reach).
/// </remarks>
public sealed record AuditRedactionReport
{
    /// <exception cref="ArgumentOutOfRangeException">A count is negative or a column or kind is undefined.</exception>
    public AuditRedactionReport(
        long rowsSelected,
        IReadOnlyDictionary<AuditRedactedColumn, long> redacted,
        IReadOnlyCollection<RetainedReferenceCount> retained)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowsSelected);
        ArgumentNullException.ThrowIfNull(redacted);
        ArgumentNullException.ThrowIfNull(retained);
        if (redacted.Any(entry => !Enum.IsDefined(entry.Key) || entry.Value < 0)
            || retained.Select(entry => entry.Kind).Distinct().Count() != retained.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(redacted), "A redaction report holds defined columns, distinct kinds and no negative count.");
        }

        RowsSelected = rowsSelected;
        Redacted = Enum.GetValues<AuditRedactedColumn>().ToFrozenDictionary(
            column => column, column => redacted.TryGetValue(column, out var count) ? count : 0L);
        Retained = [.. retained.OrderBy(entry => entry.Kind)];
    }

    /// <summary>The rows the plan's selection matched.</summary>
    public long RowsSelected { get; }

    /// <summary>The rows changed per column; every column is present, zero when none changed.</summary>
    public FrozenDictionary<AuditRedactedColumn, long> Redacted { get; }

    /// <summary>The retained kinds and counts, ordered by kind.</summary>
    public ImmutableArray<RetainedReferenceCount> Retained { get; }

    /// <summary>
    /// The closed JSON a result row's <c>after_state</c> holds:
    /// <c>{"rows_selected":n,"redacted":{column:n,...},"retained":{kind:n,...}}</c>, small and bounded by the two
    /// closed vocabularies.
    /// </summary>
    public string ToClosedJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Writes the closed object <see cref="ToClosedJson"/> returns as the writer's next value.</summary>
    internal void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("rows_selected", RowsSelected);
        writer.WriteStartObject("redacted");
        foreach (var column in Enum.GetValues<AuditRedactedColumn>())
        {
            writer.WriteNumber(AuditRedactionCodes.Columns.ToCode(column), Redacted[column]);
        }

        writer.WriteEndObject();
        writer.WriteStartObject("retained");
        foreach (var entry in Retained)
        {
            writer.WriteNumber(DataRequestCodes.RetainedKinds.ToCode(entry.Kind), entry.Count);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
