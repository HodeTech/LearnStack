using System.Text.Json;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Modules.Audit.Infrastructure.DataRequests;

/// <summary>
/// The Audit module's part in subject data requests: it reads its own table, the subject's audit rows of the
/// request's scope, and never writes it
/// (<see href="../../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 1, § 3, § 6</see>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Selection.</b> A row is the subject's when the subject is its actor, or its entity is the subject or one of
/// the request's frozen Memberships. Who asked narrows the reach (ADR-0108 § 4): an administrator-requested export
/// selects only rows whose <c>organization_id</c> is the group's (none for the tenant-wide group), a self-requested
/// one sets no organization predicate of its own.
/// </para>
/// <para>
/// <b>The reach is what the row policy shows, and the bundle says so.</b> Both read on the job's ordinary tenant
/// transaction as <c>learnstack_app</c>, which holds no tenant-wide read witness, so <c>audit_log_isolation</c>
/// still bounds what is visible: an organization group sees its own organization's rows and the tenant-level rows
/// (no organization), the tenant-wide group only the tenant-level rows. A self-requested export therefore does not
/// reach the rows recorded in the person's other organizations, which ADR-0107 § 3 and ADR-0108 § 4 describe as
/// "the whole tenant"; a tenant-wide read for the job needs its own decision. Until then the section's first
/// collection, <see cref="ScopeCollection"/>, holds one record whose <c>reach</c> is a closed word
/// (<see cref="ReachOf"/>), so the person, and the Step 4 screens, can say that each organization's activity is in
/// that organization's own export.
/// </para>
/// <para>
/// <b>Index-served statements.</b> Every statement is a union of branches, each one equality on an indexed column
/// set: the actor on <c>ix_audit_log_actor_user_id_timestamp</c> and the subject's User or Membership entity on
/// <c>ix_audit_log_tenant_id_entity_type_entity_id_timestamp</c>. A row two branches select is counted and exported
/// once (the union is on the primary key). The selection, its count and the actor and entity counts are therefore
/// exact and bounded by the subject's own rows, not by the tenant's.
/// </para>
/// <para>
/// <b>The metadata count is "at least".</b> A Membership in the authorization evidence is the acting principal's own
/// (the audit composer refuses authorization attribution whose actor is not the row's actor), so that reference is
/// read exactly inside the actor branch. The causal-actor and affected-user references cannot be index-served under
/// the row policy: PostgreSQL applies a non-leakproof qual, and every JSON operator is one, only after the policy,
/// so no expression index can carry the subject's value. They are read in the newest
/// <see cref="MetadataScanWindow"/> rows carrying such a key (<c>ix_audit_log_tenant_id_person_reference_timestamp</c>),
/// which bounds the read by rows; <c>audit_metadata_reference</c> is exact while the reach holds at most that many
/// such rows and a lower bound beyond it, never more than the truth. Exact counting at any size needs the references
/// as plain columns, a change to the audit table's shape that needs its own decision.
/// </para>
/// <para>
/// The statements run under <see cref="StatementBudget"/>; a statement that cannot finish inside it rolls the job's
/// transaction back and the runtime retries the export.
/// </para>
/// <para>
/// <b>What a record holds.</b> The envelope (time, module, operation, outcome, entity type) and who acted, as
/// <c>self</c>, <c>system</c> or <c>third_party</c>; the address and user agent only of a row the subject acted in;
/// the payload columns only for a row about the subject's own Membership (and so its profile) or a consent the
/// subject recorded, rewritten by <see cref="AuditPayloadProjection"/>. Metadata, correlation, reason and every
/// identifier stay out.
/// </para>
/// <para>
/// <b>Bounds.</b> Newest first, keyset-paged on <c>(timestamp, id)</c>, under the section's 8 MiB cap: a cap hit is
/// declared with the records included and the records in total.
/// </para>
/// </remarks>
public sealed class AuditUserDataParticipant(IUnitOfWork unit, ITenantContext tenant) : IUserDataParticipant
{
    public const string ParticipantKey = "audit";
    public const string Collection = "entries";

    /// <summary>The section's first collection: one record stating which audit rows the section could reach.</summary>
    public const string ScopeCollection = "audit_scope";

    /// <summary>A self-requested export in an organization group: that organization's rows and the tenant-level rows.</summary>
    public const string ReachOrganizationAndTenantLevel = "organization_and_tenant_level";

    /// <summary>An administrator-requested export in an organization group: that organization's rows only.</summary>
    public const string ReachOrganizationOnly = "organization_only";

    /// <summary>Any export in the tenant-wide group: the tenant-level rows only.</summary>
    public const string ReachTenantLevelOnly = "tenant_level_only";

