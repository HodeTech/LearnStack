using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>
/// The in-process rate limiter
/// <see href="../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036</see>
/// makes a Packet 4 deliverable, at the anonymous budget
/// <see href="../../../docs/standards/04-api-design.md">Standards 04
/// § Request and Response Limits</see> fixes.
/// </summary>
/// <remarks>
/// Its own fixture, and nothing else uses it. A limiter is per-host state, so a
/// test that exhausts the budget would otherwise 429 whatever ran next in the
/// same host — the kind of order-dependent failure that shows up once in CI and
/// never locally.
/// </remarks>
public sealed class RateLimitingHttpTests : IClassFixture<RateLimitedHostFixture>
{
    private readonly RateLimitedHostFixture _fixture;

    public RateLimitingHttpTests(RateLimitedHostFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_Anonymous_Budget_Is_Enforced_And_Answers_In_The_One_Error_Shape()
    {
        using var client = _fixture.CreateClient();
        var path = new Uri("/healthz", UriKind.Relative);

        // The real configured budget, not a stubbed one: exhausting exactly
        // RateLimitingExtensions.AnonymousPermitPerWindow requests must still
        // succeed, and the next must not.
        for (var i = 0; i < RateLimitingExtensions.AnonymousPermitPerWindow; i++)
        {
            var allowed = await client.GetAsync(path);
            allowed.StatusCode.Should().Be(HttpStatusCode.OK,
                "request {0} is inside the budget", i + 1);
        }

        var rejected = await client.GetAsync(path);

        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // Standards 04 § Status Codes: "429 Rate limited; Retry-After set".
        rejected.Headers.Should().ContainSingle(header =>
            header.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase));

        // And the same Problem Details shape every other client error carries —
        // the limiter writes no body, so UseStatusCodePages supplies it.
        rejected.Content.Headers.ContentType?.MediaType
            .Should().Be("application/problem+json");
        var problem = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("rate_limited");
        problem.GetProperty("status").GetInt32().Should().Be(429);
    }
}

/// <summary>A host used by nothing else, so its exhausted budget poisons nothing.</summary>
public sealed class RateLimitedHostFixture : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(Environments.Development);
    }
}
