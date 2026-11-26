using System.Text.Json.Nodes;
using LearnStack.SharedKernel.Identifiers;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class AuditRedactionCanaryTests
{
    /// <summary>The reach cases: one row per way a row can name the subject, in two organizations, the tenant level and another tenant.</summary>
    private static List<CanaryRow> Reach(Guid tenant, Guid foreign, Guid branch, Guid otherBranch)
    {
        var subject = Text(Subject);
        var administrator = Text(OtherPerson);
        JsonObject Course(string creator, string updater) => new()
        {
            ["Id"] = Guid.CreateVersion7().ToString("D"),
            ["CreatedBy"] = creator,
            ["UpdatedBy"] = updater,
            ["DeletedBy"] = null,
        };

        CanaryRow Actor(string label, Guid owner, Guid? organization, string? metadata = null) =>
            new(label, owner, organization, Subject, "Course", Guid.CreateVersion7().ToString("D"),
                Course(administrator, administrator), Course(administrator, subject), null, metadata, Selected: true, null, null, null);

        CanaryRow Entity(string label, Guid? organization, Guid membership) =>
            new(label, tenant, organization, OtherPerson, "Membership", Text(membership),
                new JsonObject { ["Id"] = Text(membership), ["UserId"] = subject, ["UpdatedBy"] = administrator }, null, null, null,
                Selected: true, null, null, null);

        CanaryRow Payload(string label, Guid owner, Guid? organization) =>
            new(label, owner, organization, OtherPerson, "Course", Guid.CreateVersion7().ToString("D"),
                Course(subject, administrator), Course(subject, administrator), null, null, Selected: true, null, null, null);

        var elided = Actor("elided/branch", tenant, branch) with
        {
            Before = JsonNode.Parse("""{"_elided":true,"bytes":300000,"sha256":"00"}"""),
            After = JsonNode.Parse("""{"_elided":true,"bytes":300001,"sha256":"01"}"""),
            Changes = JsonNode.Parse("""[{"_elided":true,"bytes":600000,"sha256":"02"}]"""),
        };
        return
        [
            Actor("actor/branch", tenant, branch),
            Actor("actor/other-branch", tenant, otherBranch),
            Actor("actor/tenant", tenant, null),
            Actor("actor+authorization/branch", tenant, branch, $$$"""{"authorization":{"memberships":["{{{Text(Membership1)}}}"]}}"""),
            elided,
            Entity("entity/branch", branch, Membership1),
            Entity("entity/tenant", null, Membership2),
            Payload("payload/branch", tenant, branch),
            Payload("payload/other-branch", tenant, otherBranch),
            Payload("payload/tenant", tenant, null),
            new("metadata/branch", tenant, branch, UserId.SystemActor.Value, "Course", Guid.CreateVersion7().ToString("D"),
                null, Course(administrator, Text(UserId.SystemActor.Value)), null,
                $$"""{"causal_actor_user_id":"{{subject}}","job_id":"{{Guid.CreateVersion7()}}"}""", Selected: false, null, null, null)
            {
                Attributed = false,
            },
            Actor("foreign/actor", foreign, null),
            Payload("foreign/payload", foreign, null),
        ];
    }

    private static async Task<Scope> OpenAsync(string platformConnectionString)
    {
        var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(platformConnectionString);
        return new Scope(connection, await connection.BeginTransactionAsync());
    }

    private static async Task InsertAsync(Scope scope, IEnumerable<CanaryRow> rows)
    {
        foreach (var row in rows)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO public.audit_log (id, "timestamp", tenant_id, organization_id, actor_user_id, actor_email, module, operation,
                    operation_type, operation_class, entity_type, entity_id, outcome, before_state, after_state, changes, correlation_id,
                    ip_address, user_agent, metadata)
                VALUES (@id, COALESCE(@at, clock_timestamp()), @tenant, @organization, @actor, @email, 'canary', 'canary.redaction.row', 'Update', 'Must',
                    @entity_type, @entity_id, 'success', @before, @after, @changes, @correlation, @address, @agent, @metadata)
                """, scope.Connection, scope.Transaction);
            command.Parameters.AddWithValue("id", row.Id);
            command.Parameters.AddWithValue("at", NpgsqlDbType.TimestampTz, (object?)row.At ?? DBNull.Value);
            command.Parameters.AddWithValue("tenant", row.Tenant);
            command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)row.Organization ?? DBNull.Value);
            command.Parameters.AddWithValue("actor", NpgsqlDbType.Uuid, (object?)row.Actor ?? DBNull.Value);
            command.Parameters.AddWithValue("email", NpgsqlDbType.Varchar, row.Attributed ? "actor@example.test" : DBNull.Value);
            command.Parameters.AddWithValue("entity_type", row.EntityType);
            command.Parameters.AddWithValue("entity_id", row.EntityId);
            command.Parameters.AddWithValue("before", NpgsqlDbType.Jsonb, (object?)row.Before?.ToJsonString() ?? DBNull.Value);
            command.Parameters.AddWithValue("after", NpgsqlDbType.Jsonb, (object?)row.After?.ToJsonString() ?? DBNull.Value);
            command.Parameters.AddWithValue("changes", NpgsqlDbType.Jsonb, (object?)row.Changes?.ToJsonString() ?? DBNull.Value);
            command.Parameters.AddWithValue("correlation", "00-" + row.Id.ToString("N") + "-01");
            command.Parameters.AddWithValue("address", NpgsqlDbType.Inet,
                row.Attributed ? System.Net.IPAddress.Parse("198.51.100.7") : DBNull.Value);
            command.Parameters.AddWithValue("agent", NpgsqlDbType.Varchar, row.Attributed ? "Canary/1.0" : DBNull.Value);
            command.Parameters.AddWithValue("metadata", NpgsqlDbType.Jsonb, (object?)row.Metadata ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Every row's six redactable columns, and every other column as one canonical document.</summary>
    private static async Task<Dictionary<Guid, Snapshot>> ReadAsync(Scope scope, IEnumerable<CanaryRow> rows)
    {
        await using var command = new NpgsqlCommand("""
            SELECT a.id, host(a.ip_address), a.user_agent, a.actor_email, a.before_state::text, a.after_state::text, a.changes::text,
                (to_jsonb(a) - 'ip_address' - 'user_agent' - 'actor_email' - 'before_state' - 'after_state' - 'changes')::text
            FROM public.audit_log AS a WHERE a.id = ANY(@ids)
            """, scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue("ids", rows.Select(row => row.Id).ToArray());
        var read = new Dictionary<Guid, Snapshot>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            read[reader.GetGuid(0)] = new Snapshot(Nullable(reader, 1), Nullable(reader, 2), Nullable(reader, 3), Nullable(reader, 4),
                Nullable(reader, 5), Nullable(reader, 6), reader.GetString(7));
        }

        return read;
    }

    private static string? Nullable(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>A platform-role connection and the work transaction every case rolls back.</summary>
    private sealed record Scope(NpgsqlConnection Connection, NpgsqlTransaction Transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    /// <summary>One audit row a case writes, and what its payload must read after the redaction.</summary>
    private sealed record CanaryRow(string Label, Guid Tenant, Guid? Organization, Guid? Actor, string EntityType, string EntityId,
        JsonNode? Before, JsonNode? After, JsonNode? Changes, string? Metadata, bool Selected, JsonNode? ExpectedBefore,
        JsonNode? ExpectedAfter, JsonNode? ExpectedChanges)
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();

        /// <summary>Whether the row carries an address, an agent and an e-mail, as a row with an authenticated actor does.</summary>
        public bool Attributed { get; init; } = true;

        /// <summary>The row's timestamp; the insert's clock when none is given.</summary>
        public DateTime? At { get; init; }
    }

    /// <summary>A row as read back: the six columns, and the rest canonical, so two readings compare by value.</summary>
    private sealed record Snapshot(string? IpAddress, string? UserAgent, string? ActorEmail, string? BeforeText, string? AfterText,
        string? ChangesText, string Retained);
}
