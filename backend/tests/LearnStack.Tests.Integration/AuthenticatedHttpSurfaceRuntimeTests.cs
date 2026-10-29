using System.Reflection;
using FluentAssertions;
using LearnStack.Api.Account;
using LearnStack.Api.PublicReads;
using LearnStack.Api.Identity;
using LearnStack.Api.Idempotency;
using LearnStack.Api.Pagination;
using LearnStack.Api.Tenancy;
using LearnStack.Modules.Education.Application.Contracts.PublicReads;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Entry;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Tenancy.Application.Contracts.Account;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>ADR-0064's real endpoint union and the request each discovered MVC action actually sends.</summary>
[Collection(TenantAuthenticationDiagnosticsTestGroup.Name)]
public sealed partial class AuthenticatedHttpSurfaceRuntimeTests
{
    private sealed record SurfaceAction(string Path, string Verb, Type Controller, string Action, Type Request,
        Type Response, string? Policy = null, ProtectedAdministrationOperation? Protected = null);

    // This literal wire/caller inventory is independent of production classifiers and descriptors.
    private static readonly SurfaceAction[] ApprovedActions =
    [
        new("public/site", "GET", typeof(PublicSiteController), "Get", typeof(GetPublicSiteQuery), typeof(PublicSite)),
        new("public/site", "HEAD", typeof(PublicSiteController), "Head", typeof(GetPublicSiteQuery), typeof(void)),
        new("public/courses", "GET", typeof(PublicEducationController), "Catalog", typeof(GetPublicCoursesQuery), typeof(PublicCourseCatalog)),
        new("public/courses", "HEAD", typeof(PublicEducationController), "HeadCatalog", typeof(GetPublicCoursesQuery), typeof(void)),
        new("public/courses/{slug}", "GET", typeof(PublicEducationController), "Course", typeof(GetPublicCourseQuery), typeof(PublicCourseDetail)),
        new("public/courses/{slug}", "HEAD", typeof(PublicEducationController), "HeadCourse", typeof(GetPublicCourseQuery), typeof(void)),
        new("public/courses/{slug}/lessons/{lessonSlug}", "GET", typeof(PublicEducationController), "Lesson", typeof(GetPublicLessonQuery), typeof(PublicLessonDetail)),
        new("public/courses/{slug}/lessons/{lessonSlug}", "HEAD", typeof(PublicEducationController), "HeadLesson", typeof(GetPublicLessonQuery), typeof(void)),
        new("account/site", "GET", typeof(AccountController), "GetSite", typeof(GetAccountSiteQuery), typeof(AccountSite), "tenancy.account_site.read"),
        new("identity/memberships", "GET", typeof(MembershipsController), "List", typeof(ListMembershipsQuery), typeof(Page<MembershipDirectoryItem>), "identity.membership.list"),
        new("identity/session-scope/admissions", "POST", typeof(NativeSessionAdmissionController), "Admit", typeof(AdmitNativeSessionScopeCommand), typeof(NativeSessionScopeAdmission), "identity.membership.admit_session_scope"),
        new("identity/memberships/{id}", "GET", typeof(MembershipsController), "Get", typeof(GetMembershipDetailQuery), typeof(MembershipDetailResponse), "identity.membership.read"),
        new("identity/memberships/{id}/profile", "PUT", typeof(MembershipsController), "SetProfile", typeof(SetMembershipProfileCommand), typeof(AdministrationVersionReceipt), "identity.membership_profile.write", ProtectedAdministrationOperation.SetMembershipProfile),
        new("identity/memberships/{id}/role-assignments", "PUT", typeof(MembershipsController), "ReplaceRoles", typeof(ReplaceMembershipRoleAssignmentsCommand), typeof(AdministrationVersionReceipt), "identity.membership.replace_role_assignments", ProtectedAdministrationOperation.ReplaceMembershipRoleAssignments),
        new("identity/memberships/{id}/status", "PUT", typeof(MembershipsController), "ChangeStatus", typeof(ChangeMembershipStatusCommand), typeof(AdministrationVersionReceipt), "identity.membership.change_status", ProtectedAdministrationOperation.ChangeMembershipStatus),
        new("identity/organization-memberships", "GET", typeof(OrganizationMembershipsController), "List", typeof(ListOrganizationMembershipsQuery), typeof(Page<MembershipDirectoryItem>), "identity.membership.list_organization"),
        new("identity/me/profile", "GET", typeof(OwnMembershipController), "GetProfile", typeof(GetOwnMembershipProfileQuery), typeof(OwnMembershipProfileResponse), "identity.membership_profile.read_own"),
        new("identity/me/profile", "PUT", typeof(OwnMembershipController), "SetProfile", typeof(SetOwnMembershipProfileCommand), typeof(AdministrationVersionReceipt), "identity.membership_profile.write_own", ProtectedAdministrationOperation.SetOwnMembershipProfile),
        new("identity/me/contexts", "GET", typeof(OwnMembershipController), "Contexts", typeof(GetOwnMembershipContextsQuery), typeof(OwnMembershipContextPage), "identity.membership.list_own_contexts"),
        new("identity/me/capabilities", "GET", typeof(OwnMembershipController), "Capabilities", typeof(GetOwnCapabilitiesQuery), typeof(OwnCapabilitiesDto), "identity.membership.read_own_capabilities"),
        new("identity/roles", "GET", typeof(RolesController), "List", typeof(ListRolesQuery), typeof(RoleDirectoryResponse), "identity.role.list"),
        new("identity/roles/{id}", "GET", typeof(RolesController), "Get", typeof(GetRoleDetailQuery), typeof(RoleDetailResponse), "identity.role.read"),
        new("identity/roles/{id}/grants", "PUT", typeof(RolesController), "ChangeGrants", typeof(ChangeRoleGrantsCommand), typeof(AdministrationVersionReceipt), "identity.role.change_grants", ProtectedAdministrationOperation.ChangeRoleGrants),
        new("identity/roles/{id}/manifest-adoptions", "POST", typeof(RolesController), "AdoptManifest", typeof(AdoptRoleGrantManifestCommand), typeof(AdministrationVersionReceipt), "identity.role.adopt_manifest", ProtectedAdministrationOperation.AdoptRoleGrantManifest),
        new("identity/permissions", "GET", typeof(PermissionsController), "List", typeof(GetPermissionRegistryQuery), typeof(PermissionRegistryResponse), "identity.role.list_permissions"),
        new("tenancy/organizations", "GET", typeof(OrganizationsController), "List", typeof(ListOrganizationsQuery), typeof(OrganizationDirectoryResponse), "tenancy.organization.list"),
        new("tenancy/organizations/{id}", "GET", typeof(OrganizationsController), "Get", typeof(GetOrganizationDetailQuery), typeof(OrganizationResponse), "tenancy.organization.read"),
        new("tenancy/organizations", "POST", typeof(OrganizationsController), "Create", typeof(CreateManagedOrganizationCommand), typeof(AdministrationVersionReceipt), "tenancy.organization.create", ProtectedAdministrationOperation.CreateOrganization),
        new("tenancy/organizations/{id}", "PUT", typeof(OrganizationsController), "Rename", typeof(RenameOrganizationCommand), typeof(AdministrationVersionReceipt), "tenancy.organization.rename", ProtectedAdministrationOperation.RenameOrganization),
        new("tenancy/organizations/{id}/archive", "POST", typeof(OrganizationsController), "Archive", typeof(ArchiveOrganizationCommand), typeof(AdministrationVersionReceipt), "tenancy.organization.archive", ProtectedAdministrationOperation.ArchiveOrganization),
        new("tenancy/default-organization", "PUT", typeof(DefaultOrganizationController), "Select", typeof(SelectDefaultOrganizationCommand), typeof(AdministrationVersionReceipt), "tenancy.organization.select_default", ProtectedAdministrationOperation.SelectDefaultOrganization),
    ];
    private static readonly Type[] ContextControllers =
        [typeof(MembershipsController), typeof(OwnMembershipController), typeof(RolesController), typeof(OrganizationsController), typeof(DefaultOrganizationController)];
    private const string CensusTarget = "018f4d40-0000-7000-8000-000000000011";

