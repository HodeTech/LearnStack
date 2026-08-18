using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.Api.Pagination;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>
/// "Problem Details (RFC 7807) on every error" — the
/// <see href="../../../docs/roadmap/phase-02a-kernel-tenancy.md">Packet 4</see>
/// deliverable — plus the cursor-pagination binding contract from
/// <see href="../../../docs/standards/04-api-design.md">Standards 04
/// § Pagination</see>.
/// </summary>
public sealed class ErrorShapeHttpTests(ErrorShapeFixture fixture)
    : IClassFixture<ErrorShapeFixture>
{
    private readonly HttpClient _client = fixture.CreateClient();

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=-5")]
    public async Task A_Non_Positive_Limit_Names_The_Parameter_The_Client_Sent(string query)
    {
        // The kernel's CursorPagination.Limit init accessor throws on a
        // non-positive value, and MVC records that throw against the binder's
        // keys rather than the query parameter's. Binding the kernel type
        // directly produced a 400 whose errors map named "$" and "pagination" —
        // right status, no way to learn which parameter was wrong.
        var response = await _client.GetAsync(
            new Uri("/api/v1/paginationprobe" + query, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("validation_failed");

        var errors = problem.GetProperty("errors");
        errors.TryGetProperty("limit", out _).Should().BeTrue(
            "the client sent `limit`, so that is the name it can act on");
        errors.EnumerateObject().Select(e => e.Name)
            .Should().NotContain(["$", "pagination"],
                "binder-internal names are not part of the contract");
    }

    [Fact]
    public async Task A_Non_Numeric_Limit_Also_Names_The_Parameter()
    {
        var response = await _client.GetAsync(
            new Uri("/api/v1/paginationprobe?limit=abc", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errors").TryGetProperty("limit", out _).Should().BeTrue();
    }

    [Fact]
    public async Task An_Absent_Limit_Takes_The_Default()
    {
        var response = await _client.GetAsync(
            new Uri("/api/v1/paginationprobe", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("limit").GetInt32().Should().Be(20);
    }

    [Fact]
    public async Task A_Limit_Above_The_Maximum_Is_Clamped_Not_Rejected()
    {
        // Standards 04 § Pagination caps at 100. The kernel clamps rather than
        // rejects — a decision that shipped with it in Packet 2 — so the wire
        // type deliberately does not enforce the ceiling. One behaviour, not
        // two.
        var response = await _client.GetAsync(
            new Uri("/api/v1/paginationprobe?limit=9999", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("limit").GetInt32().Should().Be(100);
    }

    [Fact]
    public async Task An_Unmatched_Route_Returns_Problem_Details()
    {
        var response = await _client.GetAsync(
            new Uri("/api/v1/nothing-is-mapped-here", UriKind.Relative));

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task A_Wrong_Method_Returns_Problem_Details()
    {
        var response = await _client.PostAsync(
            new Uri("/api/v1/paginationprobe", UriKind.Relative), content: null);

        await AssertProblemAsync(
            response, HttpStatusCode.MethodNotAllowed, "method_not_allowed");
    }

    [Fact]
    public async Task An_Unsupported_Media_Type_Returns_Problem_Details()
    {
        var response = await _client.PostAsync(
            new Uri("/api/v1/echoprobe", UriKind.Relative),
            new StringContent("name=alice", Encoding.UTF8, "text/plain"));

        await AssertProblemAsync(
            response, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response, HttpStatusCode expected, string code)
    {
        response.StatusCode.Should().Be(expected);
        response.Content.Headers.ContentType?.MediaType
            .Should().Be("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be(code);
        problem.GetProperty("messageKey").GetString().Should().Be("lockey_" + code);
        problem.GetProperty("status").GetInt32().Should().Be((int)expected);
        problem.TryGetProperty("correlationId", out _).Should().BeTrue(
            "Standards 09 § API Surface makes correlationId part of every error body");
    }
}

public sealed class ErrorShapeFixture : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureTestServices(services =>
            services.AddControllers(options =>
                    options.Conventions.Insert(0, new TestControllerFilter(
                        typeof(PaginationProbeController), typeof(EchoProbeController))))
                .AddApplicationPart(typeof(PaginationProbeController).Assembly));
    }
}

public sealed class PaginationProbeController : ApiControllerBase, ITestOnlyController
{
    [HttpGet]
    public IActionResult Get([FromQuery] CursorPaginationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(new { request.ToPagination().Limit });
    }
}

public sealed class EchoProbeController : ApiControllerBase, ITestOnlyController
{
    [HttpPost]
    public IActionResult Post([FromBody] EchoPayload payload) => Ok(payload);
}

public sealed record EchoPayload(string Name);
