using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Modules.Audit.Infrastructure.DataRequests;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using Npgsql;
using NpgsqlTypes;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The audit section's reach as the row policy really draws it, and its index-served statements against the single
/// predicate they replaced (ADR-0107 § 3, § 6; ADR-0108 § 4).
/// </summary>
public sealed partial class DataExportParticipantTests
{
    // The statements as they were before the union rewrite: the oracle the rewrite must agree with.
    private const string OracleSelection = """
        tenant_id = @tenant
          AND (NOT @scoped OR organization_id IS NOT DISTINCT FROM @organization)
          AND (actor_user_id = @subject
            OR (entity_type = 'User' AND entity_id = @subject_text)
            OR (entity_type = 'Membership' AND entity_id = ANY(@memberships)))
        """;

    private const string OracleCount = "SELECT count(*) FROM public.audit_log WHERE " + OracleSelection;

    private const string OraclePage = """
        SELECT id FROM public.audit_log
        WHERE (@first OR ("timestamp", id) < (@after_timestamp, @after_id))
        """ + " AND " + OracleSelection + """

        ORDER BY "timestamp" DESC, id DESC LIMIT @page
        """;

    private const string OracleRetained = """
        SELECT count(*) FILTER (WHERE actor_user_id = @subject),
               count(*) FILTER (WHERE (entity_type = 'User' AND entity_id = @subject_text)
                   OR (entity_type = 'Membership' AND entity_id = ANY(@memberships)))
        FROM public.audit_log WHERE
        """ + " " + OracleSelection;

    private const string OracleMetadata = """
        SELECT count(*) FROM public.audit_log
        WHERE tenant_id = @tenant
          AND (NOT @scoped OR organization_id IS NOT DISTINCT FROM @organization)
          AND (metadata ->> 'causal_actor_user_id' = @subject_text
            OR metadata #>> '{authorization,causal_actor_user_id}' = @subject_text
            OR metadata #>> '{provider_event,affected_user_id}' = @subject_text
            OR COALESCE(metadata -> 'authorization' -> 'memberships', '[]'::jsonb) ?| @memberships)
        """;

    [Theory]
    [InlineData("tenant_wide_self")]
    [InlineData("other_organization_self")]
    [InlineData("organization_administrator")]
    public async Task The_audit_section_states_its_reach_and_shows_no_other_organizations_rows(
        string group)
    {
        var (organization, origin, membership, reach, expected) = group switch
        {
            "tenant_wide_self" => ((Guid?)null, DataRequestOrigin.Self, LearnerMembershipA,
                AuditUserDataParticipant.ReachTenantLevelOnly, new[] { "tenant_level" }),
            "other_organization_self" => (SchemaFixture.OrgA2, DataRequestOrigin.Self, BranchMembershipA2,
                AuditUserDataParticipant.ReachOrganizationAndTenantLevel, new[] { "tenant_level", "organization_b" }),
            _ => (SchemaFixture.OrgA1, DataRequestOrigin.Administrator, BranchMembershipA1,
                AuditUserDataParticipant.ReachOrganizationOnly, new[] { "organization_a" }),
        };
        await using var scope = await DataExportTestKit.OpenAsync(schema.Postgres.AppConnectionString, SchemaFixture.TenantA, organization);
        foreach (var (name, rowOrganization) in new (string, Guid?)[]
                 {
                     ("tenant_level", null), ("organization_a", SchemaFixture.OrgA1), ("organization_b", SchemaFixture.OrgA2),
                 })
        {
            await InsertAuditAsync(scope, rowOrganization, PersonA, "fixture.reach." + name, "Invitation", Guid.CreateVersion7().ToString("D"));
        }

        await ScopeAsync(scope.Connection, scope.Transaction, SchemaFixture.TenantA, organization);
        var participant = new AuditUserDataParticipant(scope.Unit, scope.Context);
        var subject = new UserDataSubject(Guid.CreateVersion7(), DataRequestKind.Export, origin, UserId.From(PersonA),
            new DataRequestScopeGroup(TenantId.From(SchemaFixture.TenantA), organization is { } org ? OrganizationId.From(org) : null),
            [membership]);
        var writer = new CollectingWriter();

        await participant.ExportAsync(subject, writer, CancellationToken.None);

        AuditUserDataParticipant.ReachOf(subject).Should().Be(reach);
        writer.All[0].Collection.Should().Be(AuditUserDataParticipant.ScopeCollection);
        writer.All[0].Record.Should().Be("{\"reach\":\"" + reach + "\",\"complete\":false}",
            "the reach is one closed word and a closed flag, and no current reach is the person's whole audit trail");
        writer.All.Count(record => record.Collection == AuditUserDataParticipant.ScopeCollection).Should().Be(1);
        writer.Records.Select(record => JsonDocument.Parse(record).RootElement.GetProperty("operation").GetString()!)
            .Where(operation => operation.StartsWith("fixture.reach.", StringComparison.Ordinal))
            .Select(operation => operation["fixture.reach.".Length..])
            .Should().BeEquivalentTo(expected,
                "the job's transaction holds no tenant-wide read witness, so a self export does not reach the person's rows "
                + "recorded in another organization; only an export filed in that organization's group, which needs a "
                + "Membership there, does");
        await scope.Frame.FailAsync();
    }