    /// <summary>
    /// The wall-clock budget of one audit statement. Larger than the 5-second default of the other participants'
    /// reads, because the audit table is the one that grows without bound until Phase 11's retention, and still
    /// bounded, so a degraded database fails the attempt for the runtime's retry rather than holding the job's
    /// transaction. At 1.9 million rows of one tenant the statements measured about 2 ms each and the windowed metadata
    /// count 1.5 s (the Audit module's performance budget records the measurement).
    /// </summary>
    public static readonly TimeSpan StatementBudget = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How many of the newest rows carrying a causal-actor or affected-user key the metadata count examines. Those
    /// references cannot be index-served under the row policy (the JSON operators are not leakproof, so PostgreSQL
    /// evaluates them only after the policy), so the read is bounded by rows rather than by the budget, and
    /// <c>audit_metadata_reference</c> is exact up to this many such rows in the reach and a lower bound beyond it.
    /// </summary>
    public const int MetadataScanWindow = 500_000;

    private const int Page = 200;

    // The reach every branch repeats: the tenant, and for an administrator's request the group's organization.
    private const string Reach = """
        tenant_id = @tenant AND (NOT @scoped OR organization_id IS NOT DISTINCT FROM @organization)
        """;

    private const string ActorBranch = "SELECT id, \"timestamp\" FROM public.audit_log WHERE " + Reach
        + " AND actor_user_id = @subject";

    private const string UserBranch = "SELECT id, \"timestamp\" FROM public.audit_log WHERE " + Reach
        + " AND entity_type = 'User' AND entity_id = @subject_text";

    private const string MembershipBranch = "SELECT id, \"timestamp\" FROM public.audit_log WHERE " + Reach
        + " AND entity_type = 'Membership' AND entity_id = ANY(@memberships)";

    // The subject as actor or entity: the union of three index-served branches, a row in two of them once.
    private const string CountSql = "SELECT count(*) FROM (" + ActorBranch + " UNION " + UserBranch + " UNION "
        + MembershipBranch + ") AS selected";

    // Keyset page, newest first: each branch reads at most one page below the cursor in its own index order, and the
    // union's newest page is the page of the whole selection.
    private const string Below = " AND \"timestamp\" <= @after_timestamp AND (\"timestamp\", id) < (@after_timestamp, @after_id)"
        + " ORDER BY \"timestamp\" DESC, id DESC LIMIT @page";

    private const string PageSql = "WITH page AS ((" + ActorBranch + Below + ") UNION (" + UserBranch + Below + ") UNION ("
        + MembershipBranch + Below + """
        ))
        SELECT a.id, a."timestamp", a.module, a.operation, a.outcome, a.entity_type, a.entity_id, a.actor_user_id,
               host(a.ip_address), a.user_agent, a.before_state, a.after_state, a.changes
        FROM page AS p
        JOIN public.audit_log AS a ON a.id = p.id AND a."timestamp" = p."timestamp"
        ORDER BY a."timestamp" DESC, a.id DESC
        LIMIT @page
        """;

    // The actor rows, and the entity rows (the User and Membership branches are disjoint by entity type).
    private const string RetainedSql = "SELECT (SELECT count(*) FROM (" + ActorBranch + ") AS actor), (SELECT count(*) FROM ("
        + UserBranch + ") AS self) + (SELECT count(*) FROM (" + MembershipBranch + ") AS membership)";

    // A metadata person reference: a frozen Membership in the authorization evidence, read inside the actor branch
    // (exact), or the causal actor (top level or in the authorization evidence) or the provider event's affected user,
    // read in the newest MetadataScanWindow rows that carry one of those keys (a lower bound past the window). The key
    // tests repeat the partial index's predicate so the planner can prove the index applies.
    private const string MetadataSql = "SELECT count(*) FROM (" + ActorBranch + """
         AND COALESCE(metadata -> 'authorization' -> 'memberships', '[]'::jsonb) ?| @memberships
        UNION
        SELECT id, "timestamp" FROM (
            SELECT id, "timestamp", metadata FROM public.audit_log
            WHERE
        """ + " " + Reach + """
         AND (metadata ? 'causal_actor_user_id' OR (metadata -> 'authorization') ? 'causal_actor_user_id'
                OR (metadata -> 'provider_event') ? 'affected_user_id')
            ORDER BY "timestamp" DESC
            LIMIT @window
        ) AS scanned
        WHERE metadata ->> 'causal_actor_user_id' = @subject_text
           OR metadata #>> '{authorization,causal_actor_user_id}' = @subject_text
           OR metadata #>> '{provider_event,affected_user_id}' = @subject_text
        ) AS referenced
        """;

    private static readonly DateTime FirstPageTimestamp = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid FirstPageId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

