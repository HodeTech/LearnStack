using LearnStack.Api.Account;
using LearnStack.Api.Authentication;
using LearnStack.Api.Identity;
using LearnStack.Api.Idempotency;
using LearnStack.Api.PublicReads;
using LearnStack.Api.Tenancy;
using LearnStack.Api.Versioning;
using LearnStack.Application.Authorization;
using LearnStack.Modules.Customization.Application.Authorization;
using LearnStack.Modules.Education.Application.Authorization;
using LearnStack.Modules.Education.Application.Contracts.PublicReads;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Contracts.Entry;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.Account;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration.Api;

public sealed class OperationHttpClassificationTests
{
    private sealed record ExpectedAction(Type Controller, string Action, string Name, string Method,
        string Route, Type Request, string Policy, bool Anonymous = false, ProtectedAdministrationOperation? Protected = null);

    // Independent literal admission inventory: never derive expectations from the classifier or descriptors.
    private static readonly ExpectedAction[] ExpectedActions =
    [
        new(typeof(PublicSiteController), "Get", "GetPublicSite", "GET", "public/site", typeof(GetPublicSiteQuery), "tenancy.public_site.read", true),
        new(typeof(PublicSiteController), "Head", "HeadPublicSite", "HEAD", "public/site", typeof(GetPublicSiteQuery), "tenancy.public_site.read", true),
        new(typeof(PublicEducationController), "Catalog", "GetPublicCourses", "GET", "public/courses", typeof(GetPublicCoursesQuery), "education.public_course.list", true),
        new(typeof(PublicEducationController), "HeadCatalog", "HeadPublicCourses", "HEAD", "public/courses", typeof(GetPublicCoursesQuery), "education.public_course.list", true),
        new(typeof(PublicEducationController), "Course", "GetPublicCourse", "GET", "public/courses/{slug}", typeof(GetPublicCourseQuery), "education.public_course.read", true),
        new(typeof(PublicEducationController), "HeadCourse", "HeadPublicCourse", "HEAD", "public/courses/{slug}", typeof(GetPublicCourseQuery), "education.public_course.read", true),
        new(typeof(PublicEducationController), "Lesson", "GetPublicLesson", "GET", "public/courses/{slug}/lessons/{lessonSlug}", typeof(GetPublicLessonQuery), "education.public_lesson.read", true),
        new(typeof(PublicEducationController), "HeadLesson", "HeadPublicLesson", "HEAD", "public/courses/{slug}/lessons/{lessonSlug}", typeof(GetPublicLessonQuery), "education.public_lesson.read", true),
        new(typeof(AccountController), "GetSite", "GetAccountSite", "GET", "account/site", typeof(GetAccountSiteQuery), "tenancy.account_site.read"),
        new(typeof(MembershipsController), "List", "ListMemberships", "GET", "identity/memberships", typeof(ListMembershipsQuery), "identity.membership.list"),
        new(typeof(NativeSessionAdmissionController), "Admit", "AdmitNativeSessionScope", "POST", "identity/session-scope/admissions", typeof(AdmitNativeSessionScopeCommand), "identity.membership.admit_session_scope"),
        new(typeof(MembershipsController), "Get", "GetMembershipDetail", "GET", "identity/memberships/{id}", typeof(GetMembershipDetailQuery), "identity.membership.read"),
        new(typeof(MembershipsController), "SetProfile", "SetMembershipProfile", "PUT", "identity/memberships/{id}/profile", typeof(SetMembershipProfileCommand), "identity.membership_profile.write", Protected: ProtectedAdministrationOperation.SetMembershipProfile),
        new(typeof(MembershipsController), "ReplaceRoles", "ReplaceMembershipRoleAssignments", "PUT", "identity/memberships/{id}/role-assignments", typeof(ReplaceMembershipRoleAssignmentsCommand), "identity.membership.replace_role_assignments", Protected: ProtectedAdministrationOperation.ReplaceMembershipRoleAssignments),
        new(typeof(MembershipsController), "ChangeStatus", "ChangeMembershipStatus", "PUT", "identity/memberships/{id}/status", typeof(ChangeMembershipStatusCommand), "identity.membership.change_status", Protected: ProtectedAdministrationOperation.ChangeMembershipStatus),
        new(typeof(OrganizationMembershipsController), "List", "ListOrganizationMemberships", "GET", "identity/organization-memberships", typeof(ListOrganizationMembershipsQuery), "identity.membership.list_organization"),
        new(typeof(OwnMembershipController), "GetProfile", "GetOwnMembershipProfile", "GET", "identity/me/profile", typeof(GetOwnMembershipProfileQuery), "identity.membership_profile.read_own"),
        new(typeof(OwnMembershipController), "SetProfile", "SetOwnMembershipProfile", "PUT", "identity/me/profile", typeof(SetOwnMembershipProfileCommand), "identity.membership_profile.write_own", Protected: ProtectedAdministrationOperation.SetOwnMembershipProfile),
        new(typeof(OwnMembershipController), "Contexts", "ListOwnMembershipContexts", "GET", "identity/me/contexts", typeof(GetOwnMembershipContextsQuery), "identity.membership.list_own_contexts"),
        new(typeof(OwnMembershipController), "Capabilities", "GetOwnCapabilities", "GET", "identity/me/capabilities", typeof(GetOwnCapabilitiesQuery), "identity.membership.read_own_capabilities"),
        new(typeof(RolesController), "List", "ListRoles", "GET", "identity/roles", typeof(ListRolesQuery), "identity.role.list"),
        new(typeof(RolesController), "Get", "GetRoleDetail", "GET", "identity/roles/{id}", typeof(GetRoleDetailQuery), "identity.role.read"),
        new(typeof(RolesController), "ChangeGrants", "ChangeRoleGrants", "PUT", "identity/roles/{id}/grants", typeof(ChangeRoleGrantsCommand), "identity.role.change_grants", Protected: ProtectedAdministrationOperation.ChangeRoleGrants),
        new(typeof(RolesController), "AdoptManifest", "AdoptRoleGrantManifest", "POST", "identity/roles/{id}/manifest-adoptions", typeof(AdoptRoleGrantManifestCommand), "identity.role.adopt_manifest", Protected: ProtectedAdministrationOperation.AdoptRoleGrantManifest),
        new(typeof(PermissionsController), "List", "GetPermissionRegistry", "GET", "identity/permissions", typeof(GetPermissionRegistryQuery), "identity.role.list_permissions"),
        new(typeof(OrganizationsController), "List", "ListOrganizations", "GET", "tenancy/organizations", typeof(ListOrganizationsQuery), "tenancy.organization.list"),
        new(typeof(OrganizationsController), "Get", "GetOrganizationDetail", "GET", "tenancy/organizations/{id}", typeof(GetOrganizationDetailQuery), "tenancy.organization.read"),
        new(typeof(OrganizationsController), "Create", "CreateManagedOrganization", "POST", "tenancy/organizations", typeof(CreateManagedOrganizationCommand), "tenancy.organization.create", Protected: ProtectedAdministrationOperation.CreateOrganization),
        new(typeof(OrganizationsController), "Rename", "RenameOrganization", "PUT", "tenancy/organizations/{id}", typeof(RenameOrganizationCommand), "tenancy.organization.rename", Protected: ProtectedAdministrationOperation.RenameOrganization),
        new(typeof(OrganizationsController), "Archive", "ArchiveOrganization", "POST", "tenancy/organizations/{id}/archive", typeof(ArchiveOrganizationCommand), "tenancy.organization.archive", Protected: ProtectedAdministrationOperation.ArchiveOrganization),
        new(typeof(DefaultOrganizationController), "Select", "SelectDefaultOrganization", "PUT", "tenancy/default-organization", typeof(SelectDefaultOrganizationCommand), "tenancy.organization.select_default", Protected: ProtectedAdministrationOperation.SelectDefaultOrganization),
    ];

