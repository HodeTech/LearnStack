using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Secrets;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Audit;

/// <summary>
/// The one executor of privileged audit redaction, and the one file in the solution that updates <c>audit_log</c>
/// (<see href="../../../docs/decisions/0108-privileged-audit-redaction.md">ADR-0108 §§ 2, 4–6</see>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Bound to its act.</b> It refuses, before any statement, an admission that does not name a sanctioned operation
/// for the plan's kind (<see cref="Sanctions"/>), an admission that has not opened a platform scope, a job frame, and a
/// connection that is not an open Npgsql one with its transaction; its first statement then refuses any role but the
/// platform's. It runs on the caller's connection and work transaction and never begins, commits or rolls back one, so
/// the redaction commits with the request's status change and the result row (ADR-0108 § 8).
/// </para>
/// <para>
/// <b>Selection (ADR-0108 § 4).</b> A request the person filed, and a closure's fan-out erasure, reach the whole
/// tenant; an administrator's request reaches only rows whose <c>organization_id</c> is the group's (none for the
/// tenant-wide group). A row is selected when its actor is the subject, its <c>entity_id</c> is the subject or a frozen
/// Membership, or a declared path of its entity type holds one of them. The actor and entity branches are
/// index-served (<c>ix_audit_log_actor_user_id_timestamp</c>; <c>ix_audit_log_tenant_id_entity_type_entity_id_timestamp</c>
/// by a skip scan over the entity types). A payload reference has no index, so the tenant's rows are read in batches,
/// newest first along <c>ix_audit_log_tenant_id_timestamp</c>, each batch one statement: a cheap filter walks each
/// payload column once for either canonical spelling of the identifiers, and only a row it keeps is rewritten to decide
/// whether a declared path holds one. That test is the redaction itself, so a row is selected for its payload exactly
/// when the redaction changes its payload.
/// </para>
/// <para>
/// <b>Batches.</b> A batch is a half-open range of the key <c>("timestamp", id)</c>, descending: the next batch's upper
/// end is this batch's lower end, excluded, so the batches partition the tenant's rows committed before the act begins
/// and none of them is read twice or skipped, rows sharing a timestamp included (they are ordered by <c>id</c>, so a
/// boundary may fall inside them); a row committed while the act runs is read only if its key falls in a batch not yet
/// read. Its lower end is the first row, newest first, at which the batch holds <see cref="BatchRows"/> rows or the
/// decompressed size of the three payload columns of its rows in the reach reaches <see cref="BatchBytes"/>: the work of
/// the selection and the <c>UPDATE</c> is proportional to the decompressed payload they walk, which a highly
/// compressible value can make tens of times its stored size, and they walk no payload outside the reach. A value stored
/// uncompressed is sized by <c>pg_column_size</c> without detoasting it; a compressed one is decompressed in memory to
/// measure it. A batch therefore holds at most <see cref="BatchRows"/> rows and less than <see cref="BatchBytes"/> of
/// decompressed payload in the reach plus one row's, and a single row larger than the budget is a batch of its own.
/// </para>
/// <para>
/// <b>Redaction (ADR-0108 § 5).</b> On every selected row <c>ip_address</c>, <c>user_agent</c> and <c>actor_email</c>
/// become NULL. In <c>before_state</c>, <c>after_state</c> and <c>changes</c> a string at a declared path becomes
/// <see cref="SensitiveTokenCatalog.RedactedValue"/> only when it is the subject's <c>UserId</c> (a
/// <see cref="UserReferenceKind.User"/> path) or one of the frozen Membership identifiers (a
/// <see cref="UserReferenceKind.Membership"/> path), compared case-insensitively; another person's value at the same
/// path is untouched. Each <c>UPDATE</c> names only the six columns the platform role may update, by primary key, locks
/// the rows it rewrites so a concurrent redaction of the same row is never overwritten, and changes a row only where a
/// value differs, so a re-run rewrites nothing. An elided payload (<c>{"_elided":true,…}</c>) holds no value, only its
/// length and digest, so it has no path to redact and no reference to report.
/// </para>
/// <para>
/// <b>Report (ADR-0108 §§ 3, 6).</b> Counts only: the rows selected; the rows changed per column, from the
/// <c>UPDATE</c>s' own <c>RETURNING</c>; the selected rows that keep the subject as actor
/// (<c>audit_actor_reference</c>) or as entity (<c>audit_entity_reference</c>); the rows of the reach whose metadata names
/// the subject, by the Audit participant's predicates, selected or not (<c>audit_metadata_reference</c>, exact for the
/// rows committed before the act begins, because the batches read every such row of the reach anyway); and, for an administrator's request, the rows outside the group
/// whose actor or entity is the subject or a Membership (<c>unscoped_audit_rows</c>, at least: payloads and metadata
/// outside the reach are not read).
/// </para>
/// <para>
/// <b>Bounds.</b> Every statement runs under <see cref="StatementTimeout"/>; a statement that does not finish fails
/// the act, whose transaction the scope then discards. A selection statement's work grows with the rows and the
/// decompressed payload bytes of its batch, which both budgets cap, and each <c>UPDATE</c> rewrites at most
/// <see cref="UpdateRows"/> rows of one batch, so its payload is within the same byte budget; the number of batches, and
/// the act's total time, grow with the tenant's decompressed payload. What the budgets do not cap is one row: a payload column holds at most
/// <see cref="AuditJson.MaxBytes"/> (256 KiB) before it is elided, so a row is at most about 768 KiB. The budget was
/// chosen from measurement so a batch stays far inside the timeout (Audit module performance budget).
/// </para>
/// </remarks>
public sealed class AuditRedactionExecutor : IAuditRedactor
{
    /// <summary>
    /// The wall-clock bound of one statement. Measured with the budgets below, no statement took more than 2 s, on
    /// ordinary rows, on incompressible and on highly compressible payloads (Audit module performance budget). Thirty seconds leaves a degraded
    /// database room without letting one statement hold the operator's scope open for long (Audit module performance
    /// budget).
    /// </summary>
    public static readonly TimeSpan StatementTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The most of the tenant's rows one selection statement reads, newest first.</summary>
    public const int BatchRows = 100_000;

