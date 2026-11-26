using System.Net;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.PlatformOperator;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Platform;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>
/// The closure's Finalize route (ADR-0107 §§ 7 and 8, ADR-0108 § 8): the admission it runs under, the answers it maps (the
/// finalization's closed counts and unserved groups, the refusals on the field they concern, the provider's closed outcome
/// on a retryable 503), the key that admits it, its counter and its document.
/// </summary>
public sealed partial class PlatformOperatorHttpTests
{
    private const string FinalizePath = DataRequests + "/" + ClosureRequest + "/finalize-closure";
    private const string UnservedTenant = "019a1929-5450-7bbb-8df1-516e8e0025a1";
    private const string UnservedOrganization = "019a1929-5450-7bbb-8df1-516e8e0025a2";

    [Fact]
    public async Task A_Finalize_Runs_Under_An_Admission_For_Exactly_That_Operation_And_Answers_Closed_Counts_And_The_Unserved_Groups()
    {
        var response = await Call(HttpMethod.Post, FinalizePath, Valid(), """{"ticket":"CHG-31"}""");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var (admission, requestId) = _closures.FinalizeCalls.Should().ContainSingle().Which;
        _closures.Calls.Should().BeEmpty("a Finalize is not a Begin");
        requestId.Should().Be(Guid.Parse(ClosureRequest));
        admission.Operation.Should().BeSameAs(PlatformOperations.ClosureFinalize);
        admission.Ticket.Should().Be("CHG-31");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        root.GetProperty("outcome").GetString().Should().Be("finalized");
        root.GetProperty("requestId").GetString().Should().Be(ClosureRequest);
        root.GetProperty("status").GetString().Should().Be("completed");
        root.GetProperty("groups").ToString().Should().Be(
            """{"served":3,"ready":3,"pending":0,"unserved":1,"erasuresCompleted":3,"closuresCompleted":2}""");
        root.GetProperty("unservedGroups").ToString().Should().Be(
            $$"""[{"tenantId":"{{UnservedTenant}}","organizationId":"{{UnservedOrganization}}"}]""");
        var redaction = root.GetProperty("redaction");
        redaction.GetProperty("tenants").GetInt32().Should().Be(2);
        redaction.GetProperty("rowsSelected").GetInt64().Should().Be(9);
        redaction.GetProperty("redacted").GetProperty("ip_address").GetInt64().Should().Be(5);
        redaction.GetProperty("retained").GetProperty("audit_metadata_reference").GetInt64().Should().Be(3);
        root.GetProperty("retained").ToString().Should().Be("""{"audit_actor_reference":4,"unserved_group":1}""");
        root.GetProperty("correlationId").GetString().Should().Be(admission.Operator.CorrelationId);
        root.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            ["outcome", "requestId", "status", "groups", "unservedGroups", "redaction", "retained", "correlationId"],
            "the answer names no person: tenants and organizations of unserved groups, and counts");
    }

    [Fact]
    public async Task A_Repeat_After_The_Finalize_Landed_Answers_What_It_Recorded_And_No_Counts_Of_Its_Own()
    {
        _closures.FinalizeResult = new ClosureFinalizeResult(
            ClosureFinalizeOutcome.AlreadyFinalized, "completed",
            Retained: new Dictionary<string, long>(StringComparer.Ordinal) { ["audit_actor_reference"] = 4, ["unserved_group"] = 0 });

        var response = await Call(HttpMethod.Post, FinalizePath, Valid(), "{}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        root.GetProperty("outcome").GetString().Should().Be("already_finalized");
        root.GetProperty("status").GetString().Should().Be("completed");
        root.GetProperty("groups").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("redaction").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("unservedGroups").GetArrayLength().Should().Be(0);
        root.GetProperty("retained").ToString().Should().Be("""{"audit_actor_reference":4,"unserved_group":0}""");
    }

    [Theory]
    [InlineData(ClosureFinalizeOutcome.NotClosure, "kind", "lockey_data_request_not_closure", null)]
    [InlineData(ClosureFinalizeOutcome.NotFinalizable, "status", "lockey_data_request_closure_not_finalizable", "scheduled")]
    [InlineData(ClosureFinalizeOutcome.NotFinalizable, "status", "lockey_data_request_closure_not_finalizable", "cancelled")]
    public async Task A_Refused_Finalize_Is_A_409_With_Its_Reason_On_The_Field_It_Concerns(
        ClosureFinalizeOutcome outcome, string field, string reason, string? cause)
    {
        _closures.FinalizeResult = new ClosureFinalizeResult(outcome, cause ?? "scheduled");

        var response = await Call(HttpMethod.Post, FinalizePath, Valid(), "{}");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("business_rule_violation");
        var errors = body.RootElement.GetProperty("errors");
        errors.EnumerateObject().Select(property => property.Name).Should().Equal([field]);
        errors.GetProperty(field)[0].GetProperty("key").GetString().Should().Be(reason);
        if (cause is not null)
        {
            errors.GetProperty(field)[0].GetProperty("params").GetProperty("cause").GetString().Should().Be(cause);
        }
    }

    [Fact]
    public async Task A_Finalize_Before_Every_Served_Group_Is_Ready_Is_A_409_With_Closed_Tallies_And_No_Group_Named()
    {
        _closures.FinalizeResult = new ClosureFinalizeResult(
            ClosureFinalizeOutcome.GroupsPending, "fulfilling", new ClosureFinalizeTally(Served: 3, Ready: 1, Pending: 2, Unserved: 1));

        var response = await Call(HttpMethod.Post, FinalizePath, Valid(), "{}");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var text = await response.Content.ReadAsStringAsync();
        text.Should().NotContain(UnservedTenant);
        using var body = JsonDocument.Parse(text);
        var groups = body.RootElement.GetProperty("errors").GetProperty("groups")[0];
        groups.GetProperty("key").GetString().Should().Be("lockey_data_request_closure_groups_pending");
        groups.GetProperty("params").ToString().Should().Be("""{"served":"3","ready":"1","pending":"2","unserved":"1"}""");
    }

    [Fact]
    public async Task A_Groups_Refusal_Found_After_The_Provider_Step_Says_The_Account_Is_Already_Gone()
    {
        // The second transaction found a group pending after the provider deleted the account: the 409 carries the closed
        // provider answer, so the operator does not read it as "nothing changed" and a repeat finishes the closure.
        foreach (var provider in new[] { "removed", "absent" })
        {
            _closures.FinalizeResult = new ClosureFinalizeResult(
                ClosureFinalizeOutcome.GroupsPending, "fulfilling", new ClosureFinalizeTally(Served: 3, Ready: 2, Pending: 1, Unserved: 0),
                Provider: provider);

            var response = await Call(HttpMethod.Post, FinalizePath, Valid(), "{}");

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("errors").GetProperty("groups")[0].GetProperty("params").ToString().Should().Be(
                "{\"served\":\"3\",\"ready\":\"2\",\"pending\":\"1\",\"unserved\":\"0\",\"provider\":\"" + provider + "\"}");
        }
    }

    [Fact]
    public async Task A_Finalize_That_Names_No_Request_Is_Not_Found_And_Naming_No_Real_Request_Never_Calls_The_Owner()
    {
        _closures.FinalizeResult = new ClosureFinalizeResult(ClosureFinalizeOutcome.NotFound);
        var response = await Call(HttpMethod.Post, FinalizePath, Valid(), "{}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await AssertProblem(response, "not_found");
        _closures.FinalizeCalls.Clear();

        foreach (var (suffix, expected) in new[]
                 {
                     ("/data-requests/not-a-guid/finalize-closure", HttpStatusCode.NotFound),
                     ("/data-requests/00000000-0000-0000-0000-000000000000/finalize-closure", HttpStatusCode.BadRequest),
                     ("/data-requests/00000000-0000-7000-8000-000000000002/finalize-closure", HttpStatusCode.BadRequest),
                     ("/data-requests/4f9c5a3e-2b7d-4c1a-9e8f-0a1b2c3d4e5f/finalize-closure", HttpStatusCode.BadRequest),
                 })
        {
            (await Call(HttpMethod.Post, "/api/platform/v1" + suffix, Valid(), "{}")).StatusCode.Should().Be(expected, suffix);
        }

        (await Call(HttpMethod.Post, FinalizePath, Valid())).StatusCode.Should().Be(HttpStatusCode.BadRequest, "no body");
        (await Call(HttpMethod.Post, FinalizePath, Valid(), """{"ticket":"has space"}""")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Call(HttpMethod.Post, FinalizePath, Sign(("sub", "someone-else")), "not json")).StatusCode.Should().Be(
            HttpStatusCode.Forbidden, "an operator without the grant learns nothing about the body");
        _closures.FinalizeCalls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ClosureFinalizeOutcome.ProviderRefused, "lockey_provider_refused")]
    [InlineData(ClosureFinalizeOutcome.ProviderUnavailable, "lockey_provider_unavailable")]
    [InlineData(ClosureFinalizeOutcome.ProviderUncertain, "lockey_provider_outcome_unknown")]
    public async Task A_Finalize_The_Provider_Did_Not_Finish_Is_A_Retryable_503_With_The_Provider_s_Closed_Outcome(
        ClosureFinalizeOutcome outcome, string reason)
    {
        _closures.FinalizeResult = new ClosureFinalizeResult(outcome, "fulfilling", Redaction: Aggregate());

        var response = await Call(HttpMethod.Post, FinalizePath, Valid(), "{}");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.Should().NotBeNull();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("dependency_unavailable");
        var errors = body.RootElement.GetProperty("errors");
        errors.EnumerateObject().Select(property => property.Name).Should().Equal(["provider"]);
        errors.GetProperty("provider")[0].GetProperty("key").GetString().Should().Be(reason);
    }

    [Fact]
    public async Task A_Finalize_Whose_Audit_Or_Fences_Failed_Or_Whose_Commit_Is_Unknown_Is_A_Retryable_503_With_Its_Own_Reason()
    {
        foreach (var (failure, reason) in new (Exception, string)[]
                 {
                     (new AuditWriteFailedException("forced"), "audit_unavailable"),
                     (new OperatorRecoveryUnavailableException(cause: new TimeoutException("forced")), "dependency_unavailable"),
                     (new OperatorRecoveryUnavailableException(commitOutcomeUnknown: true, cause: new TimeoutException("forced")), "outcome_unknown"),
                 })
        {
            _closures.Failure = failure;

            var response = await Call(HttpMethod.Post, FinalizePath, Valid(), "{}");

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, reason);
            response.Headers.RetryAfter.Should().NotBeNull(reason);
            var text = await response.Content.ReadAsStringAsync();
            text.Should().NotContain("forced");
            using var body = JsonDocument.Parse(text);
            body.RootElement.GetProperty("code").GetString().Should().Be(reason == "outcome_unknown" ? "dependency_unavailable" : reason);
            body.RootElement.TryGetProperty("errors", out var errors).Should().Be(reason == "outcome_unknown", reason);
            if (reason == "outcome_unknown") errors.GetProperty("outcome").ToString().Should().Contain("outcome_unknown");
        }
    }

    [Fact]
    public async Task An_Audit_Store_That_Fails_After_The_Provider_Step_Is_A_Retryable_503_And_Still_Raises_The_Audit_Alert()
    {
        // A MUST row refused at the act's last step arrives wrapped, because the account is already deleted: the answer is the
        // retryable unavailable, never the audit refusal that says nothing was done, yet the audit store's failure is counted.
        using var audit = new OperatorCounterListener(PlatformOperatorMetrics.AuditFailureCounterName);
        using var dataRequests = new OperatorCounterListener(PlatformOperatorMetrics.DataRequestCounterName);
        _closures.Failure = new OperatorRecoveryUnavailableException(cause: new AuditWriteFailedException("forced"));

        var response = await Call(HttpMethod.Post, FinalizePath, Valid(), "{}");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var text = await response.Content.ReadAsStringAsync();
        text.Should().NotContain("forced");
        using var body = JsonDocument.Parse(text);
        body.RootElement.GetProperty("code").GetString().Should().Be("dependency_unavailable",
            "the audit refusal says the provider was not asked, which is false here");
        audit.Calls.Select(call => call.Operation).Should().Equal(["platform.closure.finalize"],
            "the audit store's failure is the alert's signal whatever the answer is");
        dataRequests.Calls.Should().Contain(("platform.closure.finalize", "unavailable"));
        dataRequests.Calls.Should().NotContain(call => call.Outcome == "audit_unavailable");
    }

    [Fact]
    public async Task The_Finalize_Counter_Is_Labelled_By_The_Operation_Slug_And_A_Closed_Outcome_And_Counts_Only_Calls_That_Reached_The_Owner()
    {
        using var counted = new OperatorCounterListener(PlatformOperatorMetrics.DataRequestCounterName);

        (await Call(HttpMethod.Post, FinalizePath, Valid(), "{}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Call(HttpMethod.Post, FinalizePath, Valid())).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        foreach (var result in new[]
                 {
                     new ClosureFinalizeResult(ClosureFinalizeOutcome.AlreadyFinalized, "completed"),
                     new ClosureFinalizeResult(ClosureFinalizeOutcome.NotFound),
                     new ClosureFinalizeResult(ClosureFinalizeOutcome.NotClosure, "scheduled"),
                     new ClosureFinalizeResult(ClosureFinalizeOutcome.NotFinalizable, "cancelled"),
                     new ClosureFinalizeResult(ClosureFinalizeOutcome.GroupsPending, "fulfilling", new ClosureFinalizeTally(1, 0, 1, 0)),
                     new ClosureFinalizeResult(ClosureFinalizeOutcome.ProviderRefused, "fulfilling"),
                     new ClosureFinalizeResult(ClosureFinalizeOutcome.ProviderUnavailable, "fulfilling"),
                     new ClosureFinalizeResult(ClosureFinalizeOutcome.ProviderUncertain, "fulfilling"),
                 })
        {
            _closures.FinalizeResult = result;
            (await Call(HttpMethod.Post, FinalizePath, Valid(), "{}")).Should().NotBeNull();
        }

        _closures.Failure = new AuditWriteFailedException("forced");
        (await Call(HttpMethod.Post, FinalizePath, Valid(), "{}")).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        _closures.Failure = new OperatorRecoveryUnavailableException(cause: new TimeoutException("forced"));
        (await Call(HttpMethod.Post, FinalizePath, Valid(), "{}")).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        _closures.Failure = new OperatorRecoveryUnavailableException(commitOutcomeUnknown: true);
        (await Call(HttpMethod.Post, FinalizePath, Valid(), "{}")).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        _closures.Failure = new InvalidOperationException("forced defect");
        var failed = await Call(HttpMethod.Post, FinalizePath, Valid(), "{}");
        failed.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "the failure still propagates to the host");
        (await failed.Content.ReadAsStringAsync()).Should().NotContain("forced defect");

        // The meter is process-wide: a closure's fan-out groups another class counts at the same time are not this route's.
        const string Slug = "platform.closure.finalize";
        counted.Calls.Where(call => call.Operation == Slug).Should().Equal(
            (Slug, "finalized"), (Slug, "already_finalized"), (Slug, "not_found"), (Slug, "not_closure"), (Slug, "not_finalizable"),
            (Slug, "groups_pending"), (Slug, "provider_refused"), (Slug, "provider_unavailable"), (Slug, "provider_uncertain"),
            (Slug, "audit_unavailable"), (Slug, "unavailable"), (Slug, "commit_unknown"), (Slug, "failed"));
    }

    [Theory]
    [InlineData(DataRequestKey.None)]
    [InlineData(DataRequestKey.Read)]
    [InlineData(DataRequestKey.Write)]
    public async Task Only_The_Data_Request_Write_Key_Admits_A_Finalize(DataRequestKey held)
    {
        // ADR-0107 § 8: Finalize is a write under platform.data_request.write. The read key does not admit it, and None holds
        // every other registered Platform key.
        string[] keys = held switch
        {
            DataRequestKey.Read => [PlatformPermissions.DataRequestRead.Key],
            DataRequestKey.Write => [PlatformPermissions.DataRequestWrite.Key],
            _ => PlatformPermissions.All
                .Where(key => key != PlatformPermissions.DataRequestRead && key != PlatformPermissions.DataRequestWrite)
                .Select(key => key.Key).ToArray(),
        };
        var closures = new RecordingClosures();
        await using var single = new PlatformOperatorApiFactory(_tenant, _operator, _toggle, grantSubject: Subject,
            services: registrations => registrations.AddScoped<IAccountClosureOperatorOperations>(_ => closures),
            permissions: keys);
        using var client = single.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, FinalizePath)
        {
            Headers = { { "Authorization", "Bearer " + Valid() } },
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };

        (await client.SendAsync(request)).StatusCode.Should().Be(held == DataRequestKey.Write ? HttpStatusCode.OK : HttpStatusCode.Forbidden);
        closures.FinalizeCalls.Should().HaveCount(held == DataRequestKey.Write ? 1 : 0);
    }

    [Fact]
    public async Task The_Served_Contract_Documents_The_Finalize_s_Readiness_Provider_Outcomes_And_Repeat()
    {
        using var document = JsonDocument.Parse(await _client.GetStringAsync("/openapi/platform-v1.json"));
        var finalize = document.RootElement.GetProperty("paths")
            .GetProperty("/api/platform/v1/data-requests/{requestId}/finalize-closure").GetProperty("post");
        finalize.GetProperty("operationId").GetString().Should().Be("PlatformClosureFinalize");
        finalize.GetProperty("description").GetString().Should().Contain("awaits its privileged step").And.Contain("unserved")
            .And.Contain("intent row").And.Contain("missing account is success").And.Contain("already_finalized");
        finalize.GetProperty("responses").GetProperty("409").GetProperty("description").GetString()
            .Should().Contain("errors.groups").And.Contain("A closure Finalize answers 409");
        finalize.GetProperty("responses").GetProperty("503").GetProperty("description").GetString()
            .Should().Contain("errors.provider").And.Contain("lockey_provider_outcome_unknown");
        finalize.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").ValueKind
            .Should().Be(JsonValueKind.Object, "the body is the ticket's, as for every data-request act");
    }

    /// <summary>A finalization of three served groups and one unserved, with the redaction this call's passes did.</summary>
    private static ClosureFinalizeResult FinalizedResult() => new(
        ClosureFinalizeOutcome.Finalized, "completed", new ClosureFinalizeTally(3, 3, 0, 1, 3, 2),
        [new ClosureParticipantGroup(TenantId.From(Guid.Parse(UnservedTenant)), OrganizationId.From(Guid.Parse(UnservedOrganization)))],
        Aggregate(),
        new Dictionary<string, long>(StringComparer.Ordinal) { ["audit_actor_reference"] = 4, ["unserved_group"] = 1 });

    private static AuditRedactionAggregate Aggregate()
    {
        static AuditRedactionReport Report(long selected, long redacted, params (RetainedReferenceKind Kind, long Count)[] retained) => new(
            selected,
            Enum.GetValues<AuditRedactedColumn>().ToDictionary(column => column, column => column is AuditRedactedColumn.IpAddress
                or AuditRedactedColumn.UserAgent or AuditRedactedColumn.ActorEmail ? redacted : 0L),
            [.. retained.Select(entry => new RetainedReferenceCount(entry.Kind, entry.Count))]);
        return new AuditRedactionAggregate(
            new Dictionary<TenantId, AuditRedactionReport>
            {
                [TenantId.From(Guid.CreateVersion7())] = Report(2, 1, (RetainedReferenceKind.AuditActorReference, 1)),
                [TenantId.From(Guid.CreateVersion7())] = Report(2, 1, (RetainedReferenceKind.AuditEntityReference, 2)),
            },
            Report(5, 3, (RetainedReferenceKind.AuditMetadataReference, 3)));
    }
}
