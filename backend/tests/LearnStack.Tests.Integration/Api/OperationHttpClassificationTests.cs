using LearnStack.Api.Account;
using LearnStack.Api.Authentication;
using LearnStack.Api.Versioning;
using LearnStack.Application.Authorization;
using LearnStack.Modules.Customization.Application.Authorization;
using LearnStack.Modules.Education.Application.Authorization;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Tenancy.Application.Authorization;
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
        Assert.Equal(10, endpoints.Count(endpoint => endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is not null));
        foreach (var endpoint in endpoints)
        {
            var classification = classifier.Classify(endpoint, out var policy);
            if (endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is not null)
            {
                Assert.Equal(HttpOperationClassification.Application, classification);
                Assert.NotNull(policy);
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
        var original = Assert.Single(Endpoints(app), endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "GetPublicSite");
        var classifier = new OperationHttpClassifier(Catalog());
        Assert.Equal(HttpOperationClassification.Application, classifier.Classify(original, out _));
        var metadata = original.Metadata.ToList();
        var declaration = Assert.Single(metadata.OfType<OperationPolicyAttribute>());
        switch (defect)
        {
            case "missing": metadata.Remove(declaration); break;
            case "duplicate": metadata.Add(declaration); break;
            case "wrong-request": metadata.Remove(declaration); metadata.Add(new OperationPolicyAttribute(typeof(LearnStack.Modules.Tenancy.Application.Contracts.Account.GetAccountSiteQuery))); break;
            case "wrong-name": metadata.RemoveAll(value => value is IEndpointNameMetadata); metadata.Add(new EndpointNameMetadata("UnclassifiedAction")); break;
            case "wrong-method": metadata.RemoveAll(value => value is IHttpMethodMetadata); metadata.Add(new HttpMethodMetadata(["POST"])); break;
            case "infrastructure-on-controller": metadata.Add(InfrastructureOperationMetadata.Health); break;
        }
        var dirty = new Endpoint(original.RequestDelegate, new EndpointMetadataCollection(metadata), original.DisplayName);
        Assert.Equal(HttpOperationClassification.Refused, classifier.Classify(dirty, out var refused));
        Assert.Null(refused);
        Assert.Equal(HttpOperationClassification.Application, classifier.Classify(original, out _));
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