    /// <summary>
    /// The decompressed payload bytes (<c>before_state</c>, <c>after_state</c> and <c>changes</c>) at which a batch ends
    /// before <see cref="BatchRows"/>.
    /// </summary>
    /// <remarks>
    /// 64 MiB, from measurement. A value stored uncompressed is read at its stored size, and rows of 100 KiB of
    /// incompressible text were read at about 35–70 MB a second (a batch of about 650 rows took at most 2 s), so a batch
    /// stays near two seconds, under five on a disk twice as slow, and far inside <see cref="StatementTimeout"/>.
    /// Compressed payloads were walked at 0.6–5 GB a second decompressed, so their batches take a fraction of a second;
    /// counted by stored size instead, one batch of 250 KB documents stored as 3 KB held 3.7 GB, which took 1.2 s on the
    /// measuring host and has no bound on a slower one. An <c>UPDATE</c> rewrote decompressed payload at 17–165 MB a
    /// second, so a batch whose every row is selected stays within about four seconds. The decompression the boundary
    /// pays to measure compressed rows is the act's cost of the bound: at 1.5 million ordinary rows it draws 155 batches
    /// instead of 24 and a whole-tenant act takes about 12.5 s instead of 7.5 s.
    /// </remarks>
    public const long BatchBytes = 64L * 1024 * 1024;

    /// <summary>The most selected rows one <c>UPDATE</c> rewrites.</summary>
    public const int UpdateRows = 1_000;

    private const string PlatformRole = "learnstack_platform";

    // The declarations, bound as one jsonb map and read by key: {"Entity": {"p": {"Property": [shape, kind]},
    // "n": {"Navigation": {"Property": kind}}}}, so a row's declarations are a lookup by its entity type, never a join.
    private const string Declarations = "@declarations";

    // The reach (ADR-0108 § 4): the tenant, and for an administrator's request the group's organization, none for the
    // tenant-wide group. Bound per call, so the planner folds the branch that does not apply.
    private const string Reach = """
        a.tenant_id = @tenant
          AND (@whole_tenant OR (@organization IS NULL AND a.organization_id IS NULL) OR a.organization_id = @organization)
        """;

    // One batch of the tenant's rows: [lower, upper) on the key ("timestamp", id). The plain range on the timestamp is
    // what ix_audit_log_tenant_id_timestamp serves; the two id terms then split the rows that share a boundary's timestamp.
    private const string Batch = """
        a."timestamp" >= @lower AND a."timestamp" <= @upper
          AND (a."timestamp" > @lower OR a.id >= @lower_id) AND (a."timestamp" < @upper OR a.id < @upper_id)
        """;

    // The redaction of one value at a declared path, by the declaration's kind: a string equal to an identifier of its
    // kind becomes the sentinel, and so does each such element of an array of identifiers.
    private const string RedactString = """
        CASE WHEN jsonb_typeof($VALUE$) = 'string' AND lower($VALUE$ #>> '{}') = ANY(CASE $KIND$ WHEN 1 THEN @users ELSE @memberships END)
             THEN to_jsonb(@sentinel) ELSE $VALUE$ END
        """;