    [Theory]
    [InlineData("self", "organization")]
    [InlineData("administrator", "organization")]
    [InlineData("self", "tenant_wide")]
    [InlineData("administrator", "tenant_wide")]
    public async Task The_index_served_audit_statements_select_exactly_what_the_single_predicate_selected(string origin, string group)
    {
        var tenantWide = group == "tenant_wide";
        Guid? organization = tenantWide ? null : SchemaFixture.OrgA1;
        // The subject's Membership of the group, and one of another group the metadata may name without matching.
        var membership = tenantWide ? LearnerMembershipA : BranchMembershipA1;
        var foreign = tenantWide ? BranchMembershipA1 : LearnerMembershipA;
        await using var scope = await DataExportTestKit.OpenAsync(schema.Postgres.AppConnectionString, SchemaFixture.TenantA, organization);
        var other = Guid.CreateVersion7().ToString("D");
        var subjectText = PersonA.ToString("D");
        var memberships = $$$"""{"authorization":{"memberships":["{{{membership}}}"]}}""";
        var tie = DateTime.UtcNow.AddMinutes(-5);
        var rows = new (Guid? Organization, Guid Actor, string Type, string Entity, string? Metadata, DateTime? At)[]
        {
            (SchemaFixture.OrgA1, PersonA, "Invitation", Guid.CreateVersion7().ToString("D"), memberships, null),
            (null, PersonA, "Membership", membership.ToString("D"), $$$"""{"authorization":{"memberships":["{{{foreign}}}"]}}""", null),
            (SchemaFixture.OrgA1, AdminA, "Membership", membership.ToString("D"), null, null),
            (SchemaFixture.OrgA2, AdminA, "Membership", membership.ToString("D"), null, null),
            (null, AdminA, "Membership", other, null, null),
            (null, AdminA, "Membership", membership.ToString("D"), null, null),
            (null, UserId.SystemActor.Value, "User", subjectText, $$$"""{"causal_actor_user_id":"{{{PersonA}}}"}""", null),
            (SchemaFixture.OrgA1, UserId.SystemActor.Value, "Course", other, $$$"""{"authorization":{"causal_actor_user_id":"{{{PersonA}}}"}}""", null),
            (null, UserId.SystemActor.Value, "Course", other, $$$"""{"provider_event":{"affected_user_id":"{{{PersonA}}}"}}""", null),
            (SchemaFixture.OrgA1, UserId.SystemActor.Value, "Course", other, $$$"""{"causal_actor_user_id":"{{{PersonB}}}"}""", null),
            (SchemaFixture.OrgA1, PersonA, "Course", other, $$$"""{"causal_actor_user_id":"{{{PersonA}}}"}""", null),
            (SchemaFixture.OrgA2, UserId.SystemActor.Value, "Course", other, $$$"""{"causal_actor_user_id":"{{{PersonA}}}"}""", null),
            (SchemaFixture.OrgA1, PersonA, "Invitation", other, null, tie),
            (null, PersonA, "Invitation", other, null, tie),
            (SchemaFixture.OrgA1, AdminA, "Membership", membership.ToString("D"), null, tie),
            (null, AdminB, "User", subjectText, null, tie),
            // Newest: another organization's keyed row naming the subject, which no reach of this group shows.
            (SchemaFixture.OrgA2, UserId.SystemActor.Value, "Course", other, $$$"""{"provider_event":{"affected_user_id":"{{{PersonA}}}"}}""", null),
        };
        foreach (var (rowOrganization, actor, type, entity, json, at) in rows)
        {
            await InsertAuditAsync(scope, rowOrganization, actor, "fixture.statements", type, entity, json, at);
        }

        await ScopeAsync(scope.Connection, scope.Transaction, SchemaFixture.TenantA, organization);
        var subject = new UserDataSubject(Guid.CreateVersion7(), DataRequestKind.Export,
            origin == "self" ? DataRequestOrigin.Self : DataRequestOrigin.Administrator, UserId.From(PersonA),
            new DataRequestScopeGroup(TenantId.From(SchemaFixture.TenantA), organization is { } org ? OrganizationId.From(org) : null),
            [membership]);

        var count = await ScalarAsync(scope, Statement("CountSql"), subject);
        count.Should().Be(await ScalarAsync(scope, OracleCount, subject)).And.BeGreaterThanOrEqualTo(5);
        (await RetainedAsync(scope, Statement("RetainedSql"), subject)).Should().Be(await RetainedAsync(scope, OracleRetained, subject));
        var metadata = await ScalarAsync(scope, Statement("MetadataSql"), subject, AuditUserDataParticipant.MetadataScanWindow);
        metadata.Should().Be(await ScalarAsync(scope, OracleMetadata, subject)).And.BeGreaterThan(1);
        var paged = await PagesAsync(scope, Statement("PageSql"), subject, rewritten: true);
        paged.Should().Equal(await PagesAsync(scope, OraclePage, subject, rewritten: false),
            "the union pages in the same order with the same keyset ties, a row two branches select once");
        paged.Should().OnlyHaveUniqueItems().And.HaveCount((int)count);

        // The window bounds the causal and affected references; a smaller window only ever undercounts. The newest
        // keyed row of the tenant is another organization's, so a window of one still reaches the reach's newest
        // keyed row: the window is counted inside the reach, never across the tenant.
        var bounded = await ScalarAsync(scope, Statement("MetadataSql"), subject, 1);
        bounded.Should().BeLessThan(metadata).And.BeGreaterThanOrEqualTo(1);

        // Every branch has an index path. Which index the planner takes on a table this small depends on the
        // statistics other tests leave behind, so only the absence of a sequential scan is asserted; the indexes
        // themselves are pinned against the migration by AuditUserDataParticipantTests.
        await ExecuteAsync(scope.Connection, scope.Transaction, "SET LOCAL enable_seqscan = off");
        foreach (var statement in new[] { "CountSql", "PageSql", "RetainedSql", "MetadataSql" })
        {
            var nodes = await PlanAsync(scope, Statement(statement), subject);
            nodes.Where(node => node.Relation == "audit_log").Should().NotBeEmpty(statement)
                .And.NotContain(node => node.Type == "Seq Scan", "{0} has an index path for every branch", statement);
        }

        await scope.Frame.FailAsync();
    }