    public string Key => ParticipantKey;

    public long? ExportSectionCapBytes => DataRequestLimits.AuditSectionBytes;

    public async Task ExportAsync(UserDataSubject subject, IUserDataExportWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(writer);
        RequireGroup(subject);
        var scope = await UserDataExportRecord.AppendAsync(writer, ScopeCollection, json =>
        {
            json.WriteStartObject();
            json.WriteString("reach", ReachOf(subject));
            json.WriteEndObject();
        }, cancellationToken).ConfigureAwait(false);
        if (scope != UserDataExportAppend.Appended)
        {
            return;
        }

        long total;
        await using (var count = Command(CountSql, subject))
        {
            total = (long)(await ExecuteAsync(token => count.ExecuteScalarAsync(token), cancellationToken).ConfigureAwait(false))!;
        }

        var own = Own(subject);
        long included = 0;
        (DateTime Timestamp, Guid Id)? cursor = null;
        while (true)
        {
            var rows = await ReadPageAsync(subject, cursor, cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                var appended = await UserDataExportRecord.AppendAsync(writer, Collection, json => WriteRecord(json, row, subject, own),
                    cancellationToken).ConfigureAwait(false);
                switch (appended)
                {
                    case UserDataExportAppend.Appended:
                        included++;
                        break;
                    case UserDataExportAppend.SectionCapReached:
                        writer.DeclareTruncated(Collection, included, Math.Max(total, included));
                        return;
                    default:
                        return;
                }
            }

            if (rows.Count < Page)
            {
                return;
            }

            cursor = (rows[^1].Timestamp, rows[^1].Id);
        }
    }

    public async Task<IReadOnlyCollection<RetainedReferenceCount>> DescribeRetainedAsync(UserDataSubject subject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        RequireGroup(subject);
        long actor;
        long entity;
        await using (var references = Command(RetainedSql, subject))
        {
            (actor, entity) = await ExecuteAsync(async token =>
            {
                await using var row = await references.ExecuteReaderAsync(token).ConfigureAwait(false);
                return await row.ReadAsync(token).ConfigureAwait(false) ? (row.GetInt64(0), row.GetInt64(1)) : (0L, 0L);
            }, cancellationToken).ConfigureAwait(false);
        }

        long metadata;
        await using (var command = Command(MetadataSql, subject))
        {
            command.Parameters.AddWithValue("window", MetadataScanWindow);
            metadata = (long)(await ExecuteAsync(token => command.ExecuteScalarAsync(token), cancellationToken).ConfigureAwait(false))!;
        }

        return
        [
            new(RetainedReferenceKind.AuditActorReference, actor),
            new(RetainedReferenceKind.AuditEntityReference, entity),
            new(RetainedReferenceKind.AuditMetadataReference, metadata),
        ];
    }

    /// <summary>
    /// The closed reach of a request's audit section under the row policy: which organization's rows the job's
    /// transaction can see, narrowed by who asked (ADR-0108 § 4).
    /// </summary>
    public static string ReachOf(UserDataSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return subject.Group.IsTenantWide
            ? ReachTenantLevelOnly
            : subject.Origin == DataRequestOrigin.Self ? ReachOrganizationAndTenantLevel : ReachOrganizationOnly;
    }

    /// <summary>Whether a row's payload columns may be disclosed: its entity is the subject's own Membership or a consent the subject recorded.</summary>
    public static bool DisclosesPayload(AuditRow row, UserDataSubject subject) =>
        row.EntityType switch
        {
            "Membership" => row.EntityId is { } id && Guid.TryParseExact(id, "D", out var membership)
                && subject.MembershipIds.Contains(membership),
            "MembershipFieldConsent" => row.Actor == subject.SubjectUserId.Value,
            _ => false,
        };

    /// <summary>The closed actor vocabulary of an exported row.</summary>
    public static string? ActorOf(AuditRow row, UserDataSubject subject) => row.Actor switch
    {
        null => null,
        { } actor when actor == subject.SubjectUserId.Value => AuditPayloadProjection.Self,
        { } actor when actor == UserId.SystemActor.Value => "system",
        _ => AuditPayloadProjection.ThirdParty,
    };

