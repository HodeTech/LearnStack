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

/// <summary>A read marked idempotent, so the rule has something to catch.</summary>
public sealed class IdempotentReadProbeController : ApiControllerBase, ITestOnlyController
{
    [HttpGet]
    [Idempotent]
    public IActionResult Get() => Ok();
}
