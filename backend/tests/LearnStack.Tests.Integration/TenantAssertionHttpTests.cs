using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using LearnStack.Infrastructure.Audit;
using LearnStack.Api.Common;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Identifiers;
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
/// The assertion comparison from
/// <see href="../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036
/// § What the assertions do</see>: <c>X-Tenant-Id</c> and
/// <c>X-Organization-Id</c> can cause a request to be rejected and can never
/// cause a tenant to be selected.
/// </summary>
/// <remarks>
/// Packet 4 resolves nothing, so the fixture substitutes a resolved
/// <c>ITenantContext</c> — which is what makes the mismatch path reachable at
/// all. That substitution is the test's subject, not a shortcut: ADR-0036's
/// staging table said this comparison was "unreachable in traffic … and exercised by
/// unit tests over a stubbed context" before Packet 7's resolver existed. It is reachable
/// now; the substitution stays because this suite drives the mismatch deliberately, and
/// <c>TenantIsolationHttpTests</c> is where the real resolver answers.
/// </remarks>
public sealed class TenantAssertionHttpTests(ResolvedTenantFixture fixture)
    : IClassFixture<ResolvedTenantFixture>
{
    private readonly HttpClient _client = fixture.CreateClient();

    [Fact]
    public async Task An_Assertion_That_Agrees_Changes_Nothing()
    {
        using var request = Get("/api/v1/assertionprobe");
        request.Headers.Add(TenantAssertionMiddleware.TenantHeaderName,
            ResolvedTenantFixture.TenantId.ToString());

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task No_Assertion_Changes_Nothing()
    {
        var response = await _client.GetAsync(new Uri("/api/v1/assertionprobe", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_Tenant_Assertion_That_Disagrees_Is_A_404()
    {
        // 404, not 403: saying "wrong tenant" confirms the other tenant exists.
        using var request = Get("/api/v1/assertionprobe");
        request.Headers.Add(TenantAssertionMiddleware.TenantHeaderName,
            Guid.NewGuid().ToString());

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType
            .Should().Be("application/problem+json",
                "a rejected assertion answers exactly as a routing 404 does");
    }

    [Fact]
    public async Task The_Rejection_Names_The_Resolved_Tenant_And_The_Asserted_Value()
    {
        // The recorder decides what the ROW carries; this decides what the recorder is
        // TOLD, and nothing constrained it. Measured: swapping the two arguments in
        // TenantAssertionMiddleware left all 1782 cases green — and that swap announces
        // `app.tenant_id` from an attacker-supplied header, which is the one primitive
        // ADR-0036 § Recording a rejected assertion exists to deny.
        fixture.Recorder.Clear();

        var asserted = Guid.Parse("018f4d40-0000-7000-8000-0000000000ff");

        using var request = Get("/api/v1/assertionprobe");
        request.Headers.Add(TenantAssertionMiddleware.TenantHeaderName, asserted.ToString());

        (await _client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var rejection = fixture.Recorder.Rejections.Should().ContainSingle().Subject;

        rejection.ResolvedTenantId.Should().Be(ResolvedTenantFixture.TenantId,
            "the row is written under the tenant whose boundary was defended");
        rejection.AssertedValue.Should().Be(asserted,
            "and the client's claim travels as metadata, never as the row's tenant");
        rejection.Dimension.Should().Be(TenantAssertionDimension.Tenant);
        rejection.IsAuthenticated.Should().BeFalse(
            "there is no UseAuthentication until Phase 02b, so the tier is constant-false");
    }

    [Fact]
    public async Task An_Organization_Assertion_That_Disagrees_Is_A_404()
    {
        using var request = Get("/api/v1/assertionprobe");
        request.Headers.Add(TenantAssertionMiddleware.OrganizationHeaderName,
            Guid.NewGuid().ToString());

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_Assertion_Never_Selects_A_Tenant()
    {
        // The property the whole ADR exists for. The probe echoes the tenant
        // the context resolved; asserting a different one must not change it —
        // it must refuse the request.
        using var agreeing = Get("/api/v1/assertionprobe");
        agreeing.Headers.Add(TenantAssertionMiddleware.TenantHeaderName,
            ResolvedTenantFixture.TenantId.ToString());

        var body = await (await _client.SendAsync(agreeing)).Content.ReadAsStringAsync();

        body.Should().Contain(ResolvedTenantFixture.TenantId.ToString(),
            "the resolved tenant is the only one that can ever be served");
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000")]
    [InlineData("'; drop table tenants--")]
    public async Task A_Malformed_Assertion_Is_A_400(string value)
    {
        // An empty value is deliberately not a row here: HttpClient drops a
        // header whose value is empty, so the request arrives with no header at
        // all and correctly answers 200. The code path is still covered —
        // Guid.TryParse("") is false — it simply cannot be reached through a
        // client that refuses to send it.
        using var request = Get("/api/v1/assertionprobe");
        request.Headers.TryAddWithoutValidation(
            TenantAssertionMiddleware.TenantHeaderName, value);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_Repeated_Assertion_Is_Refused_Not_Resolved_By_First_Or_Last()
    {
        // The classic header-confusion bug: a proxy in front of a client that
        // already sent one produces two, and whichever end you pick, some
        // topology makes it the attacker's. Both values here are individually
        // valid — only their multiplicity is wrong.
        using var request = Get("/api/v1/assertionprobe");
        request.Headers.Add(TenantAssertionMiddleware.TenantHeaderName,
            ResolvedTenantFixture.TenantId.ToString());
        request.Headers.Add(TenantAssertionMiddleware.TenantHeaderName,
            Guid.NewGuid().ToString());

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Every_Response_Carries_A_Correlation_Id()
    {
        // Standards 10 § Correlation puts correlation_id on the Problem Details
        // body and on error-tracker captures. The success path had no handle at
        // all: a client reporting "this rendered the wrong thing" could only
        // obtain one by receiving an error first.
        var response = await _client.GetAsync(new Uri("/api/v1/assertionprobe", UriKind.Relative));

        response.Headers.TryGetValues(CorrelationHeaderMiddleware.HeaderName, out var values)
            .Should().BeTrue();
        values!.Single().Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("client-chosen-value")]
    [InlineData("café")]
    [InlineData("a\u0001b")]
    [InlineData("\U0001F4A9")]
    [InlineData("a\u007Fb")]
    public async Task A_Client_Supplied_Correlation_Id_Is_Ignored_Not_Reflected(string supplied)
    {
        // A first version echoed this back under a second header. Kestrel
        // accepts bytes in a REQUEST header that it refuses to write into a
        // RESPONSE header, so 'é', a control character or an emoji made the
        // assignment throw: a 500 on every route, before authentication,
        // each one captured by IErrorTrackingProvider. One header, anonymous,
        // and the error-tracker quota is someone else's.
        using var request = Get("/api/v1/assertionprobe");
        request.Headers.TryAddWithoutValidation(
            CorrelationHeaderMiddleware.HeaderName, supplied);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "no client-supplied value may decide whether this request succeeds");

        var returned = response.Headers.GetValues(CorrelationHeaderMiddleware.HeaderName).Single();
        returned.Should().NotBe(supplied,
            "the trace context is the identity, and the client's value is not adopted");
        response.Headers.Should().NotContain(header =>
            header.Key.Contains("Request-Correlation", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/openapi/v1.json")]
    public async Task A_Malformed_Assertion_Does_Not_Break_An_Unscoped_Route(string path)
    {
        // Registered globally, a malformed X-Tenant-Id 400s the orchestrator's
        // health probe — which takes the pod out — and the Hub's
        // /api/internal/* surface, neither of which has an assertion to
        // compare. ADR-0036 scopes host classification to /api/v1/* for the
        // same reason.
        using var request = Get(path);
        request.Headers.TryAddWithoutValidation(
            TenantAssertionMiddleware.TenantHeaderName, "not-a-guid");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static HttpRequestMessage Get(string path) =>
        new(HttpMethod.Get, new Uri(path, UriKind.Relative));
}

/// <summary>
/// The tenant-wide branch of the organization comparison: a resolved context
/// carrying <b>no</b> organization, with an organization asserted at it.
/// </summary>
/// <remarks>
/// <para>
/// Its own fixture because <see cref="ResolvedTenantFixture"/>'s context always
/// resolves an organization, so the branch this covers has no fixture there and
/// was reachable by no test. Measured: rewriting the middleware's null clause to
/// the natural-looking
/// <c>OrganizationId is { } r &amp;&amp; organization != r.Value</c> leaves the entire
/// suite green while turning this case from a 404 into a 200.
/// </para>
/// <para>
/// What that mutant permits is the one thing
/// <see href="../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036</see>
/// says an assertion may never do: a header would widen a tenant-wide request
/// into an organization scope the resolver never granted. The rule is that an
/// assertion can reject a request and can never fill a gap.
/// </para>
/// </remarks>
public sealed class TenantWideOrganizationAssertionTests(TenantWideFixture fixture)
    : IClassFixture<TenantWideFixture>
{
    private readonly HttpClient _client = fixture.CreateClient();

    [Fact]
    public async Task An_Organization_Asserted_Against_A_Tenant_Wide_Context_Is_A_404()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri("/api/v1/assertionprobe", UriKind.Relative));
        request.Headers.Add(
            TenantAssertionMiddleware.OrganizationHeaderName, Guid.NewGuid().ToString());

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "an assertion may reject a request and may never widen one");
    }

    [Fact]
    public async Task An_Organization_Asserted_Against_An_Unassigned_One_Is_A_404_Not_A_500()
    {
        // A resolved context whose OrganizationId is non-null but was never
        // assigned. Reading Value on it throws, and the throw escapes into
        // UseExceptionHandler — replacing this middleware's whole purpose, a
        // clean fail-closed 404, with an uncontrolled 500 on a pre-auth path,
        // triggered by an attacker-supplied header. The comparison must treat an
        // unusable resolved organization exactly as it treats an absent one.
        using var client = fixture.WithUnassignedOrganization().CreateClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri("/api/v1/assertionprobe", UriKind.Relative));
        request.Headers.Add(
            TenantAssertionMiddleware.OrganizationHeaderName, Guid.NewGuid().ToString());

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_Same_Request_Without_The_Header_Is_Served()
    {
        // The companion that stops the 404 above from passing for the wrong
        // reason — a mis-wired fixture, a missing probe route, a middleware that
        // refuses every tenant-wide request.
        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri("/api/v1/assertionprobe", UriKind.Relative));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

/// <summary>
/// <see cref="ResolvedTenantFixture"/> with the organization removed, so the
/// context is resolved and tenant-wide.
/// </summary>
public sealed class TenantWideFixture : ResolvedTenantFixture
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITenantContext>();
            services.AddScoped<ITenantContext>(_ => TenantWideContext.Instance);
        });
    }

    /// <summary>
    /// The same host, with an organization that is present but never assigned.
    /// </summary>
    public WebApplicationFactory<Program> WithUnassignedOrganization() =>
        WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITenantContext>();
            services.AddScoped<ITenantContext>(_ => UnassignedOrganizationContext.Instance);
        }));

    private sealed class UnassignedOrganizationContext : ITenantContext
    {
        public static UnassignedOrganizationContext Instance { get; } = new();

        public bool IsResolved => true;

        public TenantId TenantId =>
            SharedKernel.Identifiers.TenantId.From(ResolvedTenantFixture.TenantId);

        /// <summary>
        /// Non-null and uninitialized. VOG009 forbids writing that as a literal,
        /// so it comes from an array element — the way production reaches it too,
        /// through a member nothing assigned.
        /// </summary>
        public OrganizationId? OrganizationId => Unassigned;

        private static readonly OrganizationId Unassigned = Zeroed();

        private static OrganizationId Zeroed()
        {
            var slot = new OrganizationId[1];
            return slot[0];
        }

        public UserId? UserId => null;

        public string? CorrelationId => null;

        public string? ModuleName => "integration-test";
    }

    private sealed class TenantWideContext : ITenantContext
    {
        public static TenantWideContext Instance { get; } = new();

        public bool IsResolved => true;

        public TenantId TenantId =>
            SharedKernel.Identifiers.TenantId.From(ResolvedTenantFixture.TenantId);

        /// <summary>Tenant-wide: no organization, which is a scope and not "unknown".</summary>
        public OrganizationId? OrganizationId => null;

        public UserId? UserId => null;

        public string? CorrelationId => null;

        public string? ModuleName => "integration-test";
    }
}

/// <summary>
/// A host whose <see cref="ITenantContext"/> is resolved, so the assertion
/// comparison has something to compare against.
/// </summary>
/// <summary>
/// What the composition root actually binds — the half no other case could see.
/// </summary>
/// <remarks>
/// Measured: reverting <c>ITenantAssertionRecorder</c> to
/// <c>LoggingTenantAssertionRecorder</c>, or the burst detector to <c>AddScoped</c>, left
/// every one of the 1782 cases green while writing zero <c>audit_log</c> rows — the first
/// because every suite over this middleware substitutes a spy, the second because a
/// per-request detector counts to one and crosses nothing. The packet could be un-shipped
/// without a single test noticing.
/// </remarks>
public sealed class AssertionRecorderCompositionTests(RegisteredRecorderFixture fixture)
    : IClassFixture<RegisteredRecorderFixture>
{
    [Fact]
    public void The_Registered_Recorder_Is_The_Auditing_One()
    {
        using var scope = fixture.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ITenantAssertionRecorder>()
            .Should().BeOfType<AuditingTenantAssertionRecorder>(
                "the logging recorder is the inner half, not the registered seam");
    }

    [Fact]
    public void The_Burst_Detector_Is_A_Singleton()
    {
        // The window is the PROCESS's. A scoped detector counts to one per request and
        // never reaches any threshold, so the anonymous tier would silently stop writing.
        using var first = fixture.Services.CreateScope();
        using var second = fixture.Services.CreateScope();

        first.ServiceProvider.GetRequiredService<TenantAssertionBurstDetector>()
            .Should().BeSameAs(
                second.ServiceProvider.GetRequiredService<TenantAssertionBurstDetector>());
    }

    [Fact]
    public void A_Non_Positive_Burst_Window_Is_Refused_At_Boot()
    {
        // A MUST-class security event that a config typo switches off is the one outcome
        // the in-process counter exists to prevent. Measured before the guard: with
        // Window = 00:00:00 the counter resets on every occurrence, so 1000 anonymous
        // mismatches produced zero crossings and zero rows, with no error anywhere.
        // CreateClient(), not GetRequiredService. Measured: resolving the detector
        // directly passes with `.ValidateOnStart()` DELETED, because the detector's own
        // constructor reads IOptions.Value and trips the same validator lazily — so the
        // earlier version of this case proved validation-on-first-use and called itself
        // "at boot". Starting the host is the only thing that proves the boot refusal.
        using var host = new MisconfiguredBurstFixture(
            "Tenancy:AssertionBurst:Window", "00:00:00");

        var act = () => host.CreateClient();

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void A_Threshold_Below_One_Is_Refused_At_Boot()
    {
        using var host = new MisconfiguredBurstFixture("Tenancy:AssertionBurst:Threshold", "0");

        var act = () => host.CreateClient();

        act.Should().Throw<OptionsValidationException>();
    }
}

/// <summary>The real host, with no recorder substituted.</summary>
/// <remarks>
/// Parameterless, because xUnit constructs an <c>IClassFixture</c> itself. The
/// misconfigured variants take their override through
/// <see cref="MisconfiguredBurstFixture"/>, which a case builds directly.
/// </remarks>
public sealed class RegisteredRecorderFixture : WebApplicationFactory<Program>;

/// <summary>The real host with one burst setting bent out of shape.</summary>
public sealed class MisconfiguredBurstFixture(string key, string value)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Only the setting under test. An explicit Deployment:Mode was here too and was
        // measured dead — WebApplicationFactory defaults to the Development environment,
        // which loads appsettings.Development.json, which carries it.
        builder.UseSetting(key, value);
    }
}

public class ResolvedTenantFixture : WebApplicationFactory<Program>
{
    public static readonly Guid TenantId = Guid.Parse("018f4d40-0000-7000-8000-0000000000aa");
    public static readonly Guid OrganizationId = Guid.Parse("018f4d40-0000-7000-8000-0000000000bb");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureTestServices(services =>
        {
            services.AddControllers(options =>
                    options.Conventions.Insert(0, new TestControllerFilter(
                        typeof(AssertionProbeController))))
                .AddApplicationPart(typeof(AssertionProbeController).Assembly);

            services.RemoveAll<ITenantContext>();
            services.AddScoped<ITenantContext>(_ => ResolvedContext.Instance);

            // The dimension a malformed header is counted under is otherwise
            // observable only as a metric label, and the parameter carrying it
            // was accepted and dropped once already.
            services.RemoveAll<ITenantAssertionRecorder>();
            services.AddSingleton<ITenantAssertionRecorder>(Recorder);
        });
    }

    /// <summary>What the middleware reported, for the test to read back.</summary>
    public SpyRecorder Recorder { get; } = new();

    public sealed class SpyRecorder : ITenantAssertionRecorder
    {
        private readonly List<TenantAssertionDimension> _unresolved = [];

        public IReadOnlyList<TenantAssertionDimension> Unresolved
        {
            get { lock (_unresolved) { return [.. _unresolved]; } }
        }

        public void Clear()
        {
            lock (_unresolved) { _unresolved.Clear(); }
            lock (_rejections) { _rejections.Clear(); }
        }

        private readonly List<TenantAssertionRejection> _rejections = [];

        /// <summary>What the middleware rejected, for a case that asserts the tier.</summary>
        public IReadOnlyList<TenantAssertionRejection> Rejections
        {
            get { lock (_rejections) { return [.. _rejections]; } }
        }

        public Task RecordRejectionAsync(TenantAssertionRejection rejection)
        {
            lock (_rejections) { _rejections.Add(rejection); }
            return Task.CompletedTask;
        }

        public void RecordUnresolved(TenantAssertionDimension dimension)
        {
            lock (_unresolved) { _unresolved.Add(dimension); }
        }
    }

    internal sealed class ResolvedContext : ITenantContext
    {
        public static ResolvedContext Instance { get; } = new();

        public bool IsResolved => true;
        // Fully qualified: each property's own name shadows its type here.
        public TenantId TenantId =>
            SharedKernel.Identifiers.TenantId.From(ResolvedTenantFixture.TenantId);

        public OrganizationId? OrganizationId =>
            SharedKernel.Identifiers.OrganizationId.From(ResolvedTenantFixture.OrganizationId);
        public UserId? UserId => null;
        public string? CorrelationId => null;
        public string? ModuleName => "integration-test";
    }
}

/// <summary>
/// Which dimension a malformed assertion is counted under.
/// </summary>
/// <remarks>
/// Its own fixture, because these read a recorder that every other test in the
/// class also writes to.
/// </remarks>
public sealed class TenantAssertionDimensionTests(DimensionFixture fixture)
    : IClassFixture<DimensionFixture>
{
    [Fact]
    public async Task A_Malformed_Organization_Header_Is_Counted_As_An_Organization()
    {
        // The two reads used to be short-circuited with `||`, so a valid tenant
        // beside a malformed organization still reported Tenant — the one thing
        // this counter exists to tell an operator.
        fixture.Recorder.Clear();
        using var client = fixture.CreateClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri("/api/v1/assertionprobe", UriKind.Relative));
        request.Headers.TryAddWithoutValidation(
            TenantAssertionMiddleware.TenantHeaderName, ResolvedTenantFixture.TenantId.ToString());
        request.Headers.TryAddWithoutValidation(
            TenantAssertionMiddleware.OrganizationHeaderName, "not-a-guid");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fixture.Recorder.Unresolved.Should().Equal(TenantAssertionDimension.Organization);
    }

    [Fact]
    public async Task A_Malformed_Tenant_Header_Is_Counted_As_A_Tenant()
    {
        fixture.Recorder.Clear();
        using var client = fixture.CreateClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri("/api/v1/assertionprobe", UriKind.Relative));
        request.Headers.TryAddWithoutValidation(
            TenantAssertionMiddleware.TenantHeaderName, "not-a-guid");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fixture.Recorder.Unresolved.Should().Equal(TenantAssertionDimension.Tenant);
    }
}

/// <summary>A resolved host whose recorder nothing else shares.</summary>
public sealed class DimensionFixture : ResolvedTenantFixture;


public sealed class AssertionProbeController : ApiControllerBase, ITestOnlyController
{
    [HttpGet]
    public IActionResult Get([FromServices] ITenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(tenantContext);
        return Ok(new { tenant = tenantContext.TenantId });
    }
}
