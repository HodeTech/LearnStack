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

    [Theory]
    [InlineData("notfound-with-body", 404, "not_found")]
    [InlineData("badrequest-with-body", 400, "validation_failed")]
    [InlineData("conflict-with-body", 409, "concurrency_conflict")]
    [InlineData("aspnet-validation-problem", 400, "validation_failed")]
    [InlineData("aspnet-problem", 400, "validation_failed")]
    public async Task A_Controller_Cannot_Ship_A_Second_Error_Shape(
        string route, int status, string code)
    {
        // NotFound() implements IClientErrorActionResult; NotFoundObjectResult
        // does not. So the bodyless helper produced the LearnStack shape while
        // NotFound(body), Conflict(body), ValidationProblem() and Problem() —
        // the most idiomatic lines a controller author writes — shipped raw
        // JSON or ASP.NET's own problem shape, with no code and no messageKey.
        var response = await _client.GetAsync(
            new Uri("/api/v1/escapeprobe/" + route, UriKind.Relative));

        await AssertProblemAsync(response, (HttpStatusCode)status, code);
    }

    [Fact]
    public async Task A_Handler_Supplied_Body_Survives_Normalisation()
    {
        // The normaliser is a last resort, not a rewriter. A body that already
        // carries `code` is left exactly as the handler built it — otherwise
        // it would discard the `errors` map on every validation failure.
        var response = await _client.GetAsync(
            new Uri("/api/v1/paginationprobe?limit=0", UriKind.Relative));

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errors").TryGetProperty("limit", out _).Should().BeTrue();
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    public async Task Limit_Boundaries_Behave(int requested, int effective)
    {
        var response = await _client.GetAsync(
            new Uri($"/api/v1/paginationprobe?limit={requested}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("limit").GetInt32().Should().Be(effective);
    }

    [Fact]
    public async Task The_Cursor_Binds_From_Its_Query_Name_And_Survives_Projection()
    {
        // The cursor half of the wire type had no test at all: its [FromQuery]
        // name and its projection could both have been broken silently.
        var response = await _client.GetAsync(
            new Uri("/api/v1/paginationprobe?cursor=abc123", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("cursor").GetString().Should().Be("abc123");
    }

    [Fact]
    public async Task The_Two_404s_Are_Byte_Identical_Apart_From_The_Path()
    {
        // ADR-0036 requires a tenant mismatch to be indistinguishable from a
        // plain not-found. Two spellings of the media type — one with
        // `; charset=utf-8`, one without — made a routing 404 tellable from an
        // MVC 404 without reading the body.
        var routing = await _client.GetAsync(
            new Uri("/api/v1/no-such-route", UriKind.Relative));
        var mvc = await _client.GetAsync(
            new Uri("/api/v1/escapeprobe/notfound-bare", UriKind.Relative));

        routing.Content.Headers.ContentType?.ToString()
            .Should().Be(mvc.Content.Headers.ContentType?.ToString());

        static string Shape(JsonElement e) => string.Join(
            ",", e.EnumerateObject().Where(p => p.Name != "instance")
                  .Where(p => p.Name != "correlationId")
                  .Select(p => p.Name + "=" + p.Value.ToString()));

        Shape(await routing.Content.ReadFromJsonAsync<JsonElement>())
            .Should().Be(Shape(await mvc.Content.ReadFromJsonAsync<JsonElement>()));
    }

    [Theory]
    [InlineData("/openapi/v9.json")]
    [InlineData("/openapi/garbage")]
    [InlineData("/openapi/")]
    public async Task An_Unknown_OpenApi_Document_Is_An_Ordinary_404(string path)
    {
        // Two shapes escaped here. `/openapi/v9.json` answered 404 in
        // text/plain with English framework prose — "No OpenAPI document with
        // the name 'v9' was found." — and Scalar's catch-all, when the console
        // was mounted under /openapi, answered `/openapi/garbage` with 200
        // text/html, so an unknown document looked like a success. The document
        // route is now constrained to the documents that exist and the console
        // moved to /docs, which stops it shadowing the namespace.
        var response = await _client.GetAsync(new Uri(path, UriKind.Relative));

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "not_found");
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
        problem.GetProperty("correlationId").GetString().Should().NotBeNullOrWhiteSpace(
            "Standards 09 § API Surface makes correlationId part of every error body — "
            + "asserting presence alone passes when the value is null");

        // Standards 09 fixes `title` as the lockey and `code` as that key with
        // the prefix stripped, so the two cannot disagree. Neither was pinned.
        problem.GetProperty("title").GetString().Should().Be("lockey_" + code);
        problem.GetProperty("type").GetString()
            .Should().StartWith("https://errors.learnstack.dev/");
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
                        typeof(PaginationProbeController),
                        typeof(EchoProbeController),
                        typeof(EscapeProbeController))))
                .AddApplicationPart(typeof(PaginationProbeController).Assembly));
    }
}

public sealed class PaginationProbeController : ApiControllerBase, ITestOnlyController
{
    [HttpGet]
    public IActionResult Get([FromQuery] CursorPaginationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pagination = request.ToPagination();
        return Ok(new { pagination.Limit, pagination.Cursor });
    }
}

public sealed class EchoProbeController : ApiControllerBase, ITestOnlyController
{
    [HttpPost]
    public IActionResult Post([FromBody] EchoPayload payload) => Ok(payload);
}

public sealed record EchoPayload(string Name);

/// <summary>
/// Every way a controller can ship an error body that is not the sanctioned
/// <c>ToActionResult()</c> shape.
/// </summary>
public sealed class EscapeProbeController : ApiControllerBase, ITestOnlyController
{
    [HttpGet("notfound-bare")]
    public IActionResult NotFoundBare() => NotFound();

    [HttpGet("notfound-with-body")]
    public IActionResult NotFoundWithBody() => NotFound(new { reason = "gone fishing" });

    [HttpGet("badrequest-with-body")]
    public IActionResult BadRequestWithBody() => BadRequest(new { reason = "nope" });

    [HttpGet("conflict-with-body")]
    public IActionResult ConflictWithBody() => Conflict(new { reason = "clash" });

    [HttpGet("aspnet-validation-problem")]
    public IActionResult AspNetValidationProblem()
    {
        ModelState.AddModelError("name", "Name is required.");
        return ValidationProblem(ModelState);
    }

    [HttpGet("aspnet-problem")]
    public IActionResult AspNetProblem() =>
        Problem(detail: "raw english", statusCode: 400);
}