    private const string RedactElements = """
        CASE WHEN jsonb_typeof($VALUE$) = 'array' THEN (
            SELECT COALESCE(jsonb_agg(CASE WHEN jsonb_typeof(x.value) = 'string'
                    AND lower(x.value #>> '{}') = ANY(CASE $KIND$ WHEN 1 THEN @users ELSE @memberships END)
                    THEN to_jsonb(@sentinel) ELSE x.value END ORDER BY x.position), '[]'::jsonb)
            FROM jsonb_array_elements($VALUE$) WITH ORDINALITY AS x(value, position))
        ELSE $VALUE$ END
        """;

    // One snapshot column (an object): each declared property, each declared array, and each declared member of a
    // contained collection, keyed by the member's own key. Every other key is copied as it is, and an object with no
    // declared key comes back equal, which is what makes the UPDATE's guard skip it.
    private static readonly string RedactState = Sql(
        "CASE WHEN jsonb_typeof($VALUE$) = 'object' AND", Declarations, "? a.entity_type::text THEN (",
        "SELECT COALESCE(jsonb_object_agg(e.key, CASE",
        "WHEN (", Declarations, "#> ARRAY[a.entity_type::text, 'p']) ? e.key THEN",
        "CASE WHEN (", Declarations, "#>> ARRAY[a.entity_type::text, 'p', e.key, '0']) = '3' THEN",
        Format(RedactElements, "e.value", "(" + Declarations + " #>> ARRAY[a.entity_type::text, 'p', e.key, '1'])::int"),
        "ELSE",
        Format(RedactString, "e.value", "(" + Declarations + " #>> ARRAY[a.entity_type::text, 'p', e.key, '1'])::int"),
        "END",
        "WHEN (", Declarations, "#> ARRAY[a.entity_type::text, 'n']) ? e.key AND jsonb_typeof(e.value) = 'object' THEN (",
        "SELECT COALESCE(jsonb_object_agg(m.key, CASE WHEN jsonb_typeof(m.value) = 'object' THEN (",
        "SELECT COALESCE(jsonb_object_agg(f.key, CASE WHEN (", Declarations, "#> ARRAY[a.entity_type::text, 'n', e.key]) ? f.key THEN",
        Format(RedactString, "f.value", "(" + Declarations + " #>> ARRAY[a.entity_type::text, 'n', e.key, f.key])::int"),
        """
        ELSE f.value END), '{}'::jsonb)
                FROM jsonb_each(m.value) AS f)
            ELSE m.value END), '{}'::jsonb)
            FROM jsonb_each(e.value) AS m)
        ELSE e.value END), '{}'::jsonb)
        FROM jsonb_each($VALUE$) AS e)
        ELSE $VALUE$ END
        """);

    // The changes column (an array of {path, before, after}): an item whose path is /{the row's entity type}/{id}/ and
    // then a declared property, or a declared navigation, a member key and a property, has its before and after
    // redacted; the id segment is whatever instance the item describes. Every other item is copied in place.
    private static readonly string RedactChanges = Sql(
        "CASE WHEN jsonb_typeof($VALUE$) = 'array' AND", Declarations, "? a.entity_type::text THEN (",
        "SELECT COALESCE(jsonb_agg(CASE WHEN s.kind IS NULL THEN i.item ELSE",
        "jsonb_set(jsonb_set(i.item, '{before}', COALESCE(CASE WHEN s.shape = 3 THEN",
        Format(RedactElements, "(i.item -> 'before')", "s.kind"), "ELSE", Format(RedactString, "(i.item -> 'before')", "s.kind"),
        "END, 'null'::jsonb), false), '{after}', COALESCE(CASE WHEN s.shape = 3 THEN",
        Format(RedactElements, "(i.item -> 'after')", "s.kind"), "ELSE", Format(RedactString, "(i.item -> 'after')", "s.kind"),
        """
        END, 'null'::jsonb), false) END ORDER BY i.position), '[]'::jsonb)
        FROM jsonb_array_elements($VALUE$) WITH ORDINALITY AS i(item, position)
        CROSS JOIN LATERAL (SELECT string_to_array(i.item ->> 'path', '/') AS segments) AS p
        CROSS JOIN LATERAL (SELECT
            CASE WHEN cardinality(p.segments) = 6 THEN 2
                 ELSE (
        """,
        Declarations, "#>> ARRAY[a.entity_type::text, 'p', p.segments[4], '0'])::int END AS shape,",
        "CASE WHEN p.segments[1] <> '' OR p.segments[2] <> a.entity_type::text THEN NULL",
        "WHEN cardinality(p.segments) = 4 THEN (", Declarations, "#>> ARRAY[a.entity_type::text, 'p', p.segments[4], '1'])::int",
        "WHEN cardinality(p.segments) = 6 THEN (", Declarations, "#>> ARRAY[a.entity_type::text, 'n', p.segments[4], p.segments[6]])::int",
        """
            END AS kind) AS s)
        ELSE $VALUE$ END
        """);

