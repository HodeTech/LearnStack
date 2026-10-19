using System.Reflection;
using FluentAssertions;
using LearnStack.Api.Account;
using LearnStack.Api.PublicReads;
using LearnStack.Api.Tenancy;
using LearnStack.Modules.Education.Application.Contracts.PublicReads;
using LearnStack.Modules.Tenancy.Application.Contracts.Account;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>ADR-0064's real endpoint union and the request each discovered MVC action actually sends.</summary>
[Collection(TenantAuthenticationDiagnosticsTestGroup.Name)]
public sealed class AuthenticatedHttpSurfaceRuntimeTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Production_endpoint_union_has_exact_patterns_methods_handlers_and_requests(string environment)
    {
        await using var factory = new SurfaceFactory(environment);
        using var client = factory.CreateClient();
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
            if (action is not null) request = await DispatchedRequest(action);
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
    }

    private static async Task<Type> DispatchedRequest(ControllerActionDescriptor action)
    {
        var sender = DispatchProxy.Create<ISender, RequestObservation>();
        var observation = (RequestObservation)sender;
        var controller = (ControllerBase)Activator.CreateInstance(action.ControllerTypeInfo.AsType(), sender)!;
        var context = new DefaultHttpContext();
        context.Features.Set(HostClassification.Platform("census.invalid"));
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        var arguments = action.MethodInfo.GetParameters().Select(parameter => parameter.ParameterType == typeof(CancellationToken)
            ? (object)CancellationToken.None : "census-slug").ToArray();
        var execution = (Task)action.MethodInfo.Invoke(controller, arguments)!;
        Func<Task> invoke = () => execution;
        await invoke.Should().ThrowAsync<DispatchObservedException>();
        return observation.Requests.Should().ContainSingle("each discovered production action sends its exact request once").Which;
    }

    private static void AssertUnion(IEnumerable<string> actual)
    {
        var mvc = new[]
        {
            Mvc("public/site", "GET", typeof(PublicSiteController), "Get", typeof(GetPublicSiteQuery)),
            Mvc("public/site", "HEAD", typeof(PublicSiteController), "Head", typeof(GetPublicSiteQuery)),
            Mvc("public/courses", "GET", typeof(PublicEducationController), "Catalog", typeof(GetPublicCoursesQuery)),
            Mvc("public/courses", "HEAD", typeof(PublicEducationController), "HeadCatalog", typeof(GetPublicCoursesQuery)),
            Mvc("public/courses/{slug}", "GET", typeof(PublicEducationController), "Course", typeof(GetPublicCourseQuery)),
            Mvc("public/courses/{slug}", "HEAD", typeof(PublicEducationController), "HeadCourse", typeof(GetPublicCourseQuery)),
            Mvc("public/courses/{slug}/lessons/{lessonSlug}", "GET", typeof(PublicEducationController), "Lesson", typeof(GetPublicLessonQuery)),
            Mvc("public/courses/{slug}/lessons/{lessonSlug}", "HEAD", typeof(PublicEducationController), "HeadLesson", typeof(GetPublicLessonQuery)),
            Mvc("account/site", "GET", typeof(AccountController), "GetSite", typeof(GetAccountSiteQuery)),
        };
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

    private sealed class SurfaceFactory(string environment) : ConstructionOnlyApiFactory
    {
        protected override void ConfigureTestHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("Deployment:Mode", "SelfHostedOnline");
            builder.UseSetting("Tenancy:PlatformHosts:0", "localhost");
            builder.UseSetting("Authentication:Tenant:Issuer", "https://identity.example/realms/learnstack");
        }
    }
}
