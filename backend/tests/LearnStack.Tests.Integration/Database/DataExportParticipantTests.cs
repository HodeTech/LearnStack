using System.Text.Json;
using FluentAssertions;
using LearnStack.Modules.Audit.Infrastructure.DataRequests;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0107 § 3 and ADR-0108 § 4 against the database: the audit participant's subject predicate and who-asked
/// reach, as <c>learnstack_app</c> in an organization group of the seeded schema. Every case rolls back.
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class DataExportParticipantTests(SchemaFixture schema)
{
    private static readonly string[] SelfReach = ["own_tenant_wide", "own_branch", "admin_on_membership", "about_the_user"];
    private static readonly string[] AdministratorReach = ["own_branch", "admin_on_membership"];

    [Theory]
    [InlineData("self")]
    [InlineData("administrator")]
    public async Task The_audit_section_selects_the_subjects_rows_and_its_reach_follows_who_asked(string origin)
    {
        await using var scope = await DataExportTestKit.OpenAsync(schema.Postgres.AppConnectionString, SchemaFixture.TenantA, SchemaFixture.OrgA1);
        var foreignMembership = Guid.CreateVersion7();
        var rows = new (string Name, Guid? Organization, Guid Actor, string EntityType, string Entity)[]
        {
            ("own_tenant_wide", null, PersonA, "Invitation", Guid.CreateVersion7().ToString("D")),
            ("own_branch", SchemaFixture.OrgA1, PersonA, "Invitation", Guid.CreateVersion7().ToString("D")),
            ("admin_on_membership", SchemaFixture.OrgA1, AdminA, "Membership", BranchMembershipA1.ToString("D")),
            ("admin_on_other_membership", SchemaFixture.OrgA1, AdminA, "Membership", foreignMembership.ToString("D")),
            ("about_the_user", null, UserId.SystemActor.Value, "User", PersonA.ToString("D")),
            ("other_person", SchemaFixture.OrgA1, PersonB, "Invitation", Guid.CreateVersion7().ToString("D")),
        };
        foreach (var row in rows)
        {
            await ScopeAsync(scope.Connection, scope.Transaction, SchemaFixture.TenantA, row.Organization);
            await ExecuteAsync(scope.Connection, scope.Transaction, """
                INSERT INTO public.audit_log(id,timestamp,tenant_id,organization_id,actor_user_id,module,operation,operation_type,
                    operation_class,entity_type,entity_id,outcome,reason,before_state,after_state,changes,ip_address,user_agent)
                VALUES(@id,clock_timestamp(),@tenant,@organization,@actor,'identity',@operation,'SecurityEvent','Must',@type,@entity,
                    'success',NULL,@before::jsonb,NULL,NULL,'198.51.100.4'::inet,'Synthetic/2.0')
                """, ("id", Guid.CreateVersion7()), ("tenant", SchemaFixture.TenantA), ("organization", (object?)row.Organization ?? DBNull.Value),
                ("actor", row.Actor), ("operation", "fixture." + row.Name), ("type", row.EntityType), ("entity", row.Entity),
                ("before", $$"""{"UserId":"{{PersonA}}","UpdatedBy":"{{AdminA}}"}"""));
        }

        await ScopeAsync(scope.Connection, scope.Transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
        var participant = new AuditUserDataParticipant(scope.Unit, scope.Context);
        var subject = new UserDataSubject(Guid.CreateVersion7(), DataRequestKind.Export,
            origin == "self" ? DataRequestOrigin.Self : DataRequestOrigin.Administrator, UserId.From(PersonA),
            new DataRequestScopeGroup(TenantId.From(SchemaFixture.TenantA), OrganizationId.From(SchemaFixture.OrgA1)), [BranchMembershipA1]);
        var writer = new CollectingWriter();

        await participant.ExportAsync(subject, writer, CancellationToken.None);

        var exported = writer.Records.Select(record => JsonDocument.Parse(record).RootElement)
            .Where(record => record.GetProperty("operation").GetString()!.StartsWith("fixture.", StringComparison.Ordinal))
            .ToDictionary(record => record.GetProperty("operation").GetString()!["fixture.".Length..]);
        exported.Keys.Should().BeEquivalentTo(origin == "self" ? SelfReach : AdministratorReach,
            "a self request reaches every visible row of the tenant, an administrator's only the group's organization");
        exported["own_branch"].GetProperty("actor").GetString().Should().Be("self");
        exported["own_branch"].GetProperty("ip_address").GetString().Should().Be("198.51.100.4");
        exported["own_branch"].TryGetProperty("payload", out _).Should().BeFalse("an invitation row is not the subject's own entity");
        var administered = exported["admin_on_membership"];
        administered.GetProperty("actor").GetString().Should().Be("third_party");
        administered.TryGetProperty("ip_address", out _).Should().BeFalse("the administrator's address is another person's data");
        administered.GetProperty("payload").GetProperty("before").GetProperty("UserId").GetString().Should().Be("self");
        administered.GetProperty("payload").GetProperty("before").GetProperty("UpdatedBy").GetString().Should().Be("third_party");
        writer.Records.Should().OnlyContain(record => !record.Contains(PersonB.ToString("D"), StringComparison.OrdinalIgnoreCase)
            && !record.Contains(AdminA.ToString("D"), StringComparison.OrdinalIgnoreCase));

        var retained = (await participant.DescribeRetainedAsync(subject, CancellationToken.None)).ToDictionary(item => item.Kind, item => item.Count);
        retained[RetainedReferenceKind.AuditActorReference].Should().BeGreaterThanOrEqualTo(origin == "self" ? 2 : 1);
        retained[RetainedReferenceKind.AuditEntityReference].Should().BeGreaterThanOrEqualTo(origin == "self" ? 2 : 1);
        retained.Should().ContainKey(RetainedReferenceKind.AuditMetadataReference);
        await scope.Frame.FailAsync();
    }

    [Fact]
    public async Task A_capped_audit_section_declares_the_records_included_and_the_total()
    {
        await using var scope = await DataExportTestKit.OpenAsync(schema.Postgres.AppConnectionString, SchemaFixture.TenantA, null);
        for (var index = 0; index < 5; index++)
        {
            await ExecuteAsync(scope.Connection, scope.Transaction, """
                INSERT INTO public.audit_log(id,timestamp,tenant_id,organization_id,actor_user_id,module,operation,operation_type,
                    operation_class,entity_type,entity_id,outcome)
                VALUES(@id,clock_timestamp(),@tenant,NULL,@actor,'identity','fixture.capped','SecurityEvent','Must','Invitation',NULL,'success')
                """, ("id", Guid.CreateVersion7()), ("tenant", SchemaFixture.TenantA), ("actor", PersonA));
        }

        var participant = new AuditUserDataParticipant(scope.Unit, scope.Context);
        participant.ExportSectionCapBytes.Should().Be(DataRequestLimits.AuditSectionBytes);
        var writer = new CollectingWriter(admit: 2);

        await participant.ExportAsync(new UserDataSubject(Guid.CreateVersion7(), DataRequestKind.Export, DataRequestOrigin.Self,
            UserId.From(PersonA), new DataRequestScopeGroup(TenantId.From(SchemaFixture.TenantA), null), [LearnerMembershipA]),
            writer, CancellationToken.None);

        writer.Records.Should().HaveCount(2);
        writer.Truncated.Should().ContainSingle();
        writer.Truncated[0].Collection.Should().Be("entries");
        writer.Truncated[0].Included.Should().Be(2);
        writer.Truncated[0].Total.Should().BeGreaterThanOrEqualTo(5);
        await scope.Frame.FailAsync();
    }

    /// <summary>Collects records, and reports the section cap after <c>admit</c> of them.</summary>
    private sealed class CollectingWriter(int admit = int.MaxValue) : IUserDataExportWriter
    {
        public List<string> Records { get; } = [];

        public List<(string Collection, long Included, long Total)> Truncated { get; } = [];

        public long RemainingBytes => long.MaxValue;

        public ValueTask<UserDataExportAppend> AppendAsync(string collection, ReadOnlyMemory<byte> utf8JsonRecord,
            CancellationToken cancellationToken)
        {
            if (Records.Count >= admit)
            {
                return ValueTask.FromResult(UserDataExportAppend.SectionCapReached);
            }

            Records.Add(System.Text.Encoding.UTF8.GetString(utf8JsonRecord.Span));
            return ValueTask.FromResult(UserDataExportAppend.Appended);
        }

        public void DeclareTruncated(string collection, long includedRecords, long totalRecords) =>
            Truncated.Add((collection, includedRecords, totalRecords));
    }
}
