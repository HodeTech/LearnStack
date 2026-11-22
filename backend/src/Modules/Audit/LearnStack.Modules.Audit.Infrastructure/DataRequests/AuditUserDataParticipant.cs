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
/// the request's frozen Memberships. Who asked decides the reach (ADR-0108 § 4): a self-requested export selects in
/// the whole tenant, an administrator-requested one only rows whose <c>organization_id</c> is the group's (none for
/// the tenant-wide group). Both read on the ambient tenant transaction as <c>learnstack_app</c>, so the audit row
/// policy still bounds what is visible: rows of another organization are visible only under a tenant-wide read.
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
    private const int Page = 200;

    // One literal predicate: the subject as actor or entity, and the reach by origin. Every statement repeats it.
    private const string Selection = """
        tenant_id = @tenant
          AND (NOT @scoped OR organization_id IS NOT DISTINCT FROM @organization)
          AND (actor_user_id = @subject
            OR (entity_type = 'User' AND entity_id = @subject_text)
            OR (entity_type = 'Membership' AND entity_id = ANY(@memberships)))
        """;

    private const string CountSql = "SELECT count(*) FROM public.audit_log WHERE " + Selection;

    private const string PageSql = """
        SELECT id, "timestamp", module, operation, outcome, entity_type, entity_id, actor_user_id,
               host(ip_address), user_agent, before_state, after_state, changes
        FROM public.audit_log
        WHERE (@first OR ("timestamp", id) < (@after_timestamp, @after_id))
        """ + " AND " + Selection + " " + """
        ORDER BY "timestamp" DESC, id DESC
        LIMIT @page
        """;

    private const string RetainedSql = """
        SELECT count(*) FILTER (WHERE actor_user_id = @subject),
               count(*) FILTER (WHERE (entity_type = 'User' AND entity_id = @subject_text)
                   OR (entity_type = 'Membership' AND entity_id = ANY(@memberships)))
        FROM public.audit_log
        """ + " WHERE " + Selection;

    private const string MetadataSql = """
        SELECT count(*) FROM public.audit_log
        WHERE tenant_id = @tenant
          AND (NOT @scoped OR organization_id IS NOT DISTINCT FROM @organization)
          AND (metadata ->> 'causal_actor_user_id' = @subject_text
            OR metadata #>> '{authorization,causal_actor_user_id}' = @subject_text
            OR metadata #>> '{provider_event,affected_user_id}' = @subject_text
            OR COALESCE(metadata -> 'authorization' -> 'memberships', '[]'::jsonb) ?| @memberships)
        """;

    public string Key => ParticipantKey;

    public long? ExportSectionCapBytes => DataRequestLimits.AuditSectionBytes;

    public async Task ExportAsync(UserDataSubject subject, IUserDataExportWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(writer);
        RequireGroup(subject);
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
            metadata = (long)(await ExecuteAsync(token => command.ExecuteScalarAsync(token), cancellationToken).ConfigureAwait(false))!;
        }

        return
        [
            new(RetainedReferenceKind.AuditActorReference, actor),
            new(RetainedReferenceKind.AuditEntityReference, entity),
            new(RetainedReferenceKind.AuditMetadataReference, metadata),
        ];
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
        command.Parameters.AddWithValue("first", cursor is null);
        command.Parameters.AddWithValue("after_timestamp", NpgsqlDbType.TimestampTz, cursor?.Timestamp ?? DateTime.UnixEpoch);
        command.Parameters.AddWithValue("after_id", cursor?.Id ?? Guid.Empty);
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
        var command = new NpgsqlCommand(sql, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!) { CommandTimeout = 5 };
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
        budget.CancelAfter(TimeSpan.FromSeconds(5));
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