    private static readonly string RedactedBefore = Format(RedactState, "a.before_state");
    private static readonly string RedactedAfter = Format(RedactState, "a.after_state");
    private static readonly string RedactedChanges = Format(RedactChanges, "a.changes");

    // The cheap filter: one level-bounded walk per payload column for a string equal to either canonical spelling of
    // an identifier (the capture writes the lower-case one). It reaches every path the grammar can declare (a
    // property, an element of an array property, a member's property; a changes item's before and after and their
    // elements) and more, so it only narrows: the rewrite decides.
    private const string MayHold = """
        (jsonb_path_query_array(a.before_state, 'lax $.**{1 to 3}') ?| @spellings
          OR jsonb_path_query_array(a.after_state, 'lax $.**{1 to 3}') ?| @spellings
          OR jsonb_path_query_array(a.changes, 'lax $.**{1 to 3}') ?| @spellings)
        """;

    // A declared path holds an identifier exactly when the redaction would change the row's payload.
    private static readonly string Holds = Sql(
        "(", RedactedBefore, "IS DISTINCT FROM a.before_state OR", RedactedAfter, "IS DISTINCT FROM a.after_state OR",
        RedactedChanges, "IS DISTINCT FROM a.changes)");

    // The metadata person references the Audit participant reads (ADR-0107 § 6): the subject as causal actor, top level
    // or in the authorization evidence, or as a provider event's affected user; and a frozen Membership in the
    // authorization evidence of a row the subject acted in.
    private const string NamesSubject = """
        COALESCE(a.metadata ->> 'causal_actor_user_id' = @subject_text
          OR a.metadata #>> '{authorization,causal_actor_user_id}' = @subject_text
          OR a.metadata #>> '{provider_event,affected_user_id}' = @subject_text
          OR (a.actor_user_id = @subject AND COALESCE(a.metadata -> 'authorization' -> 'memberships', '[]'::jsonb) ?| @memberships), false)
        """;

    // The role, and whether JIT is on for this transaction.
    private const string EnterSql = "SELECT current_user::text, current_setting('jit') = 'on'";

    // JIT off for the redaction's own statements, and back on after them when it was on. Their long per-row expressions
    // carry planner estimates far above jit_optimize_above_cost, and compiling them cost more than running them
    // (measured on 1.5 million rows: a selection batch took 1.6–3.1 s with JIT and at most 0.96 s without, a whole-tenant
    // act 41–42 s against 10–12 s). A planner setting, transaction-local, and no session announcement: the scope's
    // tenant-free context is untouched.
    private const string JitOffSql = "SET LOCAL jit = off";

    private const string JitOnSql = "SET LOCAL jit = on";

    // One payload column's decompressed size: its stored size when it is stored uncompressed, which pg_column_size
    // reads without detoasting, and the size of the decompressed value when it is compressed. The empty path #> '{}'
    // hands back the whole document, decompressed by the operator's argument fetch, so pg_column_size measures it in
    // memory: the decompression the selection pays anyway, without rendering the document as text (measured on 1.5
    // million rows, 1.9 s against 4.2 s for octet_length(value::text)). NULL is zero.
    private const string PayloadBytes = """
        CASE WHEN pg_column_compression($VALUE$) IS NULL THEN COALESCE(pg_column_size($VALUE$), 0)
             ELSE pg_column_size($VALUE$ #> '{}') END
        """;

    // The next batch's lower bound: newest first below the upper one, the first row at which the batch holds
    // @batch_rows rows or the payload of its rows in the reach reaches @batch_bytes. A payload's size is what the
    // selection and the UPDATE work through, its decompressed size: a value stored uncompressed (inline or out of line)
    // is sized by pg_column_size, which reads it from the header or the TOAST pointer without detoasting; a compressed one
    // has no such header, so it is decompressed once to measure it, as the selection then does anyway. A row
    // outside the reach counts no bytes: the selection reads its key and organization and never its payload, and the
    // row cap bounds those reads. The inner query reads the
    // rows in order from ix_audit_log_tenant_id_timestamp (ties sorted by id incrementally) and sizes each row where
    // it reads it, so the sort carries a key and a number, never a payload. Its LIMIT is the row cap no batch exceeds,
    // which keeps the inner query a top-N that the index serves in order. The running sums stream, so the statement
    // stops at the boundary. No row once fewer rows and bytes remain, and the last batch is open below.
    private static readonly string BoundarySql = Checked(Sql(
        """
        SELECT b."timestamp", b.id FROM (
            SELECT s."timestamp", s.id, count(*) OVER batch AS n, sum(s.bytes) OVER batch AS bytes
            FROM (
                SELECT a."timestamp", a.id,
                    CASE WHEN @whole_tenant OR (@organization IS NULL AND a.organization_id IS NULL) OR a.organization_id = @organization
                    THEN
        """,
        Format(PayloadBytes, "a.before_state"), "+", Format(PayloadBytes, "a.after_state"), "+", Format(PayloadBytes, "a.changes"),
        """
                    ELSE 0 END AS bytes
                FROM public.audit_log AS a
                WHERE a.tenant_id = @tenant AND a."timestamp" <= @upper AND (a."timestamp" < @upper OR a.id < @upper_id)
                ORDER BY a."timestamp" DESC, a.id DESC
                LIMIT @batch_rows) AS s
            WINDOW batch AS (ORDER BY s."timestamp" DESC, s.id DESC ROWS UNBOUNDED PRECEDING)) AS b
        WHERE b.n >= @batch_rows OR b.bytes >= @batch_bytes
        ORDER BY b."timestamp" DESC, b.id DESC
        LIMIT 1
        """));

