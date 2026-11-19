using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Audit;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The two witness-bound audit methods of the controlled issuer rebind (ADR-0104 § 8) against real PostgreSQL,
/// written on the rebind role's own connection exactly as the offline tool writes them.
/// </summary>
[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class IdentityRebindAuditStoreTests(PublicReadFixture parent)
{
    private static readonly PlatformOperation Rebind = PlatformOperations.IdentityAuthorityRebind;

    [Fact]
    public async Task The_Intent_And_The_Result_Rows_Are_Written_Bound_To_The_Admission_And_Carry_The_Ticket()
    {
        await using var scope = RebindProofSupport.Store();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var admission = Admit(ticket: "CHG-104");
        var authority = Guid.CreateVersion7();
        var intent = Intent(admission, authority);
        var result = Result(admission, authority);

        await using (var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(parent.Postgres.IdentityRebindConnectionString))
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await audit.WriteIdentityRebindIntentAsync(intent, admission, connection, transaction);
            await transaction.CommitAsync();
        }

        await using (var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(parent.Postgres.IdentityRebindConnectionString))
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await audit.WriteIdentityRebindResultAsync(result, admission, connection, transaction);
            await transaction.CommitAsync();
        }

        var rows = await ReadAsync(admission.Operator.CorrelationId!);
        rows.Select(row => (row.Operation, row.Outcome)).Should().BeEquivalentTo(
            [("identity.authority.rebind_attempt", "indeterminate"), ("identity.authority.rebind", "success")]);
        rows.Should().OnlyContain(row => row.Tenant == TenantId.PlatformSentinel.Value && row.Actor == null
            && row.Reason == "issuer_rebind" && row.Module == "identity" && row.Entity == "IdentityAuthority"
            && OperatorNode(row.Metadata).GetProperty("ticket").GetString() == "CHG-104"
            && OperatorNode(row.Metadata).GetProperty("subjectDigest").GetString() == admission.Operator.SubjectDigest);
    }

    [Fact]
    public async Task The_Result_Row_Commits_With_The_Transaction_Or_Not_At_All()
    {
        await using var scope = RebindProofSupport.Store();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var admission = Admit();

        await using (var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(parent.Postgres.IdentityRebindConnectionString))
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await audit.WriteIdentityRebindResultAsync(Result(admission, Guid.CreateVersion7()), admission, connection, transaction);
            await transaction.RollbackAsync();
        }

        (await ReadAsync(admission.Operator.CorrelationId!)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_Row_That_Differs_From_Its_Admission_Or_Its_Declaration_Is_Refused_Before_The_Database()
    {
        await using var scope = RebindProofSupport.Store();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var admission = Admit(ticket: "CHG-104");
        var authority = Guid.CreateVersion7();
        var good = Result(admission, authority);
        var digest = admission.Operator.SubjectDigest;
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(parent.Postgres.IdentityRebindConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();

        var refused = new Dictionary<string, AuditEntryDraft>
        {
            ["a real tenant"] = good with { TenantId = TenantId.From(Guid.CreateVersion7()) },
            ["the nil tenant"] = good with { TenantId = TenantId.From(Guid.Empty) },
            ["an organization"] = good with { OrganizationId = OrganizationId.From(Guid.CreateVersion7()) },
            ["another module"] = good with { ModuleName = "tenancy" },
            ["another operation"] = good with { Operation = "identity.user.create" },
            ["the intent slug for a result"] = good with { Operation = "identity.authority.rebind_attempt" },
            ["a failed outcome"] = good with { Outcome = AuditOutcome.Failed },
            ["the intent outcome for a result"] = good with { Outcome = AuditOutcome.Indeterminate },
            ["a SHOULD class"] = good with { OperationClass = OperationClass.Should },
            ["another operation type"] = good with { OperationType = OperationType.Create },
            ["another entity"] = good with { EntityType = "User" },
            ["another reason"] = good with { Reason = "host_map" },
            ["an actor"] = good with { ActorUserId = UserId.From(Guid.CreateVersion7()) },
            ["a mail address"] = good with { ActorEmail = "operator@example.test" },
            ["an address"] = good with { IpAddress = System.Net.IPAddress.Parse("203.0.113.7") },
            ["a user agent"] = good with { UserAgent = "tool" },
            ["another correlation id"] = good with { CorrelationId = "another" },
            ["no metadata"] = good with { Metadata = null },
            ["no operator attribution"] = good with { Metadata = "{}" },
            ["the digest in the wrong place"] = good with
            {
                Metadata = "{\"note\":\"" + digest + "\",\"operator\":{\"issuer\":\"" + admission.Operator.Issuer + "\"}}",
            },
            ["another operator issuer"] = good with { Metadata = Metadata(admission, "https://elsewhere.example.test/realms/x", "CHG-104") },
            ["a ticket the admission never held"] = good with { Metadata = Metadata(admission, admission.Operator.Issuer, "CHG-999") },
        };

        foreach (var (attribute, draft) in refused)
        {
            var act = async () => await audit.WriteIdentityRebindResultAsync(draft, admission, connection, transaction);
            (await act.Should().ThrowAsync<ArgumentException>(attribute)).Which.Message.Should().Contain("admission");
        }

        // The intent method refuses the result's slug and outcome in turn.
        var intentOfResult = async () => await audit.WriteIdentityRebindIntentAsync(good, admission, connection, transaction);
        await intentOfResult.Should().ThrowAsync<ArgumentException>("the result slug is not the attempt slug");

        // Another operation's admission binds no rebind row, and the other operations have no attempt slug.
        var killswitch = TestPlatformAdmissions.Admission(PlatformOperations.KillswitchToggle, correlationId: admission.Operator.CorrelationId);
        var other = async () => await audit.WriteIdentityRebindResultAsync(good, killswitch, connection, transaction);
        await other.Should().ThrowAsync<ArgumentException>("another operation's admission");
        var noAttempt = async () => await audit.WriteIdentityRebindIntentAsync(
            Intent(killswitch, authority), killswitch, connection, transaction);
        await noAttempt.Should().ThrowAsync<ArgumentException>("only the rebind operation has an attempt slug");
    }

    [Fact]
    public async Task A_Database_Refusal_Is_An_Audit_Write_Failure_So_The_Rebind_Cannot_Commit()
    {
        await using var scope = RebindProofSupport.Store();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var admission = Admit();

        // The application role announces no tenant and the policy refuses the row.
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(parent.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();

        var act = async () => await audit.WriteIdentityRebindResultAsync(
            Result(admission, Guid.CreateVersion7()), admission, connection, transaction);

        await act.Should().ThrowAsync<AuditWriteFailedException>();
    }

    [Fact]
    public async Task The_Identity_Catalogue_Declares_Both_Slugs_Off_Path_As_Must_Security_Events()
    {
        var catalog = new AuditCatalog([new IdentityAuditCatalogSource()]);

        foreach (var slug in new[] { "identity.authority.rebind_attempt", "identity.authority.rebind" })
        {
            catalog.TryGetOffPath(slug, out var declared).Should().BeTrue(slug);
            declared.ModuleName.Should().Be("identity");
            declared.OperationClass.Should().Be(OperationClass.Must);
            declared.OperationType.Should().Be(OperationType.SecurityEvent);
            declared.EntityType!.Name.Should().Be("IdentityAuthority");
        }

        Rebind.AuditSlug.Should().Be("identity.authority.rebind");
        Rebind.AttemptAuditSlug.Should().Be("identity.authority.rebind_attempt");
        Rebind.ReasonSlug.Should().Be("issuer_rebind");
        Rebind.NeedsPlatformRole.Should().BeFalse("the tool uses its own role and never the platform scope");
        Rebind.Permission.Key.Should().Be("platform.identity_authority.admin");
    }

    private static System.Text.Json.JsonElement OperatorNode(string metadata) =>
        System.Text.Json.JsonDocument.Parse(metadata).RootElement.GetProperty("operator");

    private static PlatformAdmission Admit(string? ticket = null) =>
        TestPlatformAdmissions.Admission(Rebind, ticket, correlationId: "00-rebind-" + Guid.NewGuid().ToString("N"));

    private static string Metadata(PlatformAdmission admission, string issuer, string? ticket) =>
        "{\"operator\":{\"issuer\":\"" + issuer + "\",\"subjectDigest\":\"" + admission.Operator.SubjectDigest
        + "\",\"kind\":\"human\"" + (ticket is null ? string.Empty : ",\"ticket\":\"" + ticket + "\"") + "}}";

    private static AuditEntryDraft Intent(PlatformAdmission admission, Guid authority) =>
        Draft(admission, authority, admission.Operation.AttemptAuditSlug ?? "identity.authority.rebind_attempt", AuditOutcome.Indeterminate);

    private static AuditEntryDraft Result(PlatformAdmission admission, Guid authority) =>
        Draft(admission, authority, admission.Operation.AuditSlug, AuditOutcome.Success);

    private static AuditEntryDraft Draft(PlatformAdmission admission, Guid authority, string operation, AuditOutcome outcome) => new()
    {
        Id = AuditEntryId.From(Guid.CreateVersion7()),
        TenantId = TenantId.PlatformSentinel,
        OrganizationId = null,
        ActorUserId = null,
        ActorEmail = null,
        ModuleName = "identity",
        Operation = operation,
        OperationType = OperationType.SecurityEvent,
        OperationClass = OperationClass.Must,
        EntityType = "IdentityAuthority",
        EntityId = authority.ToString("D"),
        Outcome = outcome,
        ErrorKey = null,
        Reason = Rebind.ReasonSlug,
        BeforeState = null,
        AfterState = null,
        Changes = null,
        CorrelationId = admission.Operator.CorrelationId,
        IpAddress = null,
        UserAgent = null,
        Timestamp = DateTimeOffset.UtcNow,
        Metadata = "{\"operator\":" + PlatformOperatorAttribution.From(admission).ToOperatorJson(admission.Ticket) + "}",
    };

    private sealed record Row(Guid Tenant, string Operation, string Outcome, Guid? Actor, string? Reason, string Module, string Entity, string Metadata);

    private async Task<List<Row>> ReadAsync(string correlationId)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(parent.Postgres.PlatformConnectionString);
        await using var command = new NpgsqlCommand(
            """
            SELECT tenant_id, operation, outcome, actor_user_id, reason, module, entity_type, metadata::text
            FROM public.audit_log WHERE correlation_id = @correlation ORDER BY timestamp, operation
            """, connection);
        command.Parameters.AddWithValue("correlation", correlationId);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<Row>();
        while (await reader.ReadAsync())
        {
            rows.Add(new Row(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5), reader.GetString(6), reader.GetString(7)));
        }

        return rows;
    }
}