    private static string Statement(string name) => (string)typeof(AuditUserDataParticipant)
        .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;

    private static async Task InsertAuditAsync(DataExportTestKit.GroupScope scope, Guid? organization, Guid actor, string operation,
        string type, string entity, string? metadata = null, DateTime? at = null)
    {
        await ScopeAsync(scope.Connection, scope.Transaction, SchemaFixture.TenantA, organization);
        await ExecuteAsync(scope.Connection, scope.Transaction, """
            INSERT INTO public.audit_log(id,timestamp,tenant_id,organization_id,actor_user_id,module,operation,operation_type,
                operation_class,entity_type,entity_id,outcome,metadata)
            VALUES(@id,COALESCE(@at::timestamptz,clock_timestamp()),@tenant,@organization,@actor,'identity',@operation,'SecurityEvent',
                'Must',@type,@entity,'success',@metadata::jsonb)
            """, ("id", Guid.CreateVersion7()), ("at", (object?)at ?? DBNull.Value), ("tenant", SchemaFixture.TenantA),
            ("organization", (object?)organization ?? DBNull.Value), ("actor", actor), ("operation", operation), ("type", type),
            ("entity", entity), ("metadata", (object?)metadata ?? DBNull.Value));
    }

    private static NpgsqlCommand Command(DataExportTestKit.GroupScope scope, string sql, UserDataSubject subject)
    {
        var command = new NpgsqlCommand(sql, scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
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

    private static async Task<long> ScalarAsync(DataExportTestKit.GroupScope scope, string sql, UserDataSubject subject, int? window = null)
    {
        await using var command = Command(scope, sql, subject);
        if (window is { } rows)
        {
            command.Parameters.AddWithValue("window", rows);
        }

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<(long Actor, long Entity)> RetainedAsync(DataExportTestKit.GroupScope scope, string sql, UserDataSubject subject)
    {
        await using var command = Command(scope, sql, subject);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return (row.GetInt64(0), row.GetInt64(1));
    }

    /// <summary>Every page of two rows, the keyset carried from each page's last row.</summary>
    private static async Task<List<Guid>> PagesAsync(DataExportTestKit.GroupScope scope, string sql, UserDataSubject subject, bool rewritten)
    {
        var ids = new List<Guid>();
        (DateTime Timestamp, Guid Id)? cursor = null;
        while (true)
        {
            var page = new List<Guid>();
            await using (var command = Command(scope, sql, subject))
            {
                command.Parameters.AddWithValue("page", 2);
                if (!rewritten)
                {
                    command.Parameters.AddWithValue("first", cursor is null);
                }

                command.Parameters.AddWithValue("after_timestamp", NpgsqlDbType.TimestampTz,
                    cursor?.Timestamp ?? (rewritten ? new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc) : DateTime.UnixEpoch));
                command.Parameters.AddWithValue("after_id", cursor?.Id ?? (rewritten ? Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff") : Guid.Empty));
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    page.Add(reader.GetGuid(0));
                }
            }

            if (page.Count == 0)
            {
                return ids;
            }

            ids.AddRange(page);
            var last = page[^1];
            var timestamp = await ScalarTimestampAsync(scope, last);
            cursor = (timestamp, last);
            if (page.Count < 2)
            {
                return ids;
            }
        }
    }

    private static async Task<DateTime> ScalarTimestampAsync(DataExportTestKit.GroupScope scope, Guid id)
    {
        await using var command = new NpgsqlCommand("SELECT \"timestamp\" FROM public.audit_log WHERE id=@id", scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue("id", id);
        return DateTime.SpecifyKind((DateTime)(await command.ExecuteScalarAsync())!, DateTimeKind.Utc);
    }

    private sealed record PlanNode(string Type, string? Relation, string? Index);

    private static async Task<List<PlanNode>> PlanAsync(DataExportTestKit.GroupScope scope, string sql, UserDataSubject subject)
    {
        await using var command = Command(scope, "EXPLAIN (FORMAT JSON) " + sql, subject);
        command.Parameters.AddWithValue("window", AuditUserDataParticipant.MetadataScanWindow);
        command.Parameters.AddWithValue("page", 200);
        command.Parameters.AddWithValue("after_timestamp", NpgsqlDbType.TimestampTz, new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc));
        command.Parameters.AddWithValue("after_id", Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));
        using var plan = JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
        var nodes = new List<PlanNode>();
        Walk(plan.RootElement[0].GetProperty("Plan"));
        return nodes;

        void Walk(JsonElement node)
        {
            nodes.Add(new PlanNode(node.GetProperty("Node Type").GetString()!,
                node.TryGetProperty("Relation Name", out var relation) ? relation.GetString() : null,
                node.TryGetProperty("Index Name", out var index) ? index.GetString() : null));
            if (node.TryGetProperty("Plans", out var children))
            {
                foreach (var child in children.EnumerateArray())
                {
                    Walk(child);
                }
            }
        }
    }
}
