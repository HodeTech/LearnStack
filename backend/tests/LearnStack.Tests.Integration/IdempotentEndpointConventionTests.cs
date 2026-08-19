using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.Api.Idempotency;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>
/// <c>Idempotent_Endpoints_Are_Unsafe_Methods</c>, per
/// <see href="../../../docs/decisions/0037-idempotency-key-contract.md">ADR-0037</see>.
/// </summary>
/// <remarks>
/// An <c>Idempotency-Key</c> exists to keep an operation with external side
/// effects from happening twice. A safe method has none to repeat, so marking
/// one <c>[Idempotent]</c> does not protect anything — it just makes a read fail
/// for every client that did not send a header no read should need.
/// </remarks>
public sealed class IdempotentEndpointConventionTests(ProductionHostFixture fixture)
    : IClassFixture<ProductionHostFixture>
{
    private static readonly string[] UnsafeMethods =
        [HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete];

    [Fact]
    public void No_Endpoint_Marks_A_Safe_Method_Idempotent()
    {
        SafeIdempotentEndpoints(fixture.Services).Should().BeEmpty();
    }

    [Fact]
    public void The_Rule_Sees_A_Violation_When_There_Is_One()
    {
        // The production surface has no [Idempotent] endpoint yet — the first
        // one lands with the payment work in Phase 09 — so the rule above passes
        // whether or not it works. This is the positive control that says it
        // does: without it, the guard would be indistinguishable from an empty
        // assertion for as long as the surface stays empty.
        using var host = new ProbeHostFixture(typeof(IdempotentReadProbeController));

        SafeIdempotentEndpoints(host.Services)
            .Should().ContainSingle()
            .Which.Should().Contain("IdempotentReadProbe", Exactly.Once());
    }

    [Fact]
    public async Task An_Idempotent_Operation_Publishes_Its_Header_In_The_Contract()
    {
        // Without this the attribute is invisible to the generated SDK, every
        // call it produced would be answered 400, and "the first consumer is a
        // one-attribute change" would not be true.
        using var host = new ProbeHostFixture(typeof(IdempotentWriteProbeController));
        using var client = host.CreateClient();

        var document = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/openapi/v1.json", UriKind.Relative));

        // RouteOptions.LowercaseUrls is on, so the document publishes the
        // lowercased path rather than the controller's declared casing.
        var path = document.GetProperty("paths").EnumerateObject()
            .Single(entry => entry.Name.Contains(
                "idempotentwriteprobe", StringComparison.OrdinalIgnoreCase));
        var operation = path.Value.GetProperty("post");

        operation.GetProperty("parameters").EnumerateArray()
            .Should().ContainSingle(parameter =>
                parameter.GetProperty("name").GetString() == IdempotentAttribute.HeaderName
                && parameter.GetProperty("in").GetString() == "header"
                && parameter.GetProperty("required").GetBoolean());

        operation.GetProperty("responses").TryGetProperty("409", out _).Should().BeTrue(
            "a client has to branch on the three conflicts this surface can answer");
    }

    private static IReadOnlyList<string> SafeIdempotentEndpoints(IServiceProvider services) =>
        [.. services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IdempotentAttribute>() is not null)
            .Where(endpoint =>
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>() is not { } methods
                || !methods.HttpMethods.Any(method =>
                    UnsafeMethods.Contains(method, StringComparer.OrdinalIgnoreCase)))
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)];
}

/// <summary>A write marked idempotent, so the contract has something to publish.</summary>
public sealed class IdempotentWriteProbeController : ApiControllerBase, ITestOnlyController
{
    [HttpPost]
    [Idempotent]
    public IActionResult Post() => Ok();
}

/// <summary>A read marked idempotent, so the rule has something to catch.</summary>
public sealed class IdempotentReadProbeController : ApiControllerBase, ITestOnlyController
{
    [HttpGet]
    [Idempotent]
    public IActionResult Get() => Ok();
}