    [Fact]
    public async Task Unresolved_actual_public_operations_preserve_the_generic_refusal_without_authorization_or_dispatch()
    {
        await using var app = await CreateAsync();
        var classifier = new OperationHttpClassifier(Catalog());
        var endpoints = Endpoints(app).Where(endpoint =>
            endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is not null
            && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null).ToArray();
        Assert.Equal(8, endpoints.Length);
        var dispatched = 0;
        var authorizations = 0;
        var middleware = new OperationClassificationMiddleware(_ => { dispatched++; return Task.CompletedTask; });
        foreach (var endpoint in endpoints)
        {
            var context = new DefaultHttpContext { RequestServices = app.Services };
            context.SetEndpoint(endpoint);
            context.Request.Method = Assert.Single(endpoint.Metadata.GetRequiredMetadata<IHttpMethodMetadata>().HttpMethods);
            await using var body = new MemoryStream();
            context.Response.Body = body;
            await middleware.InvokeAsync(context, classifier,
                () => { authorizations++; throw new InvalidOperationException("Unresolved public operations must not resolve an authorization owner."); },
                UnresolvedTenantContext.Instance);
            Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
            Assert.Contains("no-store", context.Response.Headers.CacheControl.ToString(), StringComparison.Ordinal);
            body.Position = 0;
            using var problem = await JsonDocument.ParseAsync(body);
            Assert.Equal("tenant_mismatch", problem.RootElement.GetProperty("code").GetString());
        }
        Assert.Equal(0, authorizations);
        Assert.Equal(0, dispatched);
    }

