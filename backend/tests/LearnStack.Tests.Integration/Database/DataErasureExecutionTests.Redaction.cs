using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.SharedKernel.Platform;
using LearnStack.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class DataErasureExecutionTests
{
    /// <summary>
    /// ADR-0108 end to end: the erasure job writes its rows through the real capture, then the operator's completion runs
    /// the real executor through the real platform scope of the API composition, and no payload column of the tenant's
    /// audit log names the person or one of the request's Memberships any more, except the own key of a User or a
    /// Membership row, which is that row's <c>entity_id</c>. The second case is a person admitted again after an earlier
    /// Membership was revoked, whose erasure records two Memberships and two invitations on one row each.
    /// </summary>
    [Theory]
    [InlineData("one_membership")]
    [InlineData("readmitted")]
    public async Task The_erasure_and_its_privileged_step_leave_no_identifier_of_the_person_in_any_audit_payload(string variant)
    {
        await using var database = await OpenDatabaseAsync();
        Guid[] memberships = [LearnerMembershipA];
        if (variant == "readmitted")
        {
            await SetStatusAsync(SchemaFixture.TenantA, null, LearnerMembershipA, "Revoked");
            var current = Guid.CreateVersion7();
            await using (var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString))
            await using (var transaction = await app.BeginTransactionAsync())
            {
                await ScopeAsync(app, transaction, SchemaFixture.TenantA, null);
                await LockAsync(app, transaction, SchemaFixture.TenantA, PersonA, false);
                await InsertMemberAsync(app, transaction, SchemaFixture.TenantA, null, current, PersonA,
                    RoleId(SchemaFixture.TenantA, LearnStack.SharedKernel.Permissions.BuiltInRoleKey.Learner,
                        LearnStack.SharedKernel.Permissions.PermissionScope.Tenant));
                await transaction.CommitAsync();
            }

            memberships = [LearnerMembershipA, current];
            await IssueInvitationAsync(SchemaFixture.TenantA, null, PersonA);
        }

        await IssueInvitationAsync(SchemaFixture.TenantA, null, PersonA);
        await FileAsync(SchemaFixture.TenantA, null, PersonA, memberships, "export");
        var erasure = await FileDueErasureAsync(SchemaFixture.TenantA, null, PersonA, memberships);
        await using var host = Host(platform: true);
        (await RunAsync(host.Services, await DataErasureTestKit.SubmitAsync(database, SchemaFixture.TenantA, null, erasure)))
            .Committed();
        (await RequestAsync(SchemaFixture.TenantA, null, erasure)).Status.Should().Be("awaiting_privileged_step");
        // The identifiers, and the personal content the erasure removed: the profile's display name, which the payloads must
        // not carry either, and the e-mail of an invitation the person issued.
        string[] identifiers = [PersonA.ToString("D"), .. memberships.Select(membership => membership.ToString("D")),
            "profile-canary", "@example.invalid"];

        // The job's rows came from the capture and name the person: the proof below is not vacuous.
        var named = await IdentifiersInPayloadsAsync(SchemaFixture.TenantA, identifiers);
        named.Select(occurrence => occurrence.EntityType).Distinct().Should()
            .Contain(["Membership", "Invitation", "DataSubjectRequest", "DataExportArtifact"]);

        // A row the person acted in, with every attributed scalar column set: the proof that those columns are cleared is
        // not vacuous either, as the job's own rows carry no address or agent.
        var attributed = Guid.CreateVersion7();
        await using (var platform = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.PlatformConnectionString))
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO public.audit_log (id, "timestamp", tenant_id, organization_id, actor_user_id, actor_email, module, operation,
                operation_type, operation_class, entity_type, entity_id, outcome, correlation_id, ip_address, user_agent)
            VALUES (@id, clock_timestamp(), @tenant, NULL, @actor, 'person@example.test', 'e2e', 'e2e.redaction.row', 'Update', 'Must',
                'Course', @entity, 'success', @correlation, '198.51.100.7'::inet, 'E2E/1.0')
            """, platform))
        {
            insert.Parameters.AddWithValue("id", attributed);
            insert.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
            insert.Parameters.AddWithValue("actor", PersonA);
            insert.Parameters.AddWithValue("entity", Guid.CreateVersion7().ToString("D"));
            insert.Parameters.AddWithValue("correlation", "00-" + attributed.ToString("N") + "-01");
            await insert.ExecuteNonQueryAsync();
        }

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IDataRequestOperatorOperations>().CompleteErasureAsync(
                TestPlatformAdmissions.Admission(PlatformOperations.ErasureComplete, "INC-E2E-1",
                    correlationId: "00-erasure-e2e-" + Guid.NewGuid().ToString("N")), erasure);
            result.Outcome.Should().Be(ErasureCompletionOutcome.Completed);
            result.Report!.RowsSelected.Should().BeGreaterThan(0);
        }

        // Every survivor at once, one a line, so a failure names each undeclared reference rather than the first.
        string.Join(Environment.NewLine, await IdentifiersInPayloadsAsync(SchemaFixture.TenantA, identifiers)).Should()
            .BeEmpty("no payload names the person or a request Membership after the privileged step, except own keys");

        // The scalar columns of every row that named the person, and of the row the person acted in, are cleared.
        var rows = named.Select(occurrence => occurrence.Row).Append(attributed).Distinct().ToArray();
        await using var reader = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.PlatformConnectionString);
        await using var survivors = new NpgsqlCommand("""
            SELECT count(*) FILTER (WHERE actor_email IS NOT NULL), count(*) FILTER (WHERE ip_address IS NOT NULL),
                count(*) FILTER (WHERE user_agent IS NOT NULL), count(*)
            FROM public.audit_log WHERE tenant_id = @tenant AND id = ANY(@rows)
            """, reader);
        survivors.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
        survivors.Parameters.AddWithValue("rows", rows);
        await using var counts = await survivors.ExecuteReaderAsync();
        (await counts.ReadAsync()).Should().BeTrue();
        (counts.GetInt64(0), counts.GetInt64(1), counts.GetInt64(2)).Should().Be((0, 0, 0),
            "no e-mail, address or agent survives on a row that named the person");
        counts.GetInt64(3).Should().Be(rows.Length, "every row the proof names is still there: redaction rewrites rows, it deletes none");
    }

    private static readonly string[] Sides = ["before", "after"];

    /// <summary>Where an identifier occurs in one audit row's payload.</summary>
    private sealed record PayloadOccurrence(Guid Row, string EntityType, string EntityId, string Column, string Pointer, string Text)
    {
        public override string ToString() => $"{EntityType} {EntityId} {Column}{Pointer}: {Text}";
    }

    /// <summary>
    /// Every occurrence, case-insensitive and as a substring, of the identifiers in the tenant's <c>before_state</c>,
    /// <c>after_state</c> and <c>changes</c>, in values, object keys and <c>changes</c> paths, read as the platform role;
    /// the retained keys of a User or a Membership (a row's own <c>Id</c> state, and the instance segment of every
    /// User or Membership <c>changes</c> path with that instance's <c>Id</c> item) are left out.
    /// </summary>
    private async Task<List<PayloadOccurrence>> IdentifiersInPayloadsAsync(Guid tenant, string[] identifiers)
    {
        await using var platform = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database!.PlatformConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT id, entity_type, COALESCE(entity_id, ''), before_state::text, after_state::text, changes::text
            FROM public.audit_log WHERE tenant_id = @tenant
            """, platform);
        command.Parameters.AddWithValue("tenant", tenant);
        var found = new List<PayloadOccurrence>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var (row, type, entity) = (reader.GetGuid(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1), reader.GetString(2));
            var ownKey = type is "User" or "Membership" ? entity : null;
            foreach (var (ordinal, column) in new[] { (3, "before_state"), (4, "after_state"), (5, "changes") })
            {
                if (reader.IsDBNull(ordinal))
                {
                    continue;
                }

                var occurrences = new List<(string Pointer, string Text)>();
                Walk(WithoutOwnKey(JsonNode.Parse(reader.GetString(ordinal)), type, ownKey), string.Empty, identifiers, occurrences);
                found.AddRange(occurrences.Select(occurrence =>
                    new PayloadOccurrence(row, type, entity, column, occurrence.Pointer, occurrence.Text)));
            }
        }

        return found;
    }

    private static void Walk(JsonNode? node, string pointer, string[] identifiers, List<(string Pointer, string Text)> found)
    {
        bool Names(string text) => identifiers.Any(identifier => text.Contains(identifier, StringComparison.OrdinalIgnoreCase));
        switch (node)
        {
            case JsonObject members:
                foreach (var (key, value) in members)
                {
                    if (Names(key))
                    {
                        found.Add((pointer + "/" + key, "(key)"));
                    }

                    Walk(value, pointer + "/" + key, identifiers, found);
                }

                break;
            case JsonArray items:
                for (var index = 0; index < items.Count; index++)
                {
                    Walk(items[index], pointer + "/" + index, identifiers, found);
                }

                break;
            case JsonValue value when value.TryGetValue<string>(out var text) && Names(text):
                found.Add((pointer, text));
                break;
        }
    }

    /// <summary>
    /// The payload without the retained keys, which ADR-0108 § 6 keeps because a row's <c>entity_id</c> shows them: a
    /// User's or Membership's top-level <c>Id</c> where it is exactly the row's <c>entity_id</c>, and in <c>changes</c>
    /// the instance segment of every User or Membership path with the before and after of that instance's <c>Id</c> item.
    /// A row that carries a second Membership of the person (the person was admitted again) names it only by that
    /// segment; it is the same pseudonymous key, retained the same way. Everything else is walked.
    /// </summary>
    private static JsonNode? WithoutOwnKey(JsonNode? node, string type, string? ownKey)
    {
        bool Own(JsonNode? value) => ownKey is not null && value is JsonValue text && text.TryGetValue<string>(out var spelled)
            && string.Equals(spelled, ownKey, StringComparison.OrdinalIgnoreCase);
        switch (node)
        {
            case JsonObject state when Own(state["Id"]):
                state.Remove("Id");
                break;
            case JsonArray items:
                foreach (var item in items.OfType<JsonObject>())
                {
                    if (item["path"] is not JsonValue path || !path.TryGetValue<string>(out var spelled)
                        || InstanceSegment().Match(spelled) is not { Success: true } match)
                    {
                        continue;
                    }

                    var (entity, instance, property) = (match.Groups["type"].Value, match.Groups["instance"].Value, match.Groups["rest"].Value);
                    item["path"] = "/" + entity + "/" + property;
                    if (property == "Id")
                    {
                        foreach (var side in Sides.Where(side => item[side] is JsonValue value && value.TryGetValue<string>(out var spelledValue)
                            && string.Equals(spelledValue, instance, StringComparison.OrdinalIgnoreCase)))
                        {
                            item.Remove(side);
                        }
                    }
                }

                break;
        }

        return node;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^/(?<type>User|Membership)/(?<instance>[0-9a-fA-F-]{36})/(?<rest>.*)$")]
    private static partial System.Text.RegularExpressions.Regex InstanceSegment();
}