    // What a matched row reports, read from the row itself: whether it is selected (its actor, its entity or a declared
    // path names the subject), whether the actor or the entity is the subject, and whether its metadata names the
    // subject. Every value is the row's own, so a row two branches match is one row of the union. The rewrite that
    // decides a payload reference runs only where the cheap filter keeps the row, as in the third branch's WHERE: a row
    // matched for its metadata alone, or by the actor or entity branch, never pays it for nothing.
    private static readonly string Matched = Sql(
        "SELECT a.id, a.\"timestamp\",",
        "CASE WHEN a.actor_user_id = @subject OR a.entity_id = ANY(@identifiers) THEN true",
        "WHEN", Declarations, "? a.entity_type::text AND", MayHold, "THEN", Holds, "ELSE false END,",
        "COALESCE(a.actor_user_id = @subject, false), COALESCE(a.entity_id = ANY(@identifiers), false),",
        NamesSubject,
        "FROM public.audit_log AS a WHERE", Reach, "AND", Batch);

    // One batch's selection: the actor and the entity branches, index-served, and one pass over the batch's rows of the
    // reach for a metadata or a payload reference. A row that names the subject only in its metadata is counted and
    // never redacted.
    private static readonly string SelectionSql = Checked(Sql(
        Matched, "AND a.actor_user_id = @subject",
        "UNION",
        Matched, "AND a.entity_id = ANY(@identifiers)",
        "UNION",
        Matched, "AND (", NamesSubject,
        "OR CASE WHEN", Declarations, "? a.entity_type::text AND", MayHold, "THEN", Holds, "ELSE false END)"));

    // The rows an administrator's request deliberately does not reach that still name the subject as actor or entity:
    // the tenant's rows outside the group's organization (outside the tenant level for the tenant-wide group).
    private const string UnscopedSql = """
        SELECT count(*) FROM (
            SELECT a.id, a."timestamp" FROM public.audit_log AS a
            WHERE a.tenant_id = @tenant AND a.actor_user_id = @subject
              AND (CASE WHEN @organization IS NULL THEN a.organization_id IS NOT NULL ELSE a.organization_id IS DISTINCT FROM @organization END)
            UNION
            SELECT a.id, a."timestamp" FROM public.audit_log AS a
            WHERE a.tenant_id = @tenant AND a.entity_id = ANY(@identifiers)
              AND (CASE WHEN @organization IS NULL THEN a.organization_id IS NOT NULL ELSE a.organization_id IS DISTINCT FROM @organization END)
        ) AS outside
        """;

    // The redaction of one set of selected rows, by primary key and inside the tenant. Each row is locked and its new
    // payload computed once from the version the lock returns, so a concurrent redaction of the same row is never
    // overwritten; a row changes only where a value differs, and the statement returns which of the six columns it
    // changed. A payload column the redaction leaves equal keeps its own stored value, so an unchanged large payload is
    // not recompressed, rewritten to TOAST and logged again only because the rewrite rebuilt an equal copy.
    private static readonly string RedactSql = Checked(Sql(
        "WITH locked AS MATERIALIZED (",
        "SELECT a.id, a.\"timestamp\",", RedactedBefore, "AS before_state,", RedactedAfter, "AS after_state,", RedactedChanges, "AS changes",
        """
        FROM unnest(@ids, @timestamps) AS t(id, "timestamp")
        JOIN public.audit_log AS a ON a.id = t.id AND a."timestamp" = t."timestamp"
        WHERE a.tenant_id = @tenant
        FOR UPDATE OF a)
        UPDATE public.audit_log AS a
        SET ip_address = NULL, user_agent = NULL, actor_email = NULL,
            before_state = CASE WHEN a.before_state IS DISTINCT FROM l.before_state THEN l.before_state ELSE a.before_state END,
            after_state = CASE WHEN a.after_state IS DISTINCT FROM l.after_state THEN l.after_state ELSE a.after_state END,
            changes = CASE WHEN a.changes IS DISTINCT FROM l.changes THEN l.changes ELSE a.changes END
        FROM locked AS l
        WHERE a.id = l.id AND a."timestamp" = l."timestamp"
          AND (a.ip_address IS NOT NULL OR a.user_agent IS NOT NULL OR a.actor_email IS NOT NULL
            OR a.before_state IS DISTINCT FROM l.before_state OR a.after_state IS DISTINCT FROM l.after_state
            OR a.changes IS DISTINCT FROM l.changes)
        RETURNING old.ip_address IS NOT NULL, old.user_agent IS NOT NULL, old.actor_email IS NOT NULL,
            old.before_state IS DISTINCT FROM new.before_state, old.after_state IS DISTINCT FROM new.after_state,
            old.changes IS DISTINCT FROM new.changes
        """));

