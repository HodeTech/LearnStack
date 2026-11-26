using System.Reflection;
using System.Text;
using LearnStack.Api.Authentication;
using LearnStack.Api.Identity;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Authentication;

/// <summary>
/// ADR-0106 § 9: the retained own data-request entry's closed purposes and strict request capture, modelled on the
/// consent subject's own operation and input tests.
/// </summary>
public sealed class OwnDataRequestEntryInputTests
{
    private const string Organization = "018f4d40-0000-7000-8000-000000000021";
    private const string Request = "018f4d40-0000-7000-8000-000000000031";
    private const string Ticket = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Root = "/api/v1/identity/data-request-entry/";

    [Theory]
    [InlineData("scopes", "GET", nameof(DataRequestEntryController.Scopes))]
    [InlineData("requests", "GET", nameof(DataRequestEntryController.Requests))]
    [InlineData("exports", "POST", nameof(DataRequestEntryController.CreateExport))]
    [InlineData("erasures", "POST", nameof(DataRequestEntryController.CreateErasure))]
    [InlineData("closures", "POST", nameof(DataRequestEntryController.CreateClosure))]
    [InlineData("cancellations", "POST", nameof(DataRequestEntryController.Cancel))]
    [InlineData("revocations", "POST", nameof(DataRequestEntryController.RevokeExport))]
    [InlineData("tickets", "POST", nameof(DataRequestEntryController.MintTicket))]
    [InlineData("chunks", "POST", nameof(DataRequestEntryController.ReadChunk))]
    public void Each_closed_purpose_matches_only_its_exact_path_method_action_and_policy(string segment, string method, string action)
    {
        var operation = OwnDataRequestOperation.ForPath(Root + segment)!;
        Assert.NotNull(operation);
        var http = new DefaultHttpContext();
        http.Request.Path = Root + segment;
        http.Request.Method = method;
        http.SetEndpoint(Endpoint(action, operation.RequestType));
        Assert.True(operation.Matches(http));
        http.Request.Method = method == "GET" ? "POST" : "GET";
        Assert.False(operation.Matches(http));
        http.Request.Method = method;
        var other = action == nameof(DataRequestEntryController.Scopes) ? nameof(DataRequestEntryController.Requests) : nameof(DataRequestEntryController.Scopes);
        http.SetEndpoint(Endpoint(other, operation.RequestType));
        Assert.False(operation.Matches(http), "another action of the same controller is not this purpose");
        http.SetEndpoint(Endpoint(action, typeof(ListOwnDataRequestsQuery)));
        Assert.False(operation.Matches(http), "a live request type is not this purpose");
    }

    [Theory]
    [InlineData("/api/v1/identity/data-request-entry")]
    [InlineData("/api/v1/identity/data-request-entry/")]
    [InlineData("/api/v1/identity/data-request-entry/scopes/")]
    [InlineData("/api/v1/identity/data-request-entry/SCOPES")]
    [InlineData("/api/v1/identity/data-request-entry/artifact/revocations")]
    [InlineData("/api/v1/identity/me/data-requests")]
    public void The_closed_set_names_no_other_path(string path) => Assert.Null(OwnDataRequestOperation.ForPath(path));

    [Theory]
    [InlineData("scopes", "?group=tenant", "")]
    [InlineData("scopes", "?limit=21", "")]
    [InlineData("scopes", "?limit=01", "")]
    [InlineData("scopes", "?cursor=not-a-group", "")]
    [InlineData("requests", "", "")]
    [InlineData("requests", "?group=TENANT", "")]
    [InlineData("requests", "?group=018f4d40-0000-4000-8000-000000000021", "")]
    [InlineData("requests", "?group=tenant&group=tenant", "")]
    [InlineData("requests", "?group=tenant&limit=0", "")]
    [InlineData("requests", "?group=tenant", "{}")]
    [InlineData("exports", "?group=tenant", "{\"x\":1}")]
    [InlineData("exports", "?group=tenant&cursor=a", "{}")]
    [InlineData("erasures", "?group=tenant", "{\"x\":1}")]
    [InlineData("erasures", "", "{}")]
    [InlineData("revocations", "?group=tenant", "{}")]
    [InlineData("tickets", "?group=tenant", "{}")]
    [InlineData("tickets", "?group=tenant", "{\"requestId\":\"" + Request + "\",\"requestId\":\"" + Request + "\"}")]
    [InlineData("chunks", "?group=tenant", "{\"requestId\":\"" + Request + "\",\"ticket\":\"" + Ticket + "\",\"index\":-1}")]
    [InlineData("chunks", "?group=tenant", "{\"requestId\":\"" + Request + "\",\"ticket\":\"" + Ticket + "\",\"index\":1.0}")]
    [InlineData("chunks", "?group=tenant", "{\"requestId\":\"" + Request + "\",\"ticket\":\"" + Ticket + "\"}")]
    public async Task Noncanonical_or_extra_input_is_refused(string segment, string query, string body)
    {
        var http = Http(Root + segment, query, body);
        Assert.Null(await OwnDataRequestHttpInput.ReadAsync(http, OwnDataRequestOperation.ForPath(Root + segment)!));
    }

