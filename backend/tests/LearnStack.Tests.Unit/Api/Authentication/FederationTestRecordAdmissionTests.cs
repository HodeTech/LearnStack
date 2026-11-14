using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.Api.Identity;
using LearnStack.Api.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Authentication;

/// <summary>
/// ADR-0099 Amendment 8 (S4-2b): the credential admission is the one point where a token carrying the
/// <c>federation.test_record</c> scope is refused on every endpoint but the test record. The exception is the test
/// record's action identity, never its path: the planted control serves the test record's path from another action.
/// </summary>
public sealed class FederationTestRecordAdmissionTests
{
    private const string TestResults = "/api/v1/identity/federation/test-results";

    [Fact]
    public async Task The_test_record_admits_the_scoped_token()
    {
        var (calls, http) = await InvokeAsync(scoped: true, Action(nameof(FederationController.RecordTestResult)));
        calls.Should().Be(1);
        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Theory]
    [InlineData(nameof(FederationController.Get), "GET", "/api/v1/identity/federation")]
    [InlineData(nameof(FederationController.StartTest), "POST", "/api/v1/identity/federation/test-starts")]
    [InlineData(nameof(FederationController.Activate), "POST", "/api/v1/identity/federation/activation")]
    public async Task Every_other_endpoint_refuses_the_scoped_token_with_the_closed_403(string action, string method, string path)
    {
        var (calls, http) = await InvokeAsync(scoped: true, Action(action), method, path);
        calls.Should().Be(0);
        await ShouldBeForbiddenAsync(http);
    }

    [Fact]
    public async Task An_unmatched_request_refuses_the_scoped_token()
    {
        var (calls, http) = await InvokeAsync(scoped: true, endpoint: null, "GET", "/api/v1/identity/self");
        calls.Should().Be(0);
        await ShouldBeForbiddenAsync(http);
    }

    /// <summary>The planted control: the test record's own path and method, served by another action, is refused.</summary>
    [Fact]
    public async Task The_exception_is_the_test_record_action_not_its_path()
    {
        var (calls, http) = await InvokeAsync(scoped: true, Action(nameof(FederationController.Activate)), "POST", TestResults);
        calls.Should().Be(0);
        await ShouldBeForbiddenAsync(http);
    }

    [Theory]
    [InlineData(nameof(FederationController.Get))]
    [InlineData(nameof(FederationController.RecordTestResult))]
    public async Task An_unscoped_token_is_admitted_everywhere(string action)
    {
        var (calls, http) = await InvokeAsync(scoped: false, Action(action));
        calls.Should().Be(1);
        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    private static Endpoint Action(string name)
    {
        var method = typeof(FederationController).GetMethod(name, BindingFlags.Public | BindingFlags.Instance)!;
        return new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new ControllerActionDescriptor
        {
            ControllerTypeInfo = typeof(FederationController).GetTypeInfo(),
            MethodInfo = method,
            ControllerName = "Federation",
            ActionName = name,
        }), name);
    }

    private static async Task<(int Calls, DefaultHttpContext Http)> InvokeAsync(bool scoped, Endpoint? endpoint,
        string method = "POST", string path = TestResults)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Path = path;
        http.Response.Body = new MemoryStream();
        http.Features.Set(HostClassification.Platform("platform.example"));
        http.Features.Set(new TenantCredentialFeature
        {
            VerifiedSubject = new VerifiedProviderSubject("https://issuer.example/realms/learnstack", "subject", null, null, null,
                FederationTestRecord: scoped),
        });
        if (endpoint is not null) http.SetEndpoint(endpoint);
        var calls = 0;
        await new TenantCredentialAdmissionMiddleware(_ => { calls++; return Task.CompletedTask; }).InvokeAsync(http);
        return (calls, http);
    }

    private static async Task ShouldBeForbiddenAsync(DefaultHttpContext http)
    {
        http.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        http.Response.Body.Position = 0;
        using var problem = await JsonDocument.ParseAsync(http.Response.Body);
        problem.RootElement.GetProperty("code").GetString().Should().Be("forbidden");
        problem.RootElement.GetProperty("messageKey").GetString().Should().Be("lockey_forbidden");
    }
}
