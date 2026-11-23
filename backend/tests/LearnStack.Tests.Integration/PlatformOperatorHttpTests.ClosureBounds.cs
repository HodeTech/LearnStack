using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.PlatformOperator;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Platform;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>
/// The closure routes' answers for what a closure could never finish (ADR-0107 § 7): a Begin refused for the closure's
/// size or for an account the provider's port could never delete answers 409 on the field it concerns, with no cause, no
/// count and no identifier; each has its own counter outcome; and the served contract says what the operator does, and
/// that a Finalize's unavailable answer after its provider step may follow a deleted account.
/// </summary>
public sealed partial class PlatformOperatorHttpTests
{
    [Theory]
    [InlineData(ClosureBeginOutcome.RefusedTooLarge, "participants", "lockey_data_request_closure_too_large")]
    [InlineData(ClosureBeginOutcome.RefusedProvider, "provider", "lockey_account_removal_unavailable")]
    public async Task A_Begin_Refused_For_What_It_Could_Never_Finish_Is_A_409_On_Its_Own_Field_With_Nothing_Else(
        ClosureBeginOutcome outcome, string field, string reason)
    {
        _closures.Result = new ClosureBeginResult(outcome, "scheduled");

        var response = await Call(HttpMethod.Post, BeginPath, Valid(), "{}");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("business_rule_violation");
        var errors = body.RootElement.GetProperty("errors");
        errors.EnumerateObject().Select(property => property.Name).Should().Equal([field], "the reason is on the field it concerns, and only there");
        var message = errors.GetProperty(field)[0];
        message.GetProperty("key").GetString().Should().Be(reason);
        if (message.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object)
        {
            parameters.EnumerateObject().Should().BeEmpty("no group, no person and no count is named");
        }
    }

    [Fact]
    public async Task The_Begin_Counter_Counts_The_Refusals_Of_What_A_Closure_Could_Never_Finish_Under_Their_Own_Outcomes()
    {
        using var counted = new OperatorCounterListener(PlatformOperatorMetrics.DataRequestCounterName);

        foreach (var outcome in new[] { ClosureBeginOutcome.RefusedTooLarge, ClosureBeginOutcome.RefusedProvider })
        {
            _closures.Result = new ClosureBeginResult(outcome, "scheduled");
            (await Call(HttpMethod.Post, BeginPath, Valid(), "{}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        const string Slug = "platform.closure.begin";
        counted.Calls.Where(call => call.Operation == Slug).Should().Equal((Slug, "refused_too_large"), (Slug, "refused_provider"));
    }

    [Fact]
    public async Task The_Served_Contract_Says_What_A_Closure_Could_Never_Finish_And_What_A_Late_Unavailable_Finalize_Means()
    {
        using var document = JsonDocument.Parse(await _client.GetStringAsync("/openapi/platform-v1.json"));
        var paths = document.RootElement.GetProperty("paths");
        var begin = paths.GetProperty("/api/platform/v1/data-requests/{requestId}/begin-closure").GetProperty("post");
        begin.GetProperty("description").GetString().Should()
            .Contain(ClosureLimits.MaximumParticipantGroups.ToString(CultureInfo.InvariantCulture) + " participant groups, served or not")
            .And.Contain(ClosureLimits.MaximumTenantMemberships.ToString(CultureInfo.InvariantCulture) + " Memberships in one participant tenant")
            .And.Contain("could never delete");
        begin.GetProperty("responses").GetProperty("409").GetProperty("description").GetString().Should()
            .Contain("errors.participants").And.Contain("errors.provider").And.Contain("Phase 11's data operations");
        var finalize = paths.GetProperty("/api/platform/v1/data-requests/{requestId}/finalize-closure").GetProperty("post");
        finalize.GetProperty("description").GetString().Should().Contain("User fence alone").And.Contain("stays required");
        finalize.GetProperty("responses").GetProperty("503").GetProperty("description").GetString().Should()
            .Contain("the person's account may already be deleted at the provider");
    }
}