    [Fact]
    public async Task Group_selects_the_tenant_wide_group_or_one_organization()
    {
        var tenantWide = await OwnDataRequestHttpInput.ReadAsync(Http(Root + "requests", "?group=tenant", ""), OwnDataRequestOperation.ForPath(Root + "requests")!);
        Assert.NotNull(tenantWide);
        Assert.True(tenantWide.SelectsGroup);
        Assert.Null(tenantWide.OrganizationId);
        Assert.Equal(new ListRetainedDataRequestsQuery(null, null, 20), tenantWide.Request);
        var branch = await OwnDataRequestHttpInput.ReadAsync(Http(Root + "requests", "?group=" + Organization + "&limit=5", ""), OwnDataRequestOperation.ForPath(Root + "requests")!);
        Assert.NotNull(branch);
        Assert.Equal(OrganizationId.From(Guid.Parse(Organization)), branch.OrganizationId);
        Assert.Equal(new ListRetainedDataRequestsQuery(OrganizationId.From(Guid.Parse(Organization)), null, 5), branch.Request);
        var discovery = await OwnDataRequestHttpInput.ReadAsync(Http(Root + "scopes", "?cursor=tenant", ""), OwnDataRequestOperation.ForPath(Root + "scopes")!);
        Assert.NotNull(discovery);
        Assert.False(discovery.SelectsGroup);
        Assert.Null(discovery.After!.OrganizationId);
    }

    [Theory]
    [InlineData("exports", true, false)]
    [InlineData("erasures", true, false)]
    [InlineData("cancellations", true, true)]
    [InlineData("revocations", true, true)]
    [InlineData("tickets", false, false)]
    [InlineData("chunks", false, false)]
    public async Task Keys_and_preconditions_reach_only_the_operations_that_take_them(string segment, bool keyed, bool conditional)
    {
        var body = segment switch
        {
            "exports" or "erasures" => "{}",
            "chunks" => "{\"requestId\":\"" + Request + "\",\"ticket\":\"" + Ticket + "\",\"index\":0}",
            _ => "{\"requestId\":\"" + Request + "\"}",
        };
        var operation = OwnDataRequestOperation.ForPath(Root + segment)!;
        var http = Http(Root + segment, "?group=tenant", body);
        if (conditional) http.Request.Headers.IfMatch = "\"3\"";
        Assert.NotNull(await OwnDataRequestHttpInput.ReadAsync(http, operation));
        http = Http(Root + segment, "?group=tenant", body);
        if (conditional) http.Request.Headers.IfMatch = "\"3\"";
        http.Request.Headers["Idempotency-Key"] = "retained-key-1";
        Assert.Equal(keyed, await OwnDataRequestHttpInput.ReadAsync(http, operation) is not null);
        http = Http(Root + segment, "?group=tenant", body);
        if (!conditional) http.Request.Headers.IfMatch = "\"3\"";
        Assert.Null(await OwnDataRequestHttpInput.ReadAsync(http, operation));
        http = Http(Root + segment, "?group=tenant", body);
        if (conditional) http.Request.Headers.IfMatch = "\"3\"";
        http.Request.Headers.IfNoneMatch = "*";
        Assert.Null(await OwnDataRequestHttpInput.ReadAsync(http, operation));
    }

