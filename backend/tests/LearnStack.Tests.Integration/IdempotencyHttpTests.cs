using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.Api.Idempotency;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>
/// <c>Idempotency-Key</c>, per
/// <see href="../../../docs/standards/04-api-design.md">Standards 04
/// § Idempotency</see>: the server stores <c>(key, response)</c> and a repeat
/// returns the stored one instead of doing the work twice.
/// </summary>
public sealed class IdempotencyHttpTests(IdempotencyFixture fixture)
    : IClassFixture<IdempotencyFixture>
{
    private readonly HttpClient _client = fixture.CreateClient();

    [Fact]
    public async Task A_Repeat_Replays_The_First_Response_Without_Doing_The_Work_Again()
    {
        var key = NewKey();
        SideEffectProbeController.Reset();

        var first = await PostAsync("/api/v1/sideeffectprobe", key);
        var second = await PostAsync("/api/v1/sideeffectprobe", key);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        // The whole point: the body is identical because it IS the first one.
        (await second.Content.ReadAsStringAsync())
            .Should().Be(await first.Content.ReadAsStringAsync());

        SideEffectProbeController.Invocations.Should().Be(1,
            "the operation must run once, however many times the client asks");
    }

    [Fact]
    public async Task A_Replay_Says_So()
    {
        // A client retrying after a timeout cannot otherwise tell whether its
        // second call did the work or collected the first one's answer.
        var key = NewKey();
        SideEffectProbeController.Reset();

        var first = await PostAsync("/api/v1/sideeffectprobe", key);
        var second = await PostAsync("/api/v1/sideeffectprobe", key);

        first.Headers.Contains(IdempotentAttribute.ReplayedHeaderName).Should().BeFalse();
        second.Headers.GetValues(IdempotentAttribute.ReplayedHeaderName)
            .Single().Should().Be("true");
    }

    [Fact]
    public async Task A_Different_Key_Does_The_Work_Again()
    {
        SideEffectProbeController.Reset();

        await PostAsync("/api/v1/sideeffectprobe", NewKey());
        await PostAsync("/api/v1/sideeffectprobe", NewKey());

        SideEffectProbeController.Invocations.Should().Be(2);
    }

    [Theory]
    [InlineData(null, "absent")]
    [InlineData("short", "under the minimum length")]
    [InlineData("has a space in it", "a space is not printable-ASCII-no-space")]
    [InlineData("controlchar-key", "a control character")]
    public async Task A_Missing_Or_Malformed_Key_Is_A_400(string? key, string why)
    {
        var response = await PostAsync("/api/v1/sideeffectprobe", key);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, why);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("validation_failed");
        problem.GetProperty("errors")
            .TryGetProperty(IdempotentAttribute.ErrorsKey, out _).Should().BeTrue(
                "the camelCase form survives the errors-map projection unchanged; "
                + "camelCasing the literal header name yields 'idempotency-Key'");
    }

    [Fact]
    public async Task A_Repeated_Key_Header_Is_Refused_Not_Resolved_By_First_Or_Last()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/v1/sideeffectprobe", UriKind.Relative));
        request.Headers.Add(IdempotentAttribute.HeaderName, NewKey());
        request.Headers.Add(IdempotentAttribute.HeaderName, NewKey());

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_Failed_Attempt_Does_Not_Pin_The_Key_For_A_Day()
    {
        // Storing a failure would make every retry replay it for the retention
        // window, turning one transient fault into a day of them.
        var key = NewKey();
        SideEffectProbeController.Reset();

        var failed = await PostAsync("/api/v1/sideeffectprobe?fail=true", key);
        failed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        var retried = await PostAsync("/api/v1/sideeffectprobe", key);

        retried.StatusCode.Should().Be(HttpStatusCode.OK,
            "the key was released, so the retry is allowed to run");
    }

    [Fact]
    public async Task Two_Tenants_Using_The_Same_Key_Do_Not_Share_A_Response()
    {
        // The key is client-chosen, so two tenants WILL eventually pick the
        // same one. A flat key space would hand the second one the first one's
        // response body.
        var key = NewKey();
        SideEffectProbeController.Reset();

        var first = await PostAsync("/api/v1/sideeffectprobe", key);
        using var otherTenant = fixture.CreateClientForOtherTenant();
        var second = await PostAsync("/api/v1/sideeffectprobe", key, otherTenant);

        second.Headers.Contains(IdempotentAttribute.ReplayedHeaderName).Should().BeFalse(
            "the second tenant's key is its own");
        (await second.Content.ReadAsStringAsync())
            .Should().NotBe(await first.Content.ReadAsStringAsync());
        SideEffectProbeController.Invocations.Should().Be(2);
    }

    private static string NewKey() => "01HX" + Guid.NewGuid().ToString("N");

    private Task<HttpResponseMessage> PostAsync(string path, string? key, HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation(IdempotentAttribute.HeaderName, key);
        }

        return (client ?? _client).SendAsync(request);
    }
}

/// <summary>A host with a resolved tenant, because an idempotency key is tenant-scoped.</summary>
public sealed class IdempotencyFixture : WebApplicationFactory<Program>
{
    public static readonly Guid TenantA = Guid.Parse("018f4d40-0000-7000-8000-00000000000a");
    public static readonly Guid TenantB = Guid.Parse("018f4d40-0000-7000-8000-00000000000b");

    private readonly SwitchableTenantContext _tenantContext = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureTestServices(services =>
        {
            services.AddControllers(options =>
                    options.Conventions.Insert(0, new TestControllerFilter(
                        typeof(SideEffectProbeController))))
                .AddApplicationPart(typeof(SideEffectProbeController).Assembly);

            services.RemoveAll<ITenantContext>();
            services.AddSingleton<ITenantContext>(_tenantContext);
        });
    }

    /// <summary>
    /// A client whose requests resolve to a different tenant. The context is
    /// switched rather than a second host started, because the store is a
    /// singleton and two hosts would not share it — which would make the
    /// cross-tenant test pass for the wrong reason.
    /// </summary>
    public HttpClient CreateClientForOtherTenant()
    {
        _tenantContext.TenantId = TenantB;
        return CreateClient();
    }

    internal sealed class SwitchableTenantContext : ITenantContext
    {
        public bool IsResolved => true;
        public Guid TenantId { get; set; } = TenantA;
        public Guid? OrganizationId => null;
        public SharedKernel.Identifiers.UserId? UserId => null;
        public string? CorrelationId => null;
        public string? ModuleName => "integration-test";
    }
}

public sealed class SideEffectProbeController : ApiControllerBase, ITestOnlyController
{
    private static int _invocations;

    public static int Invocations => _invocations;

    public static void Reset() => Interlocked.Exchange(ref _invocations, 0);

    [HttpPost]
    [Idempotent]
    public IActionResult Post([FromQuery] bool fail = false)
    {
        Interlocked.Increment(ref _invocations);

        if (fail)
        {
            throw new InvalidOperationException("probe: a failed attempt must release its key");
        }

        return Ok(new { ran = _invocations, at = Guid.NewGuid() });
    }
}