    public static void WriteRecord(Utf8JsonWriter json, AuditRow row, UserDataSubject subject, IReadOnlySet<Guid> own)
    {
        json.WriteStartObject();
        json.WriteString("at", new DateTimeOffset(row.Timestamp, TimeSpan.Zero));
        json.WriteString("module", row.Module);
        json.WriteString("operation", row.Operation);
        json.WriteString("outcome", row.Outcome);
        if (row.EntityType is null)
        {
            json.WriteNull("entity_type");
        }
        else
        {
            json.WriteString("entity_type", row.EntityType);
        }

        var actor = ActorOf(row, subject);
        if (actor is null)
        {
            json.WriteNull("actor");
        }
        else
        {
            json.WriteString("actor", actor);
        }

        // The address and agent are the actor's: disclosed only when the actor is the subject.
        if (actor == AuditPayloadProjection.Self)
        {
            WriteText(json, "ip_address", row.IpAddress);
            WriteText(json, "user_agent", row.UserAgent);
        }

        if (DisclosesPayload(row, subject))
        {
            json.WriteStartObject("payload");
            json.WritePropertyName("before");
            AuditPayloadProjection.Write(json, row.BeforeState, own);
            json.WritePropertyName("after");
            AuditPayloadProjection.Write(json, row.AfterState, own);
            json.WritePropertyName("changes");
            AuditPayloadProjection.Write(json, row.Changes, own);
            json.WriteEndObject();
        }

        json.WriteEndObject();
    }

    public static IReadOnlySet<Guid> Own(UserDataSubject subject) =>
        new HashSet<Guid>(subject.MembershipIds) { subject.SubjectUserId.Value };

    private async Task<List<AuditRow>> ReadPageAsync(UserDataSubject subject, (DateTime Timestamp, Guid Id)? cursor,
        CancellationToken cancellationToken)
    {
        await using var command = Command(PageSql, subject);
        command.Parameters.AddWithValue("after_timestamp", NpgsqlDbType.TimestampTz, cursor?.Timestamp ?? FirstPageTimestamp);
        command.Parameters.AddWithValue("after_id", cursor?.Id ?? FirstPageId);
        command.Parameters.AddWithValue("page", Page);
        return await ExecuteAsync(async token =>
        {
            var rows = new List<AuditRow>(Page);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                rows.Add(new AuditRow(reader.GetGuid(0), DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
                    reader.GetString(2), reader.GetString(3), reader.GetString(4), Text(reader, 5), Text(reader, 6),
                    reader.IsDBNull(7) ? null : reader.GetGuid(7), Text(reader, 8), Text(reader, 9), Text(reader, 10),
                    Text(reader, 11), Text(reader, 12)));
            }

            return rows;
        }, cancellationToken).ConfigureAwait(false);
    }

    private NpgsqlCommand Command(string sql, UserDataSubject subject)
    {
        var command = new NpgsqlCommand(sql, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!)
        {
            CommandTimeout = (int)StatementBudget.TotalSeconds,
        };
        command.Parameters.AddWithValue("tenant", tenant.TenantId.Value);
        command.Parameters.AddWithValue("scoped", subject.Origin != DataRequestOrigin.Self);
        command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)subject.Group.OrganizationId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("subject", subject.SubjectUserId.Value);
        command.Parameters.AddWithValue("subject_text", subject.SubjectUserId.Value.ToString("D"));
        command.Parameters.Add(new NpgsqlParameter("memberships", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = subject.MembershipIds.Select(membership => membership.ToString("D")).ToArray(),
        });
        return command;
    }

    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> statement, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(StatementBudget);
        try
        {
            return await statement(budget.Token).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is NpgsqlException
            || failure is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            unit.MarkRollbackOnly();
            throw new InvalidOperationException("Audit export read unavailable.");
        }
    }

    private void RequireGroup(UserDataSubject subject)
    {
        if (subject.Group.TenantId != tenant.TenantId || subject.Group.OrganizationId != tenant.OrganizationId
            || !unit.HasActiveTransaction || unit.IsRollbackOnly
            || unit.GetTenantContextAnnouncement(unit.Transaction) is not { } announcement
            || announcement.TenantId != tenant.TenantId || announcement.OrganizationId != tenant.OrganizationId)
        {
            throw new InvalidOperationException("A participant reads only the announced group of its request.");
        }
    }

    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void WriteText(Utf8JsonWriter json, string name, string? value)
    {
        if (value is null)
        {
            json.WriteNull(name);
        }
        else
        {
            json.WriteString(name, value);
        }
    }

    /// <summary>One selected row, read and dropped page by page.</summary>
    public sealed record AuditRow(Guid Id, DateTime Timestamp, string Module, string Operation, string Outcome, string? EntityType,
        string? EntityId, Guid? Actor, string? IpAddress, string? UserAgent, string? BeforeState, string? AfterState, string? Changes)
    {
        public override string ToString() => nameof(AuditRow);
    }
}

/// <summary>Registers the Audit module's participant from its own boundary (ADR-0107 § 1).</summary>
public static class AuditDataRequestRegistration
{
    public static IServiceCollection AddAuditUserDataParticipant(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IUserDataParticipant, AuditUserDataParticipant>());
        return services;
    }
}