    [Fact]
    public async Task Every_actual_built_controller_and_infrastructure_endpoint_has_one_closed_classification()
    {
        await using var app = await CreateAsync();
        var endpoints = Endpoints(app);
        var catalog = Catalog();
        var classifier = new OperationHttpClassifier(catalog);
        Assert.Equal(31, ExpectedActions.Length);
        Assert.Equal(10, ExpectedActions.Count(expected => expected.Protected is not null));
        var actualActions = endpoints.Where(endpoint => endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is not null).ToArray();
        Assert.Equal(31, actualActions.Length);
        Assert.Equal(ExpectedActions.Select(expected => expected.Name).Order(StringComparer.Ordinal), actualActions
            .Select(endpoint => endpoint.Metadata.GetRequiredMetadata<IEndpointNameMetadata>().EndpointName).Order(StringComparer.Ordinal));
        foreach (var endpoint in endpoints)
        {
            var classification = classifier.Classify(endpoint, out var policy);
            if (endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action)
            {
                Assert.Equal(HttpOperationClassification.Application, classification);
                Assert.NotNull(policy);
                var expected = Assert.Single(ExpectedActions, candidate => candidate.Name == endpoint.Metadata.GetRequiredMetadata<IEndpointNameMetadata>().EndpointName);
                Assert.Equal(expected.Controller, action.ControllerTypeInfo.AsType());
                Assert.Equal(expected.Controller, action.MethodInfo.DeclaringType);
                Assert.Equal(expected.Action, action.MethodInfo.Name);
                Assert.Equal("api/v1/" + expected.Route, Assert.IsType<RouteEndpoint>(endpoint).RoutePattern.RawText?.TrimStart('/'));
                Assert.Equal(expected.Method, Assert.Single(endpoint.Metadata.GetRequiredMetadata<IHttpMethodMetadata>().HttpMethods));
                Assert.Equal(expected.Request, Assert.Single(endpoint.Metadata.GetOrderedMetadata<OperationPolicyAttribute>()).RequestType);
                Assert.Equal(expected.Request, policy.RequestType);
                Assert.Equal(expected.Policy, policy.Id);
                if (expected.Anonymous)
                {
                    Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>());
                    Assert.Empty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
                }
                else
                {
                    Assert.Empty(endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>());
                    var exactAuthorization = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
                    Assert.Equal("TenantBearer", exactAuthorization.AuthenticationSchemes);
                    Assert.Equal("LearnStackOperation:" + expected.Policy, exactAuthorization.Policy);
                    Assert.True(string.IsNullOrEmpty(exactAuthorization.Roles));
                }
                AssertKeyMetadata(endpoint, action, expected.Protected);
                Assert.Equal(expected.Protected is not null, OperationHttpClassifier.RequiresProtectedAdmission(endpoint));
                Assert.True(catalog.TryGet(policy.RequestType, out var registered));
                Assert.Same(registered, policy);
                var requirement = new OperationPolicyRequirement(policy);
                Assert.Equal(OperationPolicyRequirement.Prefix + policy.Id, requirement.Name);
                Assert.Same(policy, requirement.Policy);
                var stripped = endpoint.Metadata.Where(value => value is not IAuthorizeData and not IAllowAnonymous).ToList();
                Assert.Equal(HttpOperationClassification.Refused, classifier.Classify(new Endpoint(endpoint.RequestDelegate,
                    new EndpointMetadataCollection(stripped), endpoint.DisplayName), out _));
                if (endpoint.Metadata.GetMetadata<IAuthorizeData>() is { } authorization)
                {
                    Assert.Equal(HttpOperationClassification.Refused, classifier.Classify(new Endpoint(endpoint.RequestDelegate,
                        new EndpointMetadataCollection(endpoint.Metadata.Append(authorization)), endpoint.DisplayName), out _));
                    foreach (var changed in new[]
                    {
                        new AuthorizeAttribute { AuthenticationSchemes = authorization.AuthenticationSchemes, Policy = "LearnStackOperation:unregistered.read" },
                        new AuthorizeAttribute { AuthenticationSchemes = authorization.AuthenticationSchemes, Policy = authorization.Policy, Roles = "tenant-admin" },
                    })
                    {
                        var altered = endpoint.Metadata.Where(value => value is not IAuthorizeData).Append(changed);
                        Assert.Equal(HttpOperationClassification.Refused, classifier.Classify(new Endpoint(endpoint.RequestDelegate,
                            new EndpointMetadataCollection(altered), endpoint.DisplayName), out _));
                    }
                }
            }
            else
            {
                Assert.Equal(HttpOperationClassification.Infrastructure, classification);
                Assert.Null(policy);
            }
        }
        Assert.Equal(new[] { InfrastructureOperationKind.Health, InfrastructureOperationKind.OpenApi, InfrastructureOperationKind.Documentation },
            endpoints.Select(endpoint => endpoint.Metadata.GetMetadata<InfrastructureOperationMetadata>())
                .Where(metadata => metadata is not null).Select(metadata => metadata!.Kind).Distinct().Order());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("wrong-request")]
    [InlineData("wrong-name")]
    [InlineData("wrong-method")]
    [InlineData("infrastructure-on-controller")]
    public async Task Actual_endpoint_metadata_twins_refuse_before_a_handler_can_run(string defect)
    {
        await using var app = await CreateAsync();
        var classifier = new OperationHttpClassifier(Catalog());
        foreach (var expected in ExpectedActions)
        {
            var original = Assert.Single(Endpoints(app), endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == expected.Name);
            Assert.Equal(HttpOperationClassification.Application, classifier.Classify(original, out _));
            var metadata = original.Metadata.ToList();
            var declaration = Assert.Single(metadata.OfType<OperationPolicyAttribute>());
            switch (defect)
            {
                case "missing": metadata.Remove(declaration); break;
                case "duplicate": metadata.Add(declaration); break;
                case "wrong-request":
                    metadata.Remove(declaration); metadata.Add(new OperationPolicyAttribute(
                    declaration.RequestType == typeof(GetPublicSiteQuery) ? typeof(GetAccountSiteQuery) : typeof(GetPublicSiteQuery))); break;
                case "wrong-name": metadata.RemoveAll(value => value is IEndpointNameMetadata); metadata.Add(new EndpointNameMetadata("UnclassifiedAction")); break;
                case "wrong-method": metadata.RemoveAll(value => value is IHttpMethodMetadata); metadata.Add(new HttpMethodMetadata([expected.Method == "GET" ? "POST" : "GET"])); break;
                case "infrastructure-on-controller": metadata.Add(InfrastructureOperationMetadata.Health); break;
            }
            var dirty = new Endpoint(original.RequestDelegate, new EndpointMetadataCollection(metadata), original.DisplayName);
            Assert.Equal(HttpOperationClassification.Refused, classifier.Classify(dirty, out var refused));
            Assert.Null(refused);
            Assert.Equal(HttpOperationClassification.Application, classifier.Classify(original, out _));
        }
    }

    [Fact]
    public async Task Matched_undeclared_endpoint_refuses_while_no_match_keeps_routing_ownership()
    {
        var classifier = new OperationHttpClassifier(Catalog());
        Assert.Equal(HttpOperationClassification.Unmatched, classifier.Classify(null, out _));
        var undeclared = new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "Undeclared endpoint");
        Assert.Equal(HttpOperationClassification.Refused, classifier.Classify(undeclared, out _));
        Endpoint? selected = null;
        await using var app = await CreateAsync(context => selected = context.GetEndpoint());
        using var client = app.GetTestClient();
        using var rejected = await client.PostAsync("/api/v1/public/site", content: null);
        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, rejected.StatusCode);
        Assert.NotNull(selected);
        Assert.Equal(HttpOperationClassification.Unmatched, classifier.Classify(selected, out _));
        var lookalike = new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, selected.DisplayName);
        Assert.Equal(HttpOperationClassification.Refused, classifier.Classify(lookalike, out _));
        using var unmatched = await client.GetAsync("/api/v1/unmatched-route-control");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, unmatched.StatusCode);
        Assert.Null(selected);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra-projection")]
    [InlineData("independent-same-operation")]
    [InlineData("wrong-operation")]
    [InlineData("generic")]
    public async Task Actual_protected_MVC_metadata_twins_require_the_same_declared_filter_instance(string defect)
    {
        await using var app = await CreateAsync();
        var endpoints = Endpoints(app);
        var classifier = new OperationHttpClassifier(Catalog());
        var protectedActions = ExpectedActions.Where(expected => expected.Protected is not null).ToArray();
        Assert.Equal(10, protectedActions.Length);
        foreach (var expected in protectedActions)
        {
            var original = Assert.Single(endpoints, endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == expected.Name);
            Assert.Equal(HttpOperationClassification.Application, classifier.Classify(original, out _));
            AssertKeyMetadata(original, original.Metadata.GetRequiredMetadata<ControllerActionDescriptor>(), expected.Protected);
            var metadata = original.Metadata.ToList();
            var projected = metadata.OfType<IdempotentAttribute>().First();
            if (defect == "extra-projection") metadata.Add(projected);
            else
            {
                metadata.RemoveAll(value => value is IdempotentAttribute);
                if (defect != "missing")
                {
                    var wrong = expected.Protected == ProtectedAdministrationOperation.SetMembershipProfile
                        ? ProtectedAdministrationOperation.RenameOrganization : ProtectedAdministrationOperation.SetMembershipProfile;
                    var replacement = defect == "generic" ? new IdempotentAttribute()
                        : new IdempotentAttribute(defect == "wrong-operation" ? wrong : expected.Protected!.Value);
                    // Even two projections of a new otherwise matching attribute do not bind
                    // to the action's actual filter instance and must not gain authority.
                    metadata.Add(replacement); metadata.Add(replacement);
                }
            }
            Assert.Equal(HttpOperationClassification.Refused, classifier.Classify(new Endpoint(original.RequestDelegate,
                new EndpointMetadataCollection(metadata), original.DisplayName), out var refused));
            Assert.Null(refused);
            Assert.Equal(HttpOperationClassification.Application, classifier.Classify(original, out _));
        }
        var unkeyedActions = ExpectedActions.Where(expected => expected.Protected is null).ToArray();
        Assert.Equal(21, unkeyedActions.Length);
        foreach (var expected in unkeyedActions)
        {
            var original = Assert.Single(endpoints, endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == expected.Name);
            var metadata = original.Metadata.Append(new IdempotentAttribute(ProtectedAdministrationOperation.SetMembershipProfile));
            Assert.Equal(HttpOperationClassification.Refused, classifier.Classify(new Endpoint(original.RequestDelegate,
                new EndpointMetadataCollection(metadata), original.DisplayName), out _));
            Assert.Equal(HttpOperationClassification.Application, classifier.Classify(original, out _));
        }
    }

    private static void AssertKeyMetadata(Endpoint endpoint, ControllerActionDescriptor action, ProtectedAdministrationOperation? expected)
    {
        var keyed = endpoint.Metadata.GetOrderedMetadata<IdempotentAttribute>();
        var declarations = action.EndpointMetadata?.OfType<IdempotentAttribute>().ToArray() ?? [];
        var filters = action.FilterDescriptors?.Where(filter => filter.Filter is IdempotentAttribute).ToArray() ?? [];
        if (expected is null)
        {
            Assert.Empty(keyed);
            Assert.Empty(declarations);
            Assert.Empty(filters);
            return;
        }
        var declaration = Assert.Single(declarations);
        var filter = Assert.Single(filters);
        Assert.Equal(expected, declaration.ProtectedOperation);
        Assert.Equal(30, filter.Scope);
        Assert.Equal(0, filter.Order);
        Assert.Same(declaration, filter.Filter);
        Assert.Equal(2, keyed.Count);
        Assert.All(keyed, projection => Assert.Same(declaration, projection));
    }

    private static OperationPolicyCatalog Catalog() => new([
        new TenancyOperationPolicySource(), new CustomizationOperationPolicySource(),
        new EducationOperationPolicySource(), new IdentityOperationPolicySource(), new BackgroundJobOperationPolicySource()]);

    private static Endpoint[] Endpoints(WebApplication app) =>
        [.. ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)];

    private static async Task<WebApplication> CreateAsync(Action<HttpContext>? observe = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ApplicationName = typeof(AccountController).Assembly.GetName().Name,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddLearnStackApiVersioning();
        builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
        {
            manager.ApplicationParts.Clear();
            manager.ApplicationParts.Add(new AssemblyPart(typeof(AccountController).Assembly));
        });
        var app = builder.Build();
        if (observe is not null)
            app.Use((context, next) => { observe(context); return next(context); });
        app.MapLearnStackOpenApi();
        app.MapGet("/healthz", () => Results.Ok()).WithName("HealthCheck").WithMetadata(InfrastructureOperationMetadata.Health);
        app.MapControllers();
        await app.StartAsync();
        return app;
    }
}
