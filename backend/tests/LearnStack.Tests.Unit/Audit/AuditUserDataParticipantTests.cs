using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Modules.Audit.Infrastructure.DataRequests;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using Xunit;

namespace LearnStack.Tests.Unit.Audit;

/// <summary>
/// ADR-0107 § 3, ADR-0106 § 5: what an exported audit record discloses — the envelope, the actor as a closed word,
/// the subject's own address and agent only, the payload only of the subject's own Membership or consent — and that
/// every identifier is rewritten to <c>self</c> or <c>third_party</c>.
/// </summary>
public sealed partial class AuditUserDataParticipantTests
{
    private static readonly Guid Subject = Guid.Parse("01970000-0000-7000-8000-00000000000a");
    private static readonly Guid OwnMembership = Guid.Parse("01970000-0000-7000-8000-00000000000b");
    private static readonly Guid OtherPerson = Guid.Parse("01970000-0000-7000-8000-00000000000c");
    private static readonly Guid OtherMembership = Guid.Parse("01970000-0000-7000-8000-00000000000d");

    [Theory]
    [InlineData("subject", "self")]
    [InlineData("system", "system")]
    [InlineData("other", "third_party")]
    [InlineData("none", null)]
    public void The_actor_is_a_closed_word_never_an_identifier(string actor, string? expected) =>
        AuditUserDataParticipant.ActorOf(Row(actor: Actor(actor)), Request()).Should().Be(expected);

    [Theory]
    [InlineData("Membership", "own", "other", true)]
    [InlineData("Membership", "other_membership", "subject", false)]
    [InlineData("MembershipFieldConsent", "any", "subject", true)]
    [InlineData("MembershipFieldConsent", "any", "other", false)]
    [InlineData("Invitation", "any", "subject", false)]
    [InlineData("Role", "any", "subject", false)]
    [InlineData("User", "subject", "system", false)]
    public void The_payload_is_disclosed_only_for_the_subjects_own_membership_or_recorded_consent(string entityType, string entity,
        string actor, bool disclosed)
    {
        var entityId = entity switch
        {
            "own" => OwnMembership.ToString("D"),
            "other_membership" => OtherMembership.ToString("D"),
            "subject" => Subject.ToString("D"),
            _ => Guid.CreateVersion7().ToString("D"),
        };

        AuditUserDataParticipant.DisclosesPayload(Row(entityType: entityType, entityId: entityId, actor: Actor(actor)), Request())
            .Should().Be(disclosed);
    }

    [Fact]
    public void An_administrators_change_to_the_subjects_membership_shows_the_payload_but_never_the_administrators_address()
    {
        var row = Row(entityType: "Membership", entityId: OwnMembership.ToString("D"), actor: OtherPerson,
            before: $$"""{"Id":"{{OwnMembership}}","UserId":"{{Subject}}","Status":"Active","UpdatedBy":"{{OtherPerson}}"}""",
            after: $$"""{"Id":"{{OwnMembership}}","UserId":"{{Subject}}","Status":"Suspended","UpdatedBy":"{{OtherPerson}}","RoleIds":["{{OtherMembership}}"]}""",
            changes: $$"""[{"path":"/Membership/{{OwnMembership}}/Status","before":"Active","after":"Suspended"}]""");

        var json = Write(row);

        var record = JsonDocument.Parse(json).RootElement;
        record.GetProperty("actor").GetString().Should().Be("third_party");
        record.TryGetProperty("ip_address", out _).Should().BeFalse();
        record.TryGetProperty("user_agent", out _).Should().BeFalse();
        var payload = record.GetProperty("payload");
        payload.GetProperty("before").GetProperty("UserId").GetString().Should().Be("self");
        payload.GetProperty("before").GetProperty("Id").GetString().Should().Be("self");
        payload.GetProperty("after").GetProperty("UpdatedBy").GetString().Should().Be("third_party");
        payload.GetProperty("after").GetProperty("RoleIds")[0].GetString().Should().Be("third_party");
        payload.GetProperty("changes")[0].GetProperty("path").GetString().Should().Be("/Membership/self/Status");
        AnyIdentifier().IsMatch(json).Should().BeFalse("no identifier, the subject's own included, leaves in an export");
    }

