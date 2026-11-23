using System.Net;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.PlatformOperator;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>
/// The closure's Begin route (ADR-0107 §§ 7 and 8): the admission it runs under, the answers it maps, the key that admits
/// it, its counter, its document, and the production composition that fails the act closed with no fan-out registered.
/// </summary>
public sealed partial class PlatformOperatorHttpTests
{
    private const string ClosureRequest = "019a1929-5450-7bbb-8df1-516e8e002505";
    private const string BeginPath = DataRequests + "/" + ClosureRequest + "/begin-closure";

    [Fact]
    public async Task A_Begin_Runs_Under_An_Admission_For_Exactly_That_Operation_And_Answers_The_Closed_Tallies()
    {
        var response = await Call(HttpMethod.Post, BeginPath, Valid(), """{"ticket":"CHG-21"}""");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var (admission, requestId) = _closures.Calls.Should().ContainSingle().Which;
        requestId.Should().Be(Guid.Parse(ClosureRequest));
        admission.Operation.Should().BeSameAs(PlatformOperations.ClosureBegin);
        admission.Ticket.Should().Be("CHG-21");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        root.GetProperty("outcome").GetString().Should().Be("begun");
        root.GetProperty("requestId").GetString().Should().Be(ClosureRequest);
        root.GetProperty("status").GetString().Should().Be("fulfilling");
        root.GetProperty("complete").GetBoolean().Should().BeTrue();
        root.GetProperty("groups").ToString().Should().Be(
            """{"served":2,"started":1,"adopted":1,"waiting":0,"failed":0,"unserved":1}""");
        root.GetProperty("correlationId").GetString().Should().Be(admission.Operator.CorrelationId);
        root.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            ["outcome", "requestId", "status", "complete", "groups", "correlationId"], "the answer names no person and no tenant");
    }

    [Fact]
    public async Task A_Begin_Whose_Fan_Out_Did_Not_Reach_Every_Group_Is_A_200_That_Says_It_Is_Incomplete()
    {
        _closures.Result = new ClosureBeginResult(
            ClosureBeginOutcome.Resumed, "fulfilling", Groups: new ClosureBeginTally(3, 0, 1, 1, 1, 0));

        var response = await Call(HttpMethod.Post, BeginPath, Valid(), "{}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("outcome").GetString().Should().Be("resumed");
        body.RootElement.GetProperty("complete").GetBoolean().Should().BeFalse("a waiting or failed group is owed a repeat");
        body.RootElement.GetProperty("groups").GetProperty("failed").GetInt32().Should().Be(1);
    }

    [Theory]
    [InlineData(ClosureBeginOutcome.RefusedWindow, "refusal", "lockey_data_request_closure_refused", "blocked_window")]
    [InlineData(ClosureBeginOutcome.RefusedHold, "refusal", "lockey_data_request_closure_refused", "blocked_hold")]
    [InlineData(ClosureBeginOutcome.RefusedLastAdministrator, "refusal", "lockey_data_request_closure_refused", "blocked_last_administrator")]
    [InlineData(ClosureBeginOutcome.NotClosure, "kind", "lockey_data_request_not_closure", null)]
    [InlineData(ClosureBeginOutcome.NotBeginnable, "status", "lockey_data_request_closure_not_beginnable", "cancelled")]
    [InlineData(ClosureBeginOutcome.SubjectNotAdmitted, "account", "lockey_data_request_closure_account_closed", null)]
    public async Task A_Refused_Begin_Is_A_409_With_Its_Reason_On_The_Field_It_Concerns(
        ClosureBeginOutcome outcome, string field, string reason, string? cause)
    {
        _closures.Result = new ClosureBeginResult(
            outcome, outcome == ClosureBeginOutcome.NotBeginnable ? cause : "scheduled",
            outcome is ClosureBeginOutcome.RefusedWindow or ClosureBeginOutcome.RefusedHold or ClosureBeginOutcome.RefusedLastAdministrator
                ? cause : null);

        var response = await Call(HttpMethod.Post, BeginPath, Valid(), "{}");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("business_rule_violation");
        var errors = body.RootElement.GetProperty("errors");
        errors.EnumerateObject().Select(property => property.Name).Should().Equal([field], "the reason is on the field it concerns, and only there");
        errors.GetProperty(field)[0].GetProperty("key").GetString().Should().Be(reason);
        if (cause is not null)
        {
            errors.GetProperty(field)[0].GetProperty("params").GetProperty("cause").GetString().Should().Be(cause,
                "the closed code that refused it is the reason's cause");
        }
    }

    [Fact]
    public async Task A_Begin_That_Names_No_Request_Is_Not_Found()
    {
        _closures.Result = new ClosureBeginResult(ClosureBeginOutcome.NotFound);

        var response = await Call(HttpMethod.Post, BeginPath, Valid(), "{}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await AssertProblem(response, "not_found");
    }

    [Theory]
    [InlineData("/data-requests/not-a-guid/begin-closure", HttpStatusCode.NotFound)]
    [InlineData("/data-requests/00000000-0000-0000-0000-000000000000/begin-closure", HttpStatusCode.BadRequest)]
    [InlineData("/data-requests/00000000-0000-7000-8000-000000000002/begin-closure", HttpStatusCode.BadRequest)]
    [InlineData("/data-requests/4f9c5a3e-2b7d-4c1a-9e8f-0a1b2c3d4e5f/begin-closure", HttpStatusCode.BadRequest)]
    public async Task A_Begin_Naming_No_Real_Request_Is_Refused_Without_Calling_The_Owner(string suffix, HttpStatusCode expected)
    {
        var response = await Call(HttpMethod.Post, "/api/platform/v1" + suffix, Valid(), "{}");

        response.StatusCode.Should().Be(expected);
        _closures.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Begin_Without_A_Body_Or_With_A_Bad_Ticket_Or_Media_Type_Is_Answered_Only_To_A_Granted_Operator()
    {
        (await Call(HttpMethod.Post, BeginPath, Valid())).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Call(HttpMethod.Post, BeginPath, Valid(), """{"ticket":"has space"}""")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var wrongType = new HttpRequestMessage(HttpMethod.Post, BeginPath)
        {
            Headers = { { "Authorization", "Bearer " + Valid() } },
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "text/plain"),
        };
        (await _client.SendAsync(wrongType)).StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await Call(HttpMethod.Post, BeginPath, Sign(("sub", "someone-else")), "not json")).StatusCode.Should().Be(
            HttpStatusCode.Forbidden, "an operator without the grant learns nothing about the body");
        (await Call(HttpMethod.Post, BeginPath, "not-a-token", "{}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _closures.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Begin_Whose_Audit_Or_Fences_Failed_Or_Whose_Commit_Is_Unknown_Is_A_Retryable_503_With_Its_Own_Reason()
    {
        foreach (var (failure, reason) in new (Exception, string)[]
                 {
                     (new AuditWriteFailedException("forced"), "audit_unavailable"),
                     (new OperatorRecoveryUnavailableException(cause: new TimeoutException("forced")), "dependency_unavailable"),
                     (new OperatorRecoveryUnavailableException(commitOutcomeUnknown: true, cause: new TimeoutException("forced")), "outcome_unknown"),
                 })
        {
            _closures.Failure = failure;

            var response = await Call(HttpMethod.Post, BeginPath, Valid(), "{}");

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
    public async Task The_Begin_Counter_Is_Labelled_By_The_Operation_Slug_And_A_Closed_Outcome_And_Counts_Only_Calls_That_Reached_The_Owner()
    {
        using var counted = new OperatorCounterListener(PlatformOperatorMetrics.DataRequestCounterName);

        (await Call(HttpMethod.Post, BeginPath, Valid(), "{}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Call(HttpMethod.Post, BeginPath, Valid())).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        foreach (var result in new[]
                 {
                     new ClosureBeginResult(ClosureBeginOutcome.Begun, "fulfilling", Groups: new ClosureBeginTally(1, 0, 0, 0, 1, 0)),
                     new ClosureBeginResult(ClosureBeginOutcome.Resumed, "fulfilling", Groups: new ClosureBeginTally(1, 0, 1, 0, 0, 0)),
                     new ClosureBeginResult(ClosureBeginOutcome.Resumed, "fulfilling", Groups: new ClosureBeginTally(1, 0, 0, 1, 0, 0)),
                     new ClosureBeginResult(ClosureBeginOutcome.RefusedWindow, "scheduled", "blocked_window"),
                     new ClosureBeginResult(ClosureBeginOutcome.RefusedHold, "scheduled", "blocked_hold"),
                     new ClosureBeginResult(ClosureBeginOutcome.RefusedLastAdministrator, "scheduled", "blocked_last_administrator"),
                     new ClosureBeginResult(ClosureBeginOutcome.NotFound),
                     new ClosureBeginResult(ClosureBeginOutcome.NotClosure, "scheduled"),
                     new ClosureBeginResult(ClosureBeginOutcome.NotBeginnable, "completed"),
                     new ClosureBeginResult(ClosureBeginOutcome.SubjectNotAdmitted, "scheduled"),
                 })
        {
            _closures.Result = result;
            (await Call(HttpMethod.Post, BeginPath, Valid(), "{}")).Should().NotBeNull();
        }

        _closures.Failure = new AuditWriteFailedException("forced");
        (await Call(HttpMethod.Post, BeginPath, Valid(), "{}")).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        _closures.Failure = new OperatorRecoveryUnavailableException(cause: new TimeoutException("forced"));
        (await Call(HttpMethod.Post, BeginPath, Valid(), "{}")).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        _closures.Failure = new OperatorRecoveryUnavailableException(commitOutcomeUnknown: true);
        (await Call(HttpMethod.Post, BeginPath, Valid(), "{}")).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        _closures.Failure = new InvalidOperationException("forced defect");
        var failed = await Call(HttpMethod.Post, BeginPath, Valid(), "{}");
        failed.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "the failure still propagates to the host");
        (await failed.Content.ReadAsStringAsync()).Should().NotContain("forced defect");

        const string Slug = "platform.closure.begin";
        counted.Calls.Should().Equal(
            (Slug, "begun"), (Slug, "begun_partial"), (Slug, "resumed"), (Slug, "resumed_partial"), (Slug, "blocked_window"),
            (Slug, "blocked_hold"), (Slug, "blocked_last_administrator"), (Slug, "not_found"), (Slug, "not_closure"),
            (Slug, "not_beginnable"), (Slug, "account_closed"), (Slug, "audit_unavailable"), (Slug, "unavailable"),
            (Slug, "commit_unknown"), (Slug, "failed"));
    }

    [Theory]
    [InlineData(DataRequestKey.None)]
    [InlineData(DataRequestKey.Read)]
    [InlineData(DataRequestKey.Write)]
    public async Task Only_The_Data_Request_Write_Key_Admits_A_Begin(DataRequestKey held)
    {
        // ADR-0107 § 8: Begin is a write under platform.data_request.write. The read key does not admit it, and None holds
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
        using var request = new HttpRequestMessage(HttpMethod.Post, BeginPath)
        {
            Headers = { { "Authorization", "Bearer " + Valid() } },
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };

        (await client.SendAsync(request)).StatusCode.Should().Be(held == DataRequestKey.Write ? HttpStatusCode.OK : HttpStatusCode.Forbidden);
        closures.Calls.Should().HaveCount(held == DataRequestKey.Write ? 1 : 0);
    }

    [Fact]
    public async Task The_Production_Composition_Resolves_The_Closure_Owner_And_Fails_The_Act_Closed_Without_A_Fan_Out()
    {
        // Production's registrations, with the platform scope recorded and every fan-out removed: the act is refused before
        // the scope is entered, so no person can be moved to Closing by a host that cannot fan the closure out. The owner
        // reads Tenancy's lifecycle port, whose registration holds the request's unit of work, so the host is given the
        // application credential's holder a deployment has; nothing here connects with it.
        var scope = new RecordingScope();
        await using var production = new PlatformOperatorApiFactory(_tenant, _operator, _toggle, grantSubject: Subject,
            services: registrations =>
            {
                registrations.RemoveAll<IClosureFanOut>();
                registrations.Replace(ServiceDescriptor.Singleton<IPlatformAdminScope>(scope));
                registrations.Replace(ServiceDescriptor.Singleton(_ => new LearnStack.Infrastructure.Persistence.ApplicationDataSourceLifetime(
                    "Host=localhost;Port=1;Database=learnstack;Username=learnstack_app")));
            });
        using var client = production.CreateClient();
        await using (var resolved = production.Services.CreateAsyncScope())
        {
            resolved.ServiceProvider.GetRequiredService<IAccountClosureOperatorOperations>()
                .Should().BeOfType<LearnStack.Modules.Identity.Infrastructure.DataRequests.AccountClosureOperatorOperations>();
        }

        using var counted = new OperatorCounterListener(PlatformOperatorMetrics.DataRequestCounterName);
        using var request = new HttpRequestMessage(HttpMethod.Post, BeginPath)
        {
            Headers = { { "Authorization", "Bearer " + Valid() } },
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "a host that cannot fan out is a deployment defect");
        scope.Entered.Should().Be(0, "the act was refused before it entered the platform scope");
        counted.Calls.Should().Equal(("platform.closure.begin", "failed"));
    }

    [Fact]
    public async Task The_Served_Contract_Documents_The_Begin_s_Refusals_Partial_Answer_And_Runbook()
    {
        using var document = JsonDocument.Parse(await _client.GetStringAsync("/openapi/platform-v1.json"));
        var begin = document.RootElement.GetProperty("paths").GetProperty("/api/platform/v1/data-requests/{requestId}/begin-closure")
            .GetProperty("post");
        begin.GetProperty("description").GetString().Should().Contain("blocked_window").And.Contain("blocked_hold")
            .And.Contain("blocked_last_administrator").And.Contain("complete is false").And.Contain("unserved");
        begin.GetProperty("responses").GetProperty("409").GetProperty("description").GetString()
            .Should().Contain("errors.refusal").And.Contain("errors.account").And.Contain("A closure Begin answers 409");
        begin.GetProperty("responses").GetProperty("503").GetProperty("description").GetString()
            .Should().Contain("three seconds");
        document.RootElement.GetProperty("info").GetProperty("description").GetString().Should().Contain("account-closure.md");
    }

    /// <summary>A platform scope that counts its entries and enters nothing.</summary>
    private sealed class RecordingScope : IPlatformAdminScope
    {
        public int Entered { get; private set; }

        public Task<IPlatformAdminScopeHandle> EnterAsync(
            PlatformAdmission admission, CancellationToken cancellationToken = default,
            [System.Runtime.CompilerServices.CallerMemberName] string? callerMember = null,
            [System.Runtime.CompilerServices.CallerFilePath] string? callerFile = null,
            [System.Runtime.CompilerServices.CallerLineNumber] int callerLine = 0)
        {
            Entered++;
            return Task.FromException<IPlatformAdminScopeHandle>(new InvalidOperationException("No scope is entered in this test."));
        }
    }

    private sealed class RecordingClosures : IAccountClosureOperatorOperations
    {
        public List<(PlatformAdmission Admission, Guid RequestId)> Calls { get; } = [];
        public Exception? Failure { get; set; }
        public ClosureBeginResult Result { get; set; } = new(
            ClosureBeginOutcome.Begun, "fulfilling", Groups: new ClosureBeginTally(2, 1, 1, 0, 0, 1));

        public Task<ClosureBeginResult> BeginAsync(
            PlatformAdmission admission, Guid closureRequestId, CancellationToken cancellationToken = default)
        {
            Calls.Add((admission, closureRequestId));
            return Failure is not null ? Task.FromException<ClosureBeginResult>(Failure) : Task.FromResult(Result);
        }
    }
}