    [Fact]
    public async Task Cancellation_binds_the_precondition_and_the_captured_input_loses_liveness_when_changed()
    {
        var operation = OwnDataRequestOperation.ForPath(Root + "cancellations")!;
        var http = Http(Root + "cancellations", "?group=" + Organization, "{\"requestId\":\"" + Request + "\"}");
        http.Request.Headers.IfMatch = "\"7\"";
        var input = await OwnDataRequestHttpInput.ReadAsync(http, operation);
        Assert.NotNull(input);
        Assert.Equal(new CancelRetainedDataRequestCommand(OrganizationId.From(Guid.Parse(Organization)), Guid.Parse(Request), 7), input.Request);
        Assert.True(input.IsCurrent(http));
        http.Request.Headers.IfMatch = "\"8\"";
        Assert.False(input.IsCurrent(http));
        http.Request.Headers.IfMatch = "\"7\"";
        http.Request.QueryString = new QueryString("?group=tenant");
        Assert.False(input.IsCurrent(http));
    }

    /// <summary>
    /// The creation purpose has three routes; each route's own request type decides what is filed, so neither the erasure
    /// nor the closure route files another kind. The revocation binds the artifact version its If-Match carries.
    /// </summary>
    [Fact]
    public async Task Each_creation_route_files_its_own_kind_and_a_revocation_binds_its_artifact_version()
    {
        var erasure = await OwnDataRequestHttpInput.ReadAsync(Http(Root + "erasures", "?group=" + Organization, "{}"),
            OwnDataRequestOperation.ForPath(Root + "erasures")!);
        Assert.Equal(new CreateRetainedDataErasureCommand(OrganizationId.From(Guid.Parse(Organization))), erasure!.Request);
        var closure = await OwnDataRequestHttpInput.ReadAsync(Http(Root + "closures", "?group=" + Organization, "{}"),
            OwnDataRequestOperation.ForPath(Root + "closures")!);
        Assert.Equal(new CreateRetainedAccountClosureCommand(OrganizationId.From(Guid.Parse(Organization))), closure!.Request);
        Assert.Null(await OwnDataRequestHttpInput.ReadAsync(Http(Root + "closures", "?group=tenant", "{\"requestId\":\"" + Request + "\"}"),
            OwnDataRequestOperation.ForPath(Root + "closures")!));
        var export = await OwnDataRequestHttpInput.ReadAsync(Http(Root + "exports", "?group=tenant", "{}"),
            OwnDataRequestOperation.ForPath(Root + "exports")!);
        Assert.Equal(new CreateRetainedDataExportCommand(null), export!.Request);

        var http = Http(Root + "revocations", "?group=tenant", "{\"requestId\":\"" + Request + "\"}");
        http.Request.Headers.IfMatch = "\"4\"";
        var revocation = await OwnDataRequestHttpInput.ReadAsync(http, OwnDataRequestOperation.ForPath(Root + "revocations")!);
        Assert.Equal(new RevokeRetainedDataExportCommand(null, Guid.Parse(Request), 4), revocation!.Request);
        Assert.True(revocation.IsCurrent(http));
        http.Request.Headers.IfMatch = "\"5\"";
        Assert.False(revocation.IsCurrent(http));
    }

    [Fact]
    public async Task The_chunk_body_is_bounded_at_four_kib()
    {
        var operation = OwnDataRequestOperation.ForPath(Root + "chunks")!;
        var body = "{\"requestId\":\"" + Request + "\",\"ticket\":\"" + Ticket + "\",\"index\":0}";
        var http = Http(Root + "chunks", "?group=tenant", body + new string(' ', 4097 - Encoding.UTF8.GetByteCount(body)));
        var failure = await Assert.ThrowsAsync<BadHttpRequestException>(() => OwnDataRequestHttpInput.ReadAsync(http, operation));
        Assert.Equal(413, failure.StatusCode);
    }

    private static Endpoint Endpoint(string action, Type policy)
    {
        var type = typeof(DataRequestEntryController);
        var descriptor = new ControllerActionDescriptor { ControllerTypeInfo = type.GetTypeInfo(), MethodInfo = type.GetMethod(action)! };
        return new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(descriptor, new OperationPolicyAttribute(policy)), "test");
    }

    private static DefaultHttpContext Http(string path, string query, string body)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.Request.QueryString = new QueryString(query);
        http.Request.ContentType = "application/json";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return http;
    }
}
