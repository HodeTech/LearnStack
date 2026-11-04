using System.Collections.Immutable;
using System.Text;
using LearnStack.Api.Common;
using LearnStack.Api.Idempotency;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Common;

public sealed class ProtectedReceiptTests
{
    private static readonly Guid Target = Guid.Parse("018f4d40-0000-7000-8000-000000000011");
    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("018f4d40-0000-7000-8000-000000000022"));

    [Theory]
    [InlineData(ProtectedAdministrationOperation.SetMembershipProfile, typeof(ReadMemberProfileReceiptQuery))]
    [InlineData(ProtectedAdministrationOperation.SetOwnMembershipProfile, typeof(ReadOwnMembershipProfileReceiptQuery))]
    [InlineData(ProtectedAdministrationOperation.ChangeMembershipStatus, typeof(ReadMembershipReceiptQuery))]
    [InlineData(ProtectedAdministrationOperation.ReplaceMembershipRoleAssignments, typeof(ReadMembershipReceiptQuery))]
    [InlineData(ProtectedAdministrationOperation.ChangeRoleGrants, typeof(ReadRoleReceiptQuery))]
    [InlineData(ProtectedAdministrationOperation.AdoptRoleGrantManifest, typeof(ReadRoleReceiptQuery))]
    [InlineData(ProtectedAdministrationOperation.CreateOrganization, typeof(ReadOrganizationReceiptQuery))]
    [InlineData(ProtectedAdministrationOperation.RenameOrganization, typeof(ReadOrganizationReceiptQuery))]
    [InlineData(ProtectedAdministrationOperation.ArchiveOrganization, typeof(ReadOrganizationReceiptQuery))]
    [InlineData(ProtectedAdministrationOperation.SelectDefaultOrganization, typeof(ReadDefaultOrganizationReceiptQuery))]
    public async Task Closed_typed_dispatch_captures_the_actual_result_and_replays_the_exact_receipt_read(
        ProtectedAdministrationOperation operation, Type queryType)
    {
        var sender = Sender();
        var tenant = Context();
        var descriptor = ProtectedAdministrationDescriptor.For(operation);
        var command = Command(operation);
        var http = Http(operation);
        var action = await ProtectedAdministrationResult.ExecuteAsync(http, sender, tenant, command, CancellationToken.None);
        var receipt = Assert.IsType<ProtectedAdministrationResult>(action);
        Assert.True(receipt.Matches(descriptor, tenant, descriptor.Creates ? null : 7));
        Assert.Equal(descriptor.TargetKind, receipt.Target.Kind);
        Assert.Equal(operation == ProtectedAdministrationOperation.SelectDefaultOrganization ? Tenant.Value : Target, receipt.Target.Id);
        Assert.Same(command, Assert.Single(sender.ReceivedCalls()).GetArguments()[0]);
        await action.ExecuteResultAsync(new ActionContext(http, new RouteData(), new ActionDescriptor()));
        Assert.Equal("{\"version\":\"8\"}", Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray()));
        var stored = new IdempotentResponse(http.Response.StatusCode, http.Response.ContentType,
            ProtectedIdempotencyFilter.CaptureHeaders(http.Response), ((MemoryStream)http.Response.Body).ToArray(), receipt.Target);
        Assert.True(ProtectedIdempotencyFilter.ValidStoredResponse(stored, descriptor, Tenant));

        sender.ClearReceivedCalls();
        var read = await new ProtectedReceiptReplayDispatcher(sender).ReadAsync(descriptor, receipt.Target, CancellationToken.None);
        Assert.True(read.IsSuccess);
        var query = Assert.Single(sender.ReceivedCalls()).GetArguments()[0];
        Assert.IsType(queryType, query);
        var id = query switch
        {
            ReadMemberProfileReceiptQuery value => value.MembershipId,
            ReadOwnMembershipProfileReceiptQuery value => value.MembershipId,
            ReadMembershipReceiptQuery value => value.MembershipId,
            ReadRoleReceiptQuery value => value.RoleId,
            ReadOrganizationReceiptQuery value => value.OrganizationId.Value,
            ReadDefaultOrganizationReceiptQuery value => value.TenantId.Value,
            _ => throw new InvalidOperationException(),
        };
        Assert.Equal(receipt.Target.Id, id);
    }

    [Fact]
    public async Task Descriptor_or_header_mismatch_never_sends_a_mutation()
    {
        var sender = Sender();
        var http = Http(ProtectedAdministrationOperation.RenameOrganization);
        var command = Command(ProtectedAdministrationOperation.ArchiveOrganization);
        Assert.IsType<ProblemDetailsActionResult>(await ProtectedAdministrationResult.ExecuteAsync(http, sender, Context(), command, CancellationToken.None));
        http = Http(ProtectedAdministrationOperation.ArchiveOrganization);
        http.Request.Headers.IfMatch = "\"6\"";
        Assert.IsType<ProblemDetailsActionResult>(await ProtectedAdministrationResult.ExecuteAsync(http, sender, Context(), command, CancellationToken.None));
        http.SetEndpoint(null);
        Assert.IsType<ProblemDetailsActionResult>(await ProtectedAdministrationResult.ExecuteAsync(http, sender, Context(), command, CancellationToken.None));
        Assert.Empty(sender.ReceivedCalls());
    }

    [Theory]
    [InlineData(1024, true)]
    [InlineData(1025, false)]
    public void Receipt_cap_includes_UTF8_header_names_and_values_once(int size, bool valid)
    {
        var response = Receipt();
        var headers = new Dictionary<string, IReadOnlyList<string>>(response.Headers, StringComparer.OrdinalIgnoreCase)
        {
            ["X-Pad"] = ["é"],
        };
        var bytes = response.Body.Length + headers.Sum(header => Encoding.UTF8.GetByteCount(header.Key)
            + header.Value.Sum(Encoding.UTF8.GetByteCount));
        headers["X-Pad"] = ["é" + new string('x', size - bytes)];
        Assert.Equal(valid, Valid(response with { Headers = headers }));
    }

    [Theory]
    [InlineData("missing-target")]
    [InlineData("empty-target")]
    [InlineData("v4-target")]
    [InlineData("wrong-kind")]
    [InlineData("status")]
    [InlineData("extra-body")]
    [InlineData("extra-field")]
    [InlineData("numeric-version")]
    [InlineData("weak-tag")]
    [InlineData("different-tag")]
    [InlineData("repeated-tag")]
    [InlineData("missing-content-type")]
    [InlineData("location-on-update")]
    [InlineData("cookie")]
    [InlineData("correlation")]
    [InlineData("framing")]
    [InlineData("te")]
    [InlineData("invalid-header-name")]
    [InlineData("invalid-header-value")]
    [InlineData("unpaired-surrogate")]
    public void Malformed_persisted_receipts_cannot_be_disclosed(string corruption)
    {
        var response = Receipt();
        var headers = new Dictionary<string, IReadOnlyList<string>>(response.Headers, StringComparer.OrdinalIgnoreCase);
        response = corruption switch
        {
            "missing-target" => response with { ReplayTarget = null },
            "empty-target" => response with { ReplayTarget = new(IdempotencyReplayTargetKind.Organization, Guid.Empty) },
            "v4-target" => response with { ReplayTarget = new(IdempotencyReplayTargetKind.Organization, Guid.NewGuid()) },
            "wrong-kind" => response with { ReplayTarget = new(IdempotencyReplayTargetKind.Membership, Target) },
            "status" => response with { StatusCode = 201 },
            "extra-body" => response with { Body = "{\"version\":\"8\"} "u8.ToArray() },
            "extra-field" => response with { Body = "{\"version\":\"8\",\"name\":\"private\"}"u8.ToArray() },
            "numeric-version" => response with { Body = "{\"version\":8}"u8.ToArray() },
            _ => response,
        };
        switch (corruption)
        {
            case "weak-tag": headers["ETag"] = ["W/\"8\""]; break;
            case "different-tag": headers["ETag"] = ["\"9\""]; break;
            case "repeated-tag": headers["ETag"] = ["\"8\"", "\"8\""]; break;
            case "missing-content-type": headers.Remove("Content-Type"); break;
            case "location-on-update": headers["Location"] = ["/unexpected"]; break;
            case "cookie": headers["Set-Cookie"] = ["session=private"]; break;
            case "correlation": headers[CorrelationHeaderMiddleware.HeaderName] = ["original-exchange"]; break;
            case "framing": headers["Content-Length"] = ["15"]; break;
            case "te": headers["TE"] = ["trailers"]; break;
            case "invalid-header-name": headers["Bad Header"] = ["value"]; break;
            case "invalid-header-value": headers["X-Outcome"] = ["value\r\nprivate"]; break;
            case "unpaired-surrogate": headers["X-Outcome"] = ["\uD800"]; break;
        }
        Assert.False(Valid(response with { Headers = headers }));
    }

    [Fact]
    public void Exchange_headers_are_excluded_and_outcome_headers_survive_capture()
    {
        var response = new DefaultHttpContext().Response;
        response.Headers.Connection = "X-This-Exchange, keep-alive";
        response.Headers["X-This-Exchange"] = "private";
        response.Headers["TE"] = "trailers";
        response.Headers["Set-Cookie"] = "private";
        response.Headers["X-Outcome"] = "retained";
        response.Headers.ETag = "\"8\"";
        var headers = ProtectedIdempotencyFilter.CaptureHeaders(response);
        Assert.Equal(2, headers.Count);
        Assert.Equal("retained", Assert.Single(headers["X-Outcome"]));
        Assert.Equal("\"8\"", Assert.Single(headers["ETag"]));
    }

    private static bool Valid(IdempotentResponse response) => ProtectedIdempotencyFilter.ValidStoredResponse(response,
        ProtectedAdministrationDescriptor.For(ProtectedAdministrationOperation.RenameOrganization), Tenant);
    private static IdempotentResponse Receipt() => new(200, "application/json; charset=utf-8",
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        { ["ETag"] = ["\"8\""], ["Content-Type"] = ["application/json; charset=utf-8"] },
        ProtectedAdministrationResult.BodyFor(8), new(IdempotencyReplayTargetKind.Organization, Target));
    private static ISender Sender()
    {
        var sender = Substitute.For<ISender>();
        sender.Send(Arg.Any<IRequest<Result<MembershipMutationResult>>>(), Arg.Any<CancellationToken>()).Returns(Result.Ok(new MembershipMutationResult(Target, 8)));
        sender.Send(Arg.Any<IRequest<Result<OrganizationMutationResult>>>(), Arg.Any<CancellationToken>()).Returns(Result.Ok(new OrganizationMutationResult(OrganizationId.From(Target), 8)));
        sender.Send(Arg.Any<IRequest<Result<long>>>(), Arg.Any<CancellationToken>()).Returns(Result.Ok(8L));
        sender.Send(Arg.Any<IRequest<Result<None>>>(), Arg.Any<CancellationToken>()).Returns(Result.Ok(None.Value));
        sender.ClearReceivedCalls();
        return sender;
    }
    private static ITenantContext Context()
    {
        var context = Substitute.For<ITenantContext>();
        context.IsResolved.Returns(true);
        context.TenantId.Returns(Tenant);
        context.UserId.Returns(UserId.From(Guid.Parse("018f4d40-0000-7000-8000-000000000033")));
        return context;
    }
    private static DefaultHttpContext Http(ProtectedAdministrationOperation operation)
    {
        var http = new DefaultHttpContext();
        http.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new IdempotentAttribute(operation)), "component"));
        if (operation != ProtectedAdministrationOperation.CreateOrganization) http.Request.Headers.IfMatch = "\"7\"";
        http.Response.Body = new MemoryStream();
        return http;
    }
    private static object Command(ProtectedAdministrationOperation operation) => operation switch
    {
        ProtectedAdministrationOperation.SetMembershipProfile => new SetMembershipProfileCommand(Target, 7, "Private", null),
        ProtectedAdministrationOperation.SetOwnMembershipProfile => new SetOwnMembershipProfileCommand(7, "Private", null),
        ProtectedAdministrationOperation.ChangeMembershipStatus => new ChangeMembershipStatusCommand(Target, 7, ManagedMembershipStatus.Suspended),
        ProtectedAdministrationOperation.ReplaceMembershipRoleAssignments => new ReplaceMembershipRoleAssignmentsCommand(Target, 7, ImmutableArray<Guid>.Empty),
        ProtectedAdministrationOperation.ChangeRoleGrants => new ChangeRoleGrantsCommand(Target, 7, []),
        ProtectedAdministrationOperation.AdoptRoleGrantManifest => new AdoptRoleGrantManifestCommand(Target, 7, 1, "manifest"),
        ProtectedAdministrationOperation.CreateOrganization => new CreateManagedOrganizationCommand("component", "Private"),
        ProtectedAdministrationOperation.RenameOrganization => new RenameOrganizationCommand(OrganizationId.From(Target), 7, "Private"),
        ProtectedAdministrationOperation.ArchiveOrganization => new ArchiveOrganizationCommand(OrganizationId.From(Target), 7),
        ProtectedAdministrationOperation.SelectDefaultOrganization => new SelectDefaultOrganizationCommand(OrganizationId.From(Target), 7),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };
}