    private static SurfaceAction Approved(ControllerActionDescriptor action) => ApprovedActions.Single(expected =>
        expected.Controller == action.ControllerTypeInfo.AsType() && expected.Action == action.MethodInfo.Name);

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Production_endpoint_union_has_exact_patterns_methods_handlers_and_requests(string environment)
    {
        await using var factory = new SurfaceFactory(environment);
        using var client = factory.CreateClient();
        AssertGlobalFilterControls(factory.Services.GetRequiredService<IOptions<MvcOptions>>().Value);
        AssertFilterProviders(factory.Services.GetServices<IFilterProvider>());
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;
        endpoints.Should().OnlyContain(endpoint => endpoint is RouteEndpoint,
            "non-route endpoints cannot disappear from the closed production census");
        var actual = new List<string>();
        foreach (var endpoint in endpoints.Cast<RouteEndpoint>())
        {
            var action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
            var method = action?.MethodInfo ?? endpoint.Metadata.GetMetadata<MethodInfo>();
            method.Should().NotBeNull("every admitted endpoint has an explicitly enumerated handler");
            Type? request = null;
            if (action is not null)
            {
                AssertActionFilterControls(action);
                AssertEndpointAuthorizationControls(action, endpoint.Metadata);
                request = await DispatchedRequest(action, endpoint);
            }
            actual.Add((endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/') + "|"
                + string.Join(",", endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []) + "|"
                + method!.DeclaringType!.FullName + "." + method.Name + "|" + (request?.FullName ?? "infrastructure"));
        }
        AssertUnion(actual);
        Action missing = () => AssertUnion(actual.Skip(1));
        missing.Should().Throw<Xunit.Sdk.XunitException>("an incomplete data source is not a clean census");
        Action extra = () => AssertUnion(actual.Append("extra|GET|Hidden.Handler|infrastructure"));
        extra.Should().Throw<Xunit.Sdk.XunitException>("an added runtime endpoint must fail the same union assertion");
        Action wrongRequest = () => AssertUnion(actual.Select(entry => entry.Replace(typeof(GetAccountSiteQuery).FullName!,
            typeof(GetPublicSiteQuery).FullName!, StringComparison.Ordinal)));
        wrongRequest.Should().Throw<Xunit.Sdk.XunitException>("an approved action may not dispatch another approved query");
        Action wrongAdmissionRequest = () => AssertUnion(actual.Select(entry => entry.Replace(typeof(AdmitNativeSessionScopeCommand).FullName!,
            typeof(ListMembershipsQuery).FullName!, StringComparison.Ordinal)));
        wrongAdmissionRequest.Should().Throw<Xunit.Sdk.XunitException>("native admission cannot substitute a Membership directory request");
        Action wrongAdmissionMethod = () => AssertUnion(actual.Select(entry => entry.Replace("identity/session-scope/admissions|POST|",
            "identity/session-scope/admissions|GET|", StringComparison.Ordinal)));
        wrongAdmissionMethod.Should().Throw<Xunit.Sdk.XunitException>("native admission remains the exact POST route");

        await using var dirtyFactory = new SurfaceFactory(environment, addFilterProvider: true);
        using var dirtyClient = dirtyFactory.CreateClient();
        AssertGlobalFilters(dirtyFactory.Services.GetRequiredService<IOptions<MvcOptions>>().Value.Filters);
        var dirtyProviders = dirtyFactory.Services.GetServices<IFilterProvider>().ToArray();
        Action providerInventory = () => AssertFilterProviders(dirtyProviders);
        providerInventory.Should().Throw<Xunit.Sdk.XunitException>(
            "a real DI provider registration can replace effective filters while MvcOptions.Filters stays unchanged");
        var filter = new AllowAnonymousFilter();
        var context = new FilterProviderContext(new ActionContext(),
            [new FilterItem(new FilterDescriptor(filter, 0), filter)]);
        dirtyProviders.OfType<UnexpectedFilterProvider>().Single().OnProvidersExecuting(context);
        context.Results[0].Filter.Should().BeOfType<UnexpectedAuthorizationFilter>();
        AssertFilterProviders(factory.Services.GetServices<IFilterProvider>());
    }

    private static void AssertEndpointAuthorizationControls(ControllerActionDescriptor action, EndpointMetadataCollection metadata)
    {
        AssertEndpointAuthorization(action, metadata);
        Action extra = () => AssertEndpointAuthorization(action, metadata.Append(new UnexpectedAnonymousAttribute()));
        extra.Should().Throw<Xunit.Sdk.XunitException>("custom IAllowAnonymous metadata cannot augment any approved MVC endpoint");
        if (Approved(action).Policy is null)
        {
            Action substitute = () => AssertEndpointAuthorization(action,
                metadata.Where(item => item is not IAllowAnonymous).Append(new UnexpectedAnonymousAttribute()));
            substitute.Should().Throw<Xunit.Sdk.XunitException>("a public endpoint requires the exact standard AllowAnonymousAttribute");
        }
        else
        {
            var authorization = metadata.OfType<IAuthorizeData>().Single();
            foreach (var policy in new[] { null, "LearnStackOperation:unregistered.read" })
            {
                var changed = new AuthorizeAttribute { AuthenticationSchemes = authorization.AuthenticationSchemes, Policy = policy };
                Action substitute = () => AssertEndpointAuthorization(action,
                    metadata.Where(item => !ReferenceEquals(item, authorization)).Append(changed));
                substitute.Should().Throw<Xunit.Sdk.XunitException>("the exact named live operation policy is required on the built endpoint");
            }
        }
        AssertEndpointAuthorization(action, metadata);
    }

    private static void AssertEndpointAuthorization(ControllerActionDescriptor action, IEnumerable<object> metadata)
    {
        var items = metadata.ToArray();
        var anonymous = items.OfType<IAllowAnonymous>().ToArray();
        var authorizations = items.OfType<IAuthorizeData>().ToArray();
        items.OfType<AuthorizationPolicy>().Should().BeEmpty("explicit policies are a separate framework authorization channel");
        items.OfType<IAuthorizationRequirementData>().Should().BeEmpty("requirement metadata must not augment the approved authorization contract");
        var expected = Approved(action);
        if (expected.Policy is not null)
        {
            anonymous.Should().BeEmpty("authenticated endpoint metadata must never bypass authorization");
            var authorization = authorizations.Should().ContainSingle().Which.Should().BeOfType<AuthorizeAttribute>().Subject;
            authorization.AuthenticationSchemes.Should().Be("TenantBearer");
            authorization.Roles.Should().BeNull();
            authorization.Policy.Should().Be("LearnStackOperation:" + expected.Policy);
        }
        else
        {
            authorizations.Should().BeEmpty("public endpoints have no authorization metadata");
            anonymous.Should().ContainSingle().Which.Should().BeOfType<AllowAnonymousAttribute>();
        }
        var keyed = items.OfType<IdempotentAttribute>().ToArray();
        var declarations = action.EndpointMetadata?.OfType<IdempotentAttribute>().ToArray() ?? [];
        var filters = action.FilterDescriptors.Where(descriptor => descriptor.Filter is IdempotentAttribute).ToArray();
        if (expected.Protected is { } operation)
        {
            var declaration = declarations.Should().ContainSingle().Which;
            declaration.ProtectedOperation.Should().Be(operation);
            filters.Should().ContainSingle().Which.Filter.Should().BeSameAs(declaration);
            keyed.Should().HaveCount(2, "MVC projects one action filter through both metadata sources");
            keyed.Should().OnlyContain(value => ReferenceEquals(value, declaration));
        }
        else
        {
            keyed.Should().BeEmpty();
            declarations.Should().BeEmpty();
            filters.Should().BeEmpty();
        }
    }

    private static void AssertActionInventory(IEnumerable<ControllerActionDescriptor> actions)
    {
        var actual = actions.ToArray();
        actual.Should().HaveCount(31);
        actual.Select(action => action.ControllerTypeInfo.FullName + "." + action.MethodInfo.Name).Order(StringComparer.Ordinal)
            .Should().Equal(ApprovedActions.Select(expected => expected.Controller.FullName + "." + expected.Action).Order(StringComparer.Ordinal),
                "every convention must reach exactly the approved controller/action multiset");
    }

    private static void AssertFilterProviders(IEnumerable<IFilterProvider> providers) =>
        providers.Select(provider => (Assembly: provider.GetType().Assembly.GetName().Name,
            Type: provider.GetType().FullName, provider.Order)).Should().Equal(
            [("Microsoft.AspNetCore.Mvc.Core", "Microsoft.AspNetCore.Mvc.Filters.DefaultFilterProvider", -1000)],
            "Fix: preserve the exact framework MVC filter provider inventory and order");

    private static void AssertGlobalFilterControls(MvcOptions options)
    {
        AssertGlobalFilters(options.Filters);
        var original = options.Filters.ToArray();
        foreach (var mutate in new Action<FilterCollection>[]
        {
            filters => filters.Add(new AllowAnonymousFilter()),
            filters => filters.Add(new UnexpectedAuthorizationFilter()),
            filters => filters.RemoveAt(0),
            filters => filters.RemoveAt(filters.Count - 1),
            filters => filters.Add(original[0]),
            filters => filters.Add(new TypeFilterAttribute(typeof(UnexpectedAuthorizationFilter))),
        })
        {
            try
            {
                mutate(options.Filters);
                Action assert = () => AssertGlobalFilters(options.Filters);
                assert.Should().Throw<Xunit.Sdk.XunitException>("the actual global MVC filters must not change outside the closed surface");
            }
            finally
            {
                options.Filters.Clear();
                foreach (var filter in original) options.Filters.Add(filter);
            }
        }
        var normalizer = options.Filters.OfType<TypeFilterAttribute>().Single();
        var order = normalizer.Order;
        normalizer.Order = 100;
        try
        {
            Action assert = () => AssertGlobalFilters(options.Filters);
            assert.Should().Throw<Xunit.Sdk.XunitException>("filter factory order is part of the real composition");
        }
        finally { normalizer.Order = order; }
        AssertGlobalFilters(options.Filters);
    }

    private static void AssertGlobalFilters(IEnumerable<IFilterMetadata> filters)
    {
        var actual = filters.Select(filter => (Kind: filter.GetType().FullName,
            Implementation: filter is TypeFilterAttribute factory ? factory.ImplementationType.FullName : null,
            Order: filter is IOrderedFilter ordered ? ordered.Order : 0,
            Reusable: filter is IFilterFactory provider && provider.IsReusable,
            Arguments: filter is TypeFilterAttribute typed ? typed.Arguments?.Length ?? 0 : 0)).ToArray();
        actual.Should().Equal(
        [
            ("Microsoft.AspNetCore.Mvc.ModelBinding.UnsupportedContentTypeFilter", null, -3000, false, 0),
            ("Microsoft.AspNetCore.Mvc.TypeFilterAttribute", "LearnStack.Api.Common.ProblemDetailsNormalizationFilter", 0, false, 0),
        ], "Fix: preserve the exact framework and application global MVC filters; authorization cannot change outside the enumerated endpoint contract");
    }

    private sealed class UnexpectedAuthorizationFilter : IAuthorizationFilter
    {
        public void OnAuthorization(AuthorizationFilterContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            context.Result = new UnauthorizedResult();
        }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    private sealed class UnexpectedAnonymousAttribute : Attribute, IAllowAnonymous;

    private sealed class UnexpectedFilterProvider : IFilterProvider
    {
        public int Order => int.MaxValue;

        public void OnProvidersExecuting(FilterProviderContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            context.Results[0].Filter = new UnexpectedAuthorizationFilter();
        }

        public void OnProvidersExecuted(FilterProviderContext context) => ArgumentNullException.ThrowIfNull(context);
    }

    private static async Task<Type> DispatchedRequest(ControllerActionDescriptor action, RouteEndpoint endpoint)
    {
        var expected = Approved(action);
        var sender = DispatchProxy.Create<ISender, RequestObservation>();
        var observation = (RequestObservation)sender;
        var controllerType = action.ControllerTypeInfo.AsType();
        object[] constructor = ContextControllers.Contains(controllerType) ? [sender, new CensusTenantContext()] : [sender];
        var controller = (ControllerBase)Activator.CreateInstance(controllerType, constructor)!;
        var context = new DefaultHttpContext();
        context.SetEndpoint(endpoint);
        context.Features.Set(HostClassification.Platform("census.invalid"));
        context.Request.Method = expected.Verb;
        context.Request.Path = "/api/v1/" + expected.Path.Replace("{id}", CensusTarget, StringComparison.Ordinal);
        var body = expected.Protected is { } operation ? CensusBody(operation)
            : controllerType == typeof(NativeSessionAdmissionController) ? "{}" : string.Empty;
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
        context.Request.Body = input;
        context.Request.ContentLength = input.Length;
        if (body.Length != 0) context.Request.ContentType = "application/json";
        if (expected.Protected is not null)
        {
            // This census stops at Send: valid closed input and the actual built endpoint
            // let it observe the typed carrier without claiming a live authorization proof.
            context.Request.Headers["Idempotency-Key"] = "census-command-key";
            if (expected.Protected != ProtectedAdministrationOperation.CreateOrganization) context.Request.Headers.IfMatch = "\"7\"";
        }
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        var arguments = action.MethodInfo.GetParameters().Select(parameter => parameter.ParameterType switch
        {
            var type when type == typeof(CancellationToken) => (object)CancellationToken.None,
            var type when type == typeof(CursorPaginationRequest) => new CursorPaginationRequest(),
            var type when type == typeof(string) => parameter.Name == "id" ? CensusTarget : "census-slug",
            _ => throw new InvalidOperationException("Unclassified action parameter"),
        }).ToArray();
        var execution = (Task)action.MethodInfo.Invoke(controller, arguments)!;
        Func<Task> invoke = () => execution;
        await invoke.Should().ThrowAsync<DispatchObservedException>();
        return observation.Requests.Should().ContainSingle("each discovered production action sends its exact request once").Which;
    }

    private static string CensusBody(ProtectedAdministrationOperation operation) => operation switch
    {
        ProtectedAdministrationOperation.SetMembershipProfile or ProtectedAdministrationOperation.SetOwnMembershipProfile => "{\"displayName\":null,\"preferredLocale\":null}",
        ProtectedAdministrationOperation.ChangeMembershipStatus => "{\"status\":\"Suspended\"}",
        ProtectedAdministrationOperation.ReplaceMembershipRoleAssignments => "{\"roleIds\":[]}",
        ProtectedAdministrationOperation.ChangeRoleGrants => "{\"grants\":[]}",
        ProtectedAdministrationOperation.AdoptRoleGrantManifest => "{\"manifestVersion\":2,\"manifestFingerprint\":\"" + new string('a', 64) + "\"}",
        ProtectedAdministrationOperation.CreateOrganization => "{\"slug\":\"census-branch\",\"displayName\":\"Census\"}",
        ProtectedAdministrationOperation.RenameOrganization => "{\"displayName\":\"Census\"}",
        ProtectedAdministrationOperation.ArchiveOrganization => "{}",
        ProtectedAdministrationOperation.SelectDefaultOrganization => "{\"organizationId\":\"" + CensusTarget + "\"}",
        _ => throw new InvalidOperationException("Unclassified protected census input"),
    };

    private sealed class CensusTenantContext : ITenantContext
    {
        public bool IsResolved => true;
        public TenantId TenantId { get; } = LearnStack.SharedKernel.Identifiers.TenantId.From(Guid.Parse("018f4d40-0000-7000-8000-000000000022"));
        public OrganizationId? OrganizationId => null;
        public UserId? UserId { get; } = LearnStack.SharedKernel.Identifiers.UserId.From(Guid.Parse("018f4d40-0000-7000-8000-000000000033"));
        public string? CorrelationId => null;
        public string? ModuleName => null;
    }

    private static void AssertUnion(IEnumerable<string> actual)
    {
        ApprovedActions.Should().HaveCount(31);
        ApprovedActions.Count(expected => expected.Protected is not null).Should().Be(10);
        var mvc = ApprovedActions.Select(expected => Mvc(expected.Path, expected.Verb, expected.Controller, expected.Action, expected.Request));
        var infrastructure = new[]
        {
            "healthz|GET|Program+<>c.<<Main>$>b__0_1|infrastructure",
            "openapi/{documentName:regex(^(v1)$)}.json|GET|Microsoft.AspNetCore.Builder.OpenApiEndpointRouteBuilderExtensions+<>c__DisplayClass0_0.<MapOpenApi>b__0|infrastructure",
            "docs/{documentName?}|GET|Scalar.AspNetCore.ScalarEndpointRouteBuilderExtensions+<>c__DisplayClass9_0.<MapScalarApiReference>b__0|infrastructure",
            "docs/scalar.js|GET|Scalar.AspNetCore.ScalarEndpointRouteBuilderExtensions+<>c.<MapStaticAssetsEndpoints>b__10_0|infrastructure",
            "docs/scalar.aspnetcore.js|GET|Scalar.AspNetCore.ScalarEndpointRouteBuilderExtensions+<>c.<MapStaticAssetsEndpoints>b__10_1|infrastructure",
            "docs/favicon.svg|GET|Scalar.AspNetCore.ScalarEndpointRouteBuilderExtensions+<>c.<MapStaticAssetsEndpoints>b__10_2|infrastructure",
        };
        actual.Order(StringComparer.Ordinal).Should().Equal(mvc.Concat(infrastructure).Order(StringComparer.Ordinal),
            "the entire production EndpointDataSource equals the approved MVC plus infrastructure multiset");
    }

    private static string Mvc(string path, string verb, Type controller, string action, Type request) =>
        "api/v1/" + path + "|" + verb + "|" + controller.FullName + "." + action + "|" + request.FullName;

    public class RequestObservation : DispatchProxy
    {
        internal List<Type> Requests { get; } = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            targetMethod!.Name.Should().Be("Send");
            Requests.Add(args![0]!.GetType());
            throw new DispatchObservedException();
        }
    }

    private sealed class DispatchObservedException : Exception;

    private sealed class SurfaceFactory(string environment, bool addFilterProvider = false,
        Action<MvcOptions>? configureMvc = null) : ConstructionOnlyApiFactory
    {
        protected override void ConfigureTestHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("Deployment:Mode", "SelfHostedOnline");
            builder.UseSetting("Tenancy:PlatformHosts:0", "localhost");
            builder.UseSetting("Authentication:Tenant:Issuer", "https://identity.example/realms/learnstack");
            if (addFilterProvider)
                builder.ConfigureTestServices(services => services.AddSingleton<IFilterProvider, UnexpectedFilterProvider>());
            if (configureMvc is not null)
                builder.ConfigureTestServices(services => services.Configure(configureMvc));
        }
    }
}
