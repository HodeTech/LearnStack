using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Platform;

namespace LearnStack.SharedKernel.Audit;

/// <summary>
/// The one executor of privileged audit redaction
/// (<see href="../../../../docs/decisions/0108-privileged-audit-redaction.md">ADR-0108 § 2</see>).
/// </summary>
/// <remarks>
/// <para>
/// <b>One caller, one site.</b> The platform scope's handle is the only caller, and it passes the operation's
/// admission, so the executor refuses unless that admission names an operation ADR-0108 sanctions
/// (<c>audit.redaction.apply</c> now; a closure's Finalize in ADR-0107 § 7). The implementation lives in
/// <c>LearnStack.Infrastructure.Audit</c>, is resolved by dependency injection so
/// <c>LearnStack.Infrastructure</c> gains no reference to it, and is the single file the append-only architecture
/// test names as a sanctioned redaction site. Modules never write <c>audit_log</c>; they only declare where their
/// person references live (<c>IUserReferenceLocator</c>).
/// </para>
/// <para>
/// <b>The caller's transaction.</b> The executor runs on the scope's own connection and work transaction, so the
/// redaction commits or rolls back with the request's status change and the result row (ADR-0108 § 8). Its
/// statements are fixed and parameterized, idempotent, and touch only the six columns the platform role may update.
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

/// <summary>
/// What one redaction concerns: the request's immutable subject, group, origin and frozen Membership identifiers
/// (ADR-0108 § 4).
/// </summary>
/// <remarks>
/// The selection depends on who asked. A request the person filed, and the fan-out erasure of a closure, selects
/// across the whole tenant; an administrator's request selects only rows whose <c>organization_id</c> is the group's
/// (<see cref="WholeTenant"/> is <see langword="false"/>), so an organization administrator never alters another
/// branch's records. A row is selected when its <c>actor_user_id</c> is the subject, its <c>entity_id</c> is the subject
/// or one of the Memberships, or a declared reference holds one of those identifiers.
/// </remarks>
public sealed record AuditRedactionPlan
{
    /// <exception cref="ArgumentException">The subject is an export's, so no redaction concerns it.</exception>
    public AuditRedactionPlan(UserDataSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (subject.Kind == DataRequestKind.Export)
        {
            throw new ArgumentException("An export is never redacted.", nameof(subject));
        }

        Subject = subject;
    }

    /// <summary>The request's frozen subject, group, origin and Membership identifiers.</summary>
    public UserDataSubject Subject { get; }

    public TenantId TenantId => Subject.Group.TenantId;

    /// <summary>The group's organization; <see langword="null"/> is the tenant-wide group.</summary>
    public OrganizationId? OrganizationId => Subject.Group.OrganizationId;

    /// <summary>
    /// Whether the selection spans the whole tenant (a person's own request, or a closure's fan-out) rather than the
    /// group's organization alone (an administrator's request).
    /// </summary>
    public bool WholeTenant => Subject.Origin != DataRequestOrigin.Administrator;

    /// <summary>Never prints the subject, the group or the snapshot.</summary>
    public override string ToString() => nameof(AuditRedactionPlan);
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

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
