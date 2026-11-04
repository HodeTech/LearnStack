using System.Text;
using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Api.Idempotency;
using LearnStack.Api.Identity;
using LearnStack.Api.Tenancy;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Common;

/// <summary>Actual controller parsing/dispatch and lossless projection; live HTTP authority is qualified separately.</summary>
public sealed class AdministrationControllerTests
{
    private static readonly Guid Target = Guid.Parse("018f4d40-0000-7000-8000-000000000011");
    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("018f4d40-0000-7000-8000-000000000022"));

    [Theory]
    [InlineData(ProtectedAdministrationOperation.SetMembershipProfile)]
    [InlineData(ProtectedAdministrationOperation.SetOwnMembershipProfile)]
    [InlineData(ProtectedAdministrationOperation.ChangeMembershipStatus)]
    [InlineData(ProtectedAdministrationOperation.ReplaceMembershipRoleAssignments)]
    [InlineData(ProtectedAdministrationOperation.ChangeRoleGrants)]
    [InlineData(ProtectedAdministrationOperation.AdoptRoleGrantManifest)]
    [InlineData(ProtectedAdministrationOperation.CreateOrganization)]
    [InlineData(ProtectedAdministrationOperation.RenameOrganization)]
    [InlineData(ProtectedAdministrationOperation.ArchiveOrganization)]
    [InlineData(ProtectedAdministrationOperation.SelectDefaultOrganization)]
    public async Task Every_write_dispatches_its_exact_typed_command_and_returns_only_a_string_version_receipt(ProtectedAdministrationOperation operation)
    {
        var sender = Sender();
        var http = Http(operation, Body(operation));
        var result = Assert.IsType<ProtectedAdministrationResult>(await InvokeAsync(operation, http, sender));
        var command = Assert.Single(sender.ReceivedCalls()).GetArguments()[0];
        Assert.NotNull(command);
        Assert.Equal(ProtectedAdministrationDescriptor.For(operation).RequestType, command.GetType());
        Assert.Equal(operation, ProtectedAdministrationDescriptor.OperationFor(command));
        Assert.Equal(operation == ProtectedAdministrationOperation.CreateOrganization ? null : (long?)7,
            ProtectedAdministrationDescriptor.ExpectedVersion(command));
        Assert.Equal(operation == ProtectedAdministrationOperation.SelectDefaultOrganization ? Tenant.Value : Target, result.Target.Id);
        await result.ExecuteResultAsync(new ActionContext { HttpContext = http });
        Assert.Equal("{\"version\":\"9223372036854775807\"}", Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray()));
        Assert.Equal("\"9223372036854775807\"", http.Response.Headers.ETag.ToString());
        Assert.Equal(operation == ProtectedAdministrationOperation.CreateOrganization ? 201 : 200, http.Response.StatusCode);
    }

    [Theory]
    [InlineData(ProtectedAdministrationOperation.SetMembershipProfile, "{\"displayName\":null}")]
    [InlineData(ProtectedAdministrationOperation.SetOwnMembershipProfile, "{\"displayName\":null,\"displayName\":null,\"preferredLocale\":null}")]
    [InlineData(ProtectedAdministrationOperation.ChangeMembershipStatus, "{\"status\":1}")]
    [InlineData(ProtectedAdministrationOperation.ChangeMembershipStatus, "{\"status\":\"active\"}")]
    [InlineData(ProtectedAdministrationOperation.ReplaceMembershipRoleAssignments, "{\"roleIds\":null}")]
    [InlineData(ProtectedAdministrationOperation.ChangeRoleGrants, "{\"grants\":[{\"scope\":3,\"key\":\"identity.role.admin\"}]}")]
    [InlineData(ProtectedAdministrationOperation.AdoptRoleGrantManifest, "{\"manifestVersion\":2.0,\"manifestFingerprint\":\"bad\"}")]
    [InlineData(ProtectedAdministrationOperation.CreateOrganization, "{\"slug\":\"branch\",\"displayName\":\"Name\",\"organizationId\":\"forged\"}")]
    [InlineData(ProtectedAdministrationOperation.RenameOrganization, "{\"displayName\":\"Name\",\"status\":\"Active\"}")]
    [InlineData(ProtectedAdministrationOperation.ArchiveOrganization, "{\"status\":\"Archived\"}")]
    [InlineData(ProtectedAdministrationOperation.SelectDefaultOrganization, "{\"organizationId\":\"018F4D40-0000-7000-8000-000000000011\"}")]
    public async Task Invalid_closed_input_never_dispatches(ProtectedAdministrationOperation operation, string body)
    {
        var sender = Sender();
        var result = Assert.IsType<ProblemDetailsActionResult>(await InvokeAsync(operation, Http(operation, body), sender));
        Assert.Equal("validation_failed", result.Error.Code);
        Assert.Empty(sender.ReceivedCalls());
    }

    [Fact]
    public async Task New_member_and_organization_reads_preserve_Int64_versions_as_strings_with_exact_ETags()
    {
        var sender = Sender();
        var actor = UserId.From(Guid.Parse("018f4d40-0000-7000-8000-000000000033"));
        sender.Send(Arg.Any<GetMembershipDetailQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Ok(
            new MembershipDetailDto(Target, actor, null, ManagedMembershipStatus.Active, "Private", null, [], long.MaxValue)));
        sender.Send(Arg.Any<GetOwnMembershipProfileQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Ok(
            new OwnMembershipProfileDto(null, "tr", long.MaxValue)));
        sender.Send(Arg.Any<GetOrganizationDetailQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Ok(
            new OrganizationDetailDto(OrganizationId.From(Target), "branch", "Name", ManagedOrganizationStatus.Active, true, long.MaxValue)));
        var context = Context();
        var member = new MembershipsController(sender, context) { ControllerContext = Controller(Http()) };
        var memberResult = Assert.IsType<OkObjectResult>(await member.Get(Target.ToString("D"), CancellationToken.None));
        Assert.Equal("9223372036854775807", Assert.IsType<MembershipDetailResponse>(memberResult.Value).Version);
        Assert.Equal("\"9223372036854775807\"", member.Response.Headers.ETag.ToString());
        var own = new OwnMembershipController(sender, context, Origins()) { ControllerContext = Controller(Http()) };
        var ownResult = Assert.IsType<OkObjectResult>(await own.GetProfile(CancellationToken.None));
        Assert.Equal("9223372036854775807", Assert.IsType<OwnMembershipProfileResponse>(ownResult.Value).Version);
        var organization = new OrganizationsController(sender, context) { ControllerContext = Controller(Http()) };
        var organizationResult = Assert.IsType<OkObjectResult>(await organization.Get(Target.ToString("D"), CancellationToken.None));
        Assert.Equal("9223372036854775807", Assert.IsType<OrganizationResponse>(organizationResult.Value).Version);
        Assert.Equal("\"9223372036854775807\"", organization.Response.Headers.ETag.ToString());
    }

    [Theory]
    [InlineData("body")]
    [InlineData("query")]
    [InlineData("uppercase")]
    public async Task Detail_reads_reject_body_query_and_noncanonical_target_before_dispatch(string defect)
    {
        var sender = Sender();
        var http = Http();
        if (defect == "body") http.Request.Body = new MemoryStream("{}"u8.ToArray());
        if (defect == "query") http.Request.QueryString = new QueryString("?tenantId=foreign");
        var controller = new MembershipsController(sender, Context()) { ControllerContext = Controller(http) };
        var id = defect == "uppercase" ? Target.ToString("D").ToUpperInvariant() : Target.ToString("D");
        Assert.IsType<ProblemDetailsActionResult>(await controller.Get(id, CancellationToken.None));
        Assert.Empty(sender.ReceivedCalls());
    }

    [Theory]
    [InlineData("tenant", true, true)]
    [InlineData("branch", true, false)]
    [InlineData("other-tenant", false, false)]
    [InlineData("platform", false, false)]
    public async Task Own_context_navigation_uses_verified_entry_origin_and_actual_host_ceiling(
        string hostKind, bool branchUsable, bool tenantUsable)
    {
        var branch = OrganizationId.From(Target);
        var sender = Sender();
        sender.Send(Arg.Any<GetOwnMembershipContextsQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Ok(
            new Page<OwnMembershipContextDto>([
                new(Tenant, branch, "Private tenant", "Private branch", "branch.example.test"),
                new(Tenant, null, "Private tenant", null, "tenant.example.test")], new PageInfo(null, null, false, false))));
        var http = Http();
        var host = hostKind switch
        {
            "tenant" => HostClassification.ForResolution("tenant.example.test", new HostResolution(Tenant, null)),
            "branch" => HostClassification.ForResolution("branch.example.test", new HostResolution(Tenant, branch)),
            "other-tenant" => HostClassification.ForResolution("other.example.test", new HostResolution(TenantId.From(Guid.CreateVersion7()), null)),
            _ => HostClassification.Platform("platform.example.test"),
        };
        http.Features.Set(host);
        var controller = new OwnMembershipController(sender, Context(), Origins("8443")) { ControllerContext = Controller(http) };
        var result = Assert.IsType<OwnMembershipContextPage>(Assert.IsType<OkObjectResult>(
            await controller.Contexts(CancellationToken.None)).Value);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal("https://branch.example.test:8443", result.Items[0].EntryOrigin);
        Assert.Equal("https://tenant.example.test:8443", result.Items[1].EntryOrigin);
        Assert.Equal(branchUsable, result.Items[0].CanUseCurrentHost);
        Assert.Equal(tenantUsable, result.Items[1].CanUseCurrentHost);
        Assert.Null(result.Items[1].OrganizationName);
    }

    private static Task<IActionResult> InvokeAsync(ProtectedAdministrationOperation operation, HttpContext http, ISender sender)
    {
        var member = new MembershipsController(sender, Context()) { ControllerContext = Controller(http) };
        var own = new OwnMembershipController(sender, Context(), Origins()) { ControllerContext = Controller(http) };
        var role = new RolesController(sender, Context()) { ControllerContext = Controller(http) };
        var organization = new OrganizationsController(sender, Context()) { ControllerContext = Controller(http) };
        var select = new DefaultOrganizationController(sender, Context()) { ControllerContext = Controller(http) };
        return operation switch
        {
            ProtectedAdministrationOperation.SetMembershipProfile => member.SetProfile(Target.ToString("D"), CancellationToken.None),
            ProtectedAdministrationOperation.SetOwnMembershipProfile => own.SetProfile(CancellationToken.None),
            ProtectedAdministrationOperation.ChangeMembershipStatus => member.ChangeStatus(Target.ToString("D"), CancellationToken.None),
            ProtectedAdministrationOperation.ReplaceMembershipRoleAssignments => member.ReplaceRoles(Target.ToString("D"), CancellationToken.None),
            ProtectedAdministrationOperation.ChangeRoleGrants => role.ChangeGrants(Target.ToString("D"), CancellationToken.None),
            ProtectedAdministrationOperation.AdoptRoleGrantManifest => role.AdoptManifest(Target.ToString("D"), CancellationToken.None),
            ProtectedAdministrationOperation.CreateOrganization => organization.Create(CancellationToken.None),
            ProtectedAdministrationOperation.RenameOrganization => organization.Rename(Target.ToString("D"), CancellationToken.None),
            ProtectedAdministrationOperation.ArchiveOrganization => organization.Archive(Target.ToString("D"), CancellationToken.None),
            ProtectedAdministrationOperation.SelectDefaultOrganization => select.Select(CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    private static string Body(ProtectedAdministrationOperation operation) => operation switch
    {
        ProtectedAdministrationOperation.SetMembershipProfile or ProtectedAdministrationOperation.SetOwnMembershipProfile => "{\"displayName\":null,\"preferredLocale\":null}",
        ProtectedAdministrationOperation.ChangeMembershipStatus => "{\"status\":\"Suspended\"}",
        ProtectedAdministrationOperation.ReplaceMembershipRoleAssignments => "{\"roleIds\":[]}",
        ProtectedAdministrationOperation.ChangeRoleGrants => "{\"grants\":[{\"scope\":\"Tenant\",\"key\":\"identity.role.admin\"}]}",
        ProtectedAdministrationOperation.AdoptRoleGrantManifest => "{\"manifestVersion\":2,\"manifestFingerprint\":\"" + new string('a', 64) + "\"}",
        ProtectedAdministrationOperation.CreateOrganization => "{\"slug\":\"branch\",\"displayName\":\"Name\"}",
        ProtectedAdministrationOperation.RenameOrganization => "{\"displayName\":\"Name\"}",
        ProtectedAdministrationOperation.ArchiveOrganization => "{}",
        ProtectedAdministrationOperation.SelectDefaultOrganization => "{\"organizationId\":\"" + Target.ToString("D") + "\"}",
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static DefaultHttpContext Http(ProtectedAdministrationOperation? operation = null, string body = "")
    {
        var http = new DefaultHttpContext();
        http.Request.ContentType = "application/json";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        http.Response.Body = new MemoryStream();
        if (operation is { } value)
        {
            http.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new IdempotentAttribute(value)), "controller-component"));
            if (value != ProtectedAdministrationOperation.CreateOrganization) http.Request.Headers.IfMatch = "\"7\"";
        }
        return http;
    }
    private static NativeEntryOriginOptions Origins(string port = "443") => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Identity:NativeSession:HttpsPort"] = port }).Build());
    private static ControllerContext Controller(HttpContext http) => new() { HttpContext = http };
    private static ITenantContext Context()
    {
        var context = Substitute.For<ITenantContext>();
        context.IsResolved.Returns(true);
        context.TenantId.Returns(Tenant);
        context.UserId.Returns(UserId.From(Guid.Parse("018f4d40-0000-7000-8000-000000000033")));
        return context;
    }
    private static ISender Sender()
    {
        var sender = Substitute.For<ISender>();
        sender.Send(Arg.Any<IRequest<Result<MembershipMutationResult>>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(new MembershipMutationResult(Target, long.MaxValue)));
        sender.Send(Arg.Any<IRequest<Result<OrganizationMutationResult>>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(new OrganizationMutationResult(OrganizationId.From(Target), long.MaxValue)));
        sender.Send(Arg.Any<IRequest<Result<long>>>(), Arg.Any<CancellationToken>()).Returns(Result.Ok(long.MaxValue));
        sender.ClearReceivedCalls();
        return sender;
    }
}