    [Fact]
    public void A_row_the_subject_acted_in_carries_their_own_address_and_agent_and_no_payload_for_a_foreign_entity()
    {
        var row = Row(entityType: "Invitation", entityId: Guid.CreateVersion7().ToString("D"), actor: Subject,
            ip: "203.0.113.7", agent: "Synthetic/1.0", before: $$"""{"RecipientEmail":"***REDACTED***","InviterUserId":"{{Subject}}"}""");

        var record = JsonDocument.Parse(Write(row)).RootElement;

        record.EnumerateObject().Select(property => property.Name).Should()
            .Equal("at", "module", "operation", "outcome", "entity_type", "actor", "ip_address", "user_agent");
        record.GetProperty("actor").GetString().Should().Be("self");
        record.GetProperty("ip_address").GetString().Should().Be("203.0.113.7");
        record.GetProperty("user_agent").GetString().Should().Be("Synthetic/1.0");
        record.GetProperty("module").GetString().Should().Be("identity");
        record.GetProperty("operation").GetString().Should().Be("identity.invitation.create");
        record.GetProperty("outcome").GetString().Should().Be("success");
    }

    [Fact]
    public void Identifiers_in_property_names_and_any_case_are_rewritten_and_the_redaction_sentinel_is_kept()
    {
        var own = AuditUserDataParticipant.Own(Request());
        var upper = OtherPerson.ToString("D").ToUpperInvariant();

        AuditPayloadProjection.Rewrite($"x-{Subject}-{upper}", own).Should().Be("x-self-third_party");
        AuditPayloadProjection.Rewrite("***REDACTED***", own).Should().Be("***REDACTED***");
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            AuditPayloadProjection.Write(json, "{\"" + OtherMembership + "\":{\"n\":1,\"b\":true,\"z\":null}}", own);
        }

        JsonDocument.Parse(stream.ToArray()).RootElement.GetProperty("third_party").GetProperty("n").GetInt32().Should().Be(1);
    }

    [Fact]
    public void Rewritten_property_names_stay_distinct_deterministically_within_one_object()
    {
        var own = AuditUserDataParticipant.Own(Request());
        var payload = "{\"" + OtherPerson + "\":1,\"" + Subject + "\":2,\"" + OtherMembership + "\":3,\"third_party_2\":4,\""
            + Subject.ToString("D").ToUpperInvariant() + "\":5,\"nested\":{\"" + OtherPerson + "\":6,\"" + OtherMembership + "\":7}}";

        string Project()
        {
            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream))
            {
                AuditPayloadProjection.Write(json, payload, own);
            }

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }

        var projected = Project();

        var root = JsonDocument.Parse(projected).RootElement;
        root.EnumerateObject().Select(property => property.Name).Should()
            .Equal("third_party", "self", "third_party_2", "third_party_2_2", "self_2", "nested");
        root.EnumerateObject().Select(property => property.Value.ValueKind == JsonValueKind.Number ? property.Value.GetInt32() : 0)
            .Should().Equal([1, 2, 3, 4, 5, 0], "every value is kept under its own name");
        root.GetProperty("nested").EnumerateObject().Select(property => property.Name).Should().Equal(["third_party", "third_party_2"],
            "each object numbers its own names");
        Project().Should().Be(projected, "the numbering follows document order alone");
        AnyIdentifier().IsMatch(projected).Should().BeFalse();
    }

    private static string Write(AuditUserDataParticipant.AuditRow row)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            AuditUserDataParticipant.WriteRecord(json, row, Request(), AuditUserDataParticipant.Own(Request()));
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static UserDataSubject Request() => new(Guid.CreateVersion7(), DataRequestKind.Export, DataRequestOrigin.Self,
        UserId.From(Subject), new DataRequestScopeGroup(TenantId.From(Guid.CreateVersion7()), null), [OwnMembership]);

    private static Guid? Actor(string actor) => actor switch
    {
        "subject" => Subject,
        "system" => UserId.SystemActor.Value,
        "other" => OtherPerson,
        _ => null,
    };

    private static AuditUserDataParticipant.AuditRow Row(string entityType = "Membership", string? entityId = null, Guid? actor = null,
        string? ip = null, string? agent = null, string? before = null, string? after = null, string? changes = null) =>
        new(Guid.CreateVersion7(), new DateTime(2026, 11, 22, 9, 0, 0, DateTimeKind.Utc), "identity",
            entityType == "Invitation" ? "identity.invitation.create" : "identity.membership.change_status", "success",
            entityType, entityId, actor, ip, agent, before, after, changes);

    [GeneratedRegex("[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}")]
    private static partial Regex AnyIdentifier();
}