    // The open ends of the first and the last batch: infinity and -infinity on the wire, so the id beside them is never
    // compared for a row with a finite timestamp. A row stamped +infinity would be outside the keyset and never read;
    // no writer produces one (the composer stamps a clock reading), and -infinity is inside the last batch.
    private static readonly Key OpenUpper = new(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), Guid.Empty);
    private static readonly Key OpenLower = new(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc), Guid.Empty);

    private readonly string _declarations;
    private readonly int _batchRows;
    private readonly long _batchBytes;
    private readonly int _updateRows;

    /// <summary>Merges and checks every registered locator's declarations once, and binds them as one map.</summary>
    /// <exception cref="ArgumentException">The declarations are empty, duplicated or ambiguous (<see cref="UserReferenceCatalog"/>).</exception>
    public AuditRedactionExecutor(IEnumerable<IUserReferenceLocator> locators)
        : this(locators, BatchRows, BatchBytes, UpdateRows)
    {
    }

    /// <summary>The same executor with other batch and chunk sizes, so a test proves the batching with a few rows.</summary>
    /// <exception cref="ArgumentException">The declarations are empty, duplicated or ambiguous.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A size is not positive.</exception>
    internal AuditRedactionExecutor(IEnumerable<IUserReferenceLocator> locators, int batchRows, long batchBytes, int updateRows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(updateRows);
        var catalog = new UserReferenceCatalog(locators);
        if (catalog.Redactable.IsEmpty)
        {
            throw new ArgumentException("The redaction needs at least one registered user reference locator.", nameof(locators));
        }

        _declarations = DeclarationMap(catalog);
        _batchRows = batchRows;
        _batchBytes = batchBytes;
        _updateRows = updateRows;
    }

    /// <inheritdoc />
    public async Task<AuditRedactionReport> RedactAsync(
        AuditRedactionPlan plan,
        PlatformAdmission admission,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        JobApplicationSourceOwner.RefuseExternalEntry();
        if (JobApplicationSourceOwner.Current is not null)
        {
            throw new InvalidOperationException("Audit redaction is an operator act and never runs in a job frame.");
        }

        if (!Sanctions(admission.Operation, plan))
        {
            throw new InvalidOperationException("The admission does not name an operation that may redact this plan's audit rows.");
        }

        // The scope consumed the admission on entry; an admission still unspent never opened a scope, and is spent here.
        if (admission.TryConsume())
        {
            throw new InvalidOperationException("Audit redaction runs inside the platform scope its admission opened.");
        }

        if (connection is not NpgsqlConnection platform || transaction is not NpgsqlTransaction work
            || !ReferenceEquals(work.Connection, platform) || platform.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("Audit redaction runs on the platform scope's open connection and its work transaction.");
        }

        var run = new Run(this, plan, platform, work);
        var jit = await run.EnterAsync(cancellationToken).ConfigureAwait(false);
        var tally = await run.RedactBatchesAsync(cancellationToken).ConfigureAwait(false);
        List<RetainedReferenceCount> retained =
        [
            new(RetainedReferenceKind.AuditActorReference, tally.Actor),
            new(RetainedReferenceKind.AuditEntityReference, tally.Entity),
            new(RetainedReferenceKind.AuditMetadataReference, tally.Metadata),
        ];
        if (!plan.WholeTenant)
        {
            retained.Add(new(RetainedReferenceKind.UnscopedAuditRows,
                await run.ScalarAsync<long>(UnscopedSql, cancellationToken).ConfigureAwait(false)));
        }

        await run.RestoreAsync(jit, cancellationToken).ConfigureAwait(false);
        return new AuditRedactionReport(tally.Selected, tally.Redacted, retained);
    }

    /// <summary>
    /// The one place an operation is bound to the plans it may redact: <c>complete_erasure</c>
    /// (<c>audit.redaction.apply</c>) redacts an erasure request's rows. A closure's Finalize joins here when ADR-0107
    /// § 7's operation is registered.
    /// </summary>
    private static bool Sanctions(PlatformOperation operation, AuditRedactionPlan plan) =>
        ReferenceEquals(operation, PlatformOperations.ErasureComplete) && plan.Subject.Kind == DataRequestKind.Erasure;

    /// <summary>
    /// The declarations as the statements read them: per entity name, <c>p</c> maps a property to its shape and kind
    /// and <c>n</c> maps a contained collection's navigation to its members' declared properties and their kinds.
    /// </summary>
    private static string DeclarationMap(UserReferenceCatalog catalog)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            foreach (var entity in catalog.Redactable.GroupBy(declaration => declaration.EntityName, StringComparer.Ordinal))
            {
                json.WriteStartObject(entity.Key);
                json.WriteStartObject("p");
                foreach (var declaration in entity.Where(declaration => declaration.Path.Navigation is null))
                {
                    json.WriteStartArray(declaration.Path.Property);
                    json.WriteNumberValue((int)declaration.Path.Shape);
                    json.WriteNumberValue((int)declaration.Kind);
                    json.WriteEndArray();
                }

                json.WriteEndObject();
                json.WriteStartObject("n");
                foreach (var navigation in entity.Where(declaration => declaration.Path.Navigation is not null)
                    .GroupBy(declaration => declaration.Path.Navigation!, StringComparer.Ordinal))
                {
                    json.WriteStartObject(navigation.Key);
                    foreach (var declaration in navigation)
                    {
                        json.WriteNumber(declaration.Path.Property, (int)declaration.Kind);
                    }

                    json.WriteEndObject();
                }

                json.WriteEndObject();
                json.WriteEndObject();
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Joins fixed fragments into one statement, a line each, so no two tokens ever run together.</summary>
    private static string Sql(params string[] fragments) => string.Join('\n', fragments);

    /// <summary>A composed statement with every fragment token filled; checked once, when the type is initialized.</summary>
    private static string Checked(string sql) =>
        sql.Contains("$VALUE$", StringComparison.Ordinal) || sql.Contains("$KIND$", StringComparison.Ordinal)
            ? throw new InvalidOperationException("A redaction statement kept an unfilled fragment token.")
            : sql;

    /// <summary>Fills a fragment's value and kind tokens with fixed column and alias names, never with data.</summary>
    private static string Format(string template, string value, string? kind = null) =>
        template.Replace("$VALUE$", value, StringComparison.Ordinal).Replace("$KIND$", kind ?? "$KIND$", StringComparison.Ordinal);

    /// <summary>One end of a batch: a row's key in the batches' order, newest first.</summary>
    private readonly record struct Key(DateTime Timestamp, Guid Id);

    /// <summary>The counts one redaction accumulates over its batches.</summary>
    private sealed class Tally
    {
        public long Selected { get; set; }
        public long Actor { get; set; }
        public long Entity { get; set; }
        public long Metadata { get; set; }
        public Dictionary<AuditRedactedColumn, long> Redacted { get; } = Enum.GetValues<AuditRedactedColumn>().ToDictionary(column => column, _ => 0L);
    }

    /// <summary>One redaction's statements, all on the caller's connection and transaction.</summary>
    private sealed class Run(AuditRedactionExecutor executor, AuditRedactionPlan plan, NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        private readonly string _subject = plan.Subject.SubjectUserId.Value.ToString("D");
        private readonly string[] _memberships = [.. plan.Subject.MembershipIds.Select(membership => membership.ToString("D"))];

        /// <summary>Refuses any role but the platform's, switches JIT off, and returns whether it was on.</summary>
        public async Task<bool> EnterAsync(CancellationToken cancellationToken)
        {
            bool jit;
            await using (var command = Command(EnterSql))
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    || !string.Equals(reader.GetString(0), PlatformRole, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Audit redaction runs as the platform role, whose grants are its only bound.");
                }

                jit = reader.GetBoolean(1);
            }

            await NonQueryAsync(JitOffSql, cancellationToken).ConfigureAwait(false);
            return jit;
        }

        /// <summary>Leaves the transaction's JIT setting as the redaction found it.</summary>
        public async Task RestoreAsync(bool jit, CancellationToken cancellationToken)
        {
            if (jit)
            {
                await NonQueryAsync(JitOnSql, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task NonQueryAsync(string sql, CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = (int)StatementTimeout.TotalSeconds };
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<T> ScalarAsync<T>(string sql, CancellationToken cancellationToken)
        {
            await using var command = Command(sql);
            return (T)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        public async Task<Tally> RedactBatchesAsync(CancellationToken cancellationToken)
        {
            var tally = new Tally();
            var upper = OpenUpper;
            while (true)
            {
                Key? boundary = null;
                await using (var command = Command(BoundarySql))
                {
                    Bind(command, "upper", upper);
                    command.Parameters.AddWithValue("batch_rows", NpgsqlDbType.Bigint, (long)executor._batchRows);
                    command.Parameters.AddWithValue("batch_bytes", NpgsqlDbType.Bigint, executor._batchBytes);
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        boundary = new Key(DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc), reader.GetGuid(1));
                    }
                }

                var selected = await SelectAsync(boundary ?? OpenLower, upper, tally, cancellationToken).ConfigureAwait(false);
                foreach (var chunk in selected.Chunk(executor._updateRows))
                {
                    await RedactAsync(chunk, tally, cancellationToken).ConfigureAwait(false);
                }

                if (boundary is not { } lower)
                {
                    return tally;
                }

                upper = lower;
            }
        }

        private async Task<List<(Guid Id, DateTime Timestamp)>> SelectAsync(Key lower, Key upper, Tally tally,
            CancellationToken cancellationToken)
        {
            var selected = new List<(Guid Id, DateTime Timestamp)>();
            await using var command = Command(SelectionSql);
            Bind(command, "lower", lower);
            Bind(command, "upper", upper);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tally.Metadata += reader.GetBoolean(5) ? 1 : 0;
                if (!reader.GetBoolean(2))
                {
                    continue;
                }

                selected.Add((reader.GetGuid(0), DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc)));
                tally.Selected++;
                tally.Actor += reader.GetBoolean(3) ? 1 : 0;
                tally.Entity += reader.GetBoolean(4) ? 1 : 0;
            }

            return selected;
        }

        private async Task RedactAsync((Guid Id, DateTime Timestamp)[] rows, Tally tally, CancellationToken cancellationToken)
        {
            await using var command = Command(RedactSql);
            command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = rows.Select(row => row.Id).ToArray() });
            command.Parameters.Add(new NpgsqlParameter("timestamps", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
            {
                Value = rows.Select(row => row.Timestamp).ToArray(),
            });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                Count(tally, AuditRedactedColumn.IpAddress, reader.GetBoolean(0));
                Count(tally, AuditRedactedColumn.UserAgent, reader.GetBoolean(1));
                Count(tally, AuditRedactedColumn.ActorEmail, reader.GetBoolean(2));
                Count(tally, AuditRedactedColumn.BeforeState, reader.GetBoolean(3));
                Count(tally, AuditRedactedColumn.AfterState, reader.GetBoolean(4));
                Count(tally, AuditRedactedColumn.Changes, reader.GetBoolean(5));
            }
        }

        private static void Count(Tally tally, AuditRedactedColumn column, bool changed)
        {
            if (changed)
            {
                tally.Redacted[column]++;
            }
        }

        private NpgsqlCommand Command(string sql)
        {
            var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = (int)StatementTimeout.TotalSeconds };
            var group = plan.Subject.Group;
            string[] identifiers = [_subject, .. _memberships];
            command.Parameters.AddWithValue("tenant", group.TenantId.Value);
            command.Parameters.AddWithValue("whole_tenant", plan.WholeTenant);
            command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)group.OrganizationId?.Value ?? DBNull.Value);
            command.Parameters.AddWithValue("subject", plan.Subject.SubjectUserId.Value);
            command.Parameters.AddWithValue("subject_text", _subject);
            command.Parameters.Add(Texts("users", [_subject]));
            command.Parameters.Add(Texts("memberships", _memberships));
            command.Parameters.Add(Texts("identifiers", identifiers));
            command.Parameters.Add(Texts("spellings", [.. identifiers, .. identifiers.Select(identifier => identifier.ToUpperInvariant())]));
            command.Parameters.AddWithValue("declarations", NpgsqlDbType.Jsonb, executor._declarations);
            command.Parameters.AddWithValue("sentinel", NpgsqlDbType.Text, SensitiveTokenCatalog.RedactedValue);
            return command;
        }

        private static NpgsqlParameter Texts(string name, string[] values) =>
            new(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values };

        /// <summary>Binds one end of a batch as <c>@name</c> and <c>@name_id</c>.</summary>
        private static void Bind(NpgsqlCommand command, string name, Key key)
        {
            command.Parameters.AddWithValue(name, NpgsqlDbType.TimestampTz, key.Timestamp);
            command.Parameters.AddWithValue(name + "_id", NpgsqlDbType.Uuid, key.Id);
        }
    }
}
