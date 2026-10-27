using System.Net;
using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using LearnStack.Api.Account;
using LearnStack.Api.Authentication;
using LearnStack.Api.Tenancy;
using LearnStack.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.Account;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Authentication;

/// <summary>Staged source-unit proof; these adapters are not HTTP-matrix activation.</summary>
public sealed class AuthorizationEvidenceApiSourceTests
{
    private static readonly UserId Actor = UserId.From(Guid.Parse("01930000-0000-7000-8000-00000000ee01"));
    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("01930000-0000-7000-8000-00000000ee02"));
    private static readonly OrganizationId Branch = OrganizationId.From(Guid.Parse("01930000-0000-7000-8000-00000000ee03"));
    private const string Secret = "evidence-test-only-visitor-secret-32-characters";
    private const string Canary = "raw-url-query-or-claim-canary";

    [Fact]
    public async Task Transport_reuses_the_admitted_visitor_and_canonical_endpoint_instead_of_reparsing_mutated_headers_or_peer()
    {
        var http = await AdmittedAsync(hostBacked: true);
        CaptureVisitor(http);
        http.Request.Headers[AnonymousRequestIdentity.VisitorHeaderName] = "198.51.100.21";
        http.Request.Headers["X-Forwarded-For"] = "198.51.100.22";
        http.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.23");
        var accessor = new HttpContextAccessor { HttpContext = http };
        using var candidates = new TenantAuthorizationCandidateSource(accessor);
        using var source = new HttpAuthorizationTransportSource(accessor, Classifier());
        using var invocationOwner = new InvocationSource();
        var invocation = invocationOwner.Mint(Context(hostBacked: true), candidates.ForValidatedScope(Tenant, Branch)!);

        var captured = source.Capture(invocation);

        captured.Should().NotBeNull();
        captured!.Address.Should().Be(IPAddress.Parse("203.0.113.19"));
        captured.Route.Should().Be("GetAccountSite");
        captured.Matches(invocation).Should().BeTrue();
        captured.ToString().Should().Be(nameof(OperationTransportProvenance));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transport_falls_back_to_the_physical_peer_when_no_usable_visitor_was_admitted(bool refusedVisitor)
    {
        var http = await AdmittedAsync(hostBacked: true);
        http.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.1");
        http.Request.Headers["X-Forwarded-For"] = "198.51.100.55";
        http.Request.Headers[AnonymousRequestIdentity.VisitorHeaderName] = "198.51.100.56";
        if (refusedVisitor)
        {
            http.Request.Headers[TrustedHopOptions.SecretHeaderName] = Secret;
            http.Request.Headers[AnonymousRequestIdentity.VisitorHeaderName] = "invalid-visitor";
            VisitorIdentity().For(http).Refuse.Should().BeTrue();
        }
        var accessor = new HttpContextAccessor { HttpContext = http };
        using var candidates = new TenantAuthorizationCandidateSource(accessor);
        using var source = new HttpAuthorizationTransportSource(accessor, Classifier());
        using var owner = new InvocationSource();
        source.Capture(owner.Mint(Context(hostBacked: true), candidates.ForValidatedScope(Tenant, Branch)!))!
            .Address.Should().Be(IPAddress.Loopback);
    }

    [Fact]
    public async Task Transport_bounds_and_sanitizes_user_agent_and_keeps_raw_route_query_and_provider_claims_out_of_its_snapshot()
    {
        var http = await AdmittedAsync(hostBacked: true);
        http.Request.Path = "/" + Canary;
        http.Request.QueryString = new QueryString("?secret=" + Canary);
        http.Request.Headers.UserAgent = "\0Browser\r\n" + new string('x', 400);
        http.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Email, Canary + "@example.invalid"), new Claim("operator", "true"),
            new Claim(ClaimTypes.Role, Canary), new Claim("realm_access", Canary)], "fixture"));
        var accessor = new HttpContextAccessor { HttpContext = http };
        using var candidates = new TenantAuthorizationCandidateSource(accessor);
        using var source = new HttpAuthorizationTransportSource(accessor, Classifier());
        using var owner = new InvocationSource();
        var captured = source.Capture(owner.Mint(Context(hostBacked: true), candidates.ForValidatedScope(Tenant, Branch)!))!;

        captured.UserAgent.Should().HaveLength(256).And.StartWith("Browser");
        captured.UserAgent!.Any(char.IsControl).Should().BeFalse();
        captured.Route.Should().Be("GetAccountSite").And.NotContain(Canary);
        typeof(OperationTransportProvenance).GetProperties().Select(property => property.Name)
            .Should().BeEquivalentTo([nameof(captured.Route), nameof(captured.Address), nameof(captured.UserAgent)]);
        new[] { captured.Route, captured.Address?.ToString(), captured.UserAgent, captured.ToString() }
            .Should().OnlyContain(value => value == null || !value.Contains(Canary, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("method")]
    [InlineData("descriptor")]
    public async Task Transport_requires_the_actual_canonical_account_endpoint_metadata(string changed)
    {
        var http = await AdmittedAsync(hostBacked: true);
        http.SetEndpoint(Endpoint(changed));
        var accessor = new HttpContextAccessor { HttpContext = http };
        using var candidates = new TenantAuthorizationCandidateSource(accessor);
        using var source = new HttpAuthorizationTransportSource(accessor, Classifier());
        using var owner = new InvocationSource();
        source.Capture(owner.Mint(Context(hostBacked: true), candidates.ForValidatedScope(Tenant, Branch)!)).Should().BeNull();
    }

    [Fact]
    public async Task A_bare_principal_cannot_replace_normalized_binding_for_either_source()
    {
        var http = await AdmittedAsync();
        var accessor = new HttpContextAccessor { HttpContext = http };
        using var candidates = new TenantAuthorizationCandidateSource(accessor);
        var candidate = candidates.ForValidatedScope(Tenant, Branch)!;
        using var invocationOwner = new InvocationSource();
        var invocation = invocationOwner.Mint(Context(), candidate);
        http.Features.Get<TenantCredentialFeature>()!.Identity = null;
        http.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("user_id", Actor.Value.ToString("D")), new Claim("tenant_id", Tenant.Value.ToString("D")),
            new Claim("organization_id", Branch.Value.ToString("D")), new Claim("operator", "true")], "fixture"));
        using var transport = new HttpAuthorizationTransportSource(accessor, Classifier());
        using var assertions = new ConfirmedClaimAssertionSource(accessor, candidates, Classifier());

        candidate.IsActive.Should().BeFalse();
        transport.Capture(invocation).Should().BeNull();
        assertions.Capture(Context(), ClaimAssertionDimension.Tenant).Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Claim_assertion_capture_requires_actual_claim_and_membership_origin(bool anonymousHost)
    {
        var http = await AdmittedAsync();
        var accessor = new HttpContextAccessor { HttpContext = http };
        using var candidates = new TenantAuthorizationCandidateSource(accessor);
        using var source = new ConfirmedClaimAssertionSource(accessor, candidates, Classifier());
        var context = anonymousHost
            ? TenantContextFactory.Create(new TenantResolutionAttempt { HostTenantId = Tenant, HostOrganizationId = Branch }).Value!
            : Context(hostBacked: true);
        context.Origin.Should().NotBe(TenantContextOrigin.ClaimAndMembership);
        source.Capture(context, ClaimAssertionDimension.Tenant).Should().BeNull();
    }

    [Theory]
    [InlineData(ClaimAssertionDimension.Tenant)]
    [InlineData(ClaimAssertionDimension.Organization)]
    public async Task Confirmed_assertion_freezes_only_the_admitted_scope_dimension_and_transport_without_asserted_UUIDs(ClaimAssertionDimension dimension)
    {
        var http = await AdmittedAsync();
        CaptureVisitor(http);
        var asserted = Guid.CreateVersion7().ToString("D");
        http.Request.Headers["X-Tenant-Id"] = asserted;
        http.Request.Headers["X-Organization-Id"] = asserted;
        http.Request.Path = "/" + Canary;
        http.Request.QueryString = new QueryString("?claim=" + Canary);
        http.Request.Headers.UserAgent = "\0AssertionAgent\r\n" + new string('a', 400);
        var accessor = new HttpContextAccessor { HttpContext = http };
        using var candidates = new TenantAuthorizationCandidateSource(accessor);
        using var source = new ConfirmedClaimAssertionSource(accessor, candidates, Classifier());
        var context = Context();
        context.Origin.Should().Be(TenantContextOrigin.ClaimAndMembership);
        candidates.ForValidatedScope(Tenant, Branch)!.IsActive.Should().BeTrue();

        var captured = source.Capture(context, dimension)!;

        captured.TenantId.Should().Be(Tenant);
        captured.OrganizationId.Should().Be(Branch);
        captured.Actor.Should().Be(Actor);
        captured.Dimension.Should().Be(dimension);
        captured.Route.Should().Be("GetAccountSite");
        captured.Address.Should().Be(IPAddress.Parse("203.0.113.19"));
        captured.CorrelationId.Should().Be("staged-claim-assertion-proof");
        captured.UserAgent.Should().HaveLength(256).And.StartWith("AssertionAgent");
        captured.UserAgent!.Any(char.IsControl).Should().BeFalse();
        captured.ToString().Should().Be(nameof(ConfirmedClaimAssertion));
        typeof(ConfirmedClaimAssertion).GetProperties().Select(property => property.Name).Should().NotContain(
            name => name.Contains("Asserted", StringComparison.Ordinal) || name.Contains("Email", StringComparison.Ordinal)
                || name.Contains("Operator", StringComparison.Ordinal));
        new[] { captured.TenantId.Value.ToString("D"), captured.OrganizationId?.Value.ToString("D"),
            captured.Actor.Value.ToString("D"), captured.Route, captured.UserAgent, captured.CorrelationId }
            .Should().OnlyContain(value => value == null || !value.Contains(asserted, StringComparison.Ordinal)
                && !value.Contains(Canary, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Assertion_reentry_uses_the_same_decided_reference_and_never_enriches_it_from_later_headers()
    {
        var http = await AdmittedAsync();
        CaptureVisitor(http);
        http.Request.Headers.UserAgent = "first-observation";
        var accessor = new HttpContextAccessor { HttpContext = http };
        using var candidates = new TenantAuthorizationCandidateSource(accessor);
        using var source = new ConfirmedClaimAssertionSource(accessor, candidates, Classifier());
        var context = Context();
        var first = source.Capture(context, ClaimAssertionDimension.Tenant)!;
        http.Request.Headers["X-Tenant-Id"] = Guid.CreateVersion7().ToString("D");
        http.Request.Headers.UserAgent = "later-header-canary";
        http.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.88");
        var repeated = source.Capture(context, ClaimAssertionDimension.Tenant);
        repeated.Should().BeSameAs(first);
        repeated!.UserAgent.Should().Be("first-observation");
        repeated.Address.Should().Be(IPAddress.Parse("203.0.113.19"));
        var otherDimension = source.Capture(context, ClaimAssertionDimension.Organization)!;
        otherDimension.Should().NotBeSameAs(first);
        otherDimension.Dimension.Should().Be(ClaimAssertionDimension.Organization);
        source.Capture(context, ClaimAssertionDimension.Organization).Should().BeSameAs(otherDimension);
        source.Dispose();
        first.IsActive.Should().BeFalse();
        otherDimension.IsActive.Should().BeFalse();
        source.Capture(context, ClaimAssertionDimension.Tenant).Should().BeNull();
        source.Capture(context, ClaimAssertionDimension.Organization).Should().BeNull();
    }

    [Fact]
    public async Task A_replacement_request_cannot_retarget_an_existing_assertion_source_even_with_the_same_normalized_scope()
    {
        var original = await AdmittedAsync();
        var accessor = new HttpContextAccessor { HttpContext = original };
        using var candidates = new TenantAuthorizationCandidateSource(accessor);
        using var source = new ConfirmedClaimAssertionSource(accessor, candidates, Classifier());
        var context = Context();
        var captured = source.Capture(context, ClaimAssertionDimension.Tenant)!;
        accessor.HttpContext = await AdmittedAsync();
        candidates.ForValidatedScope(Tenant, Branch)!.IsActive.Should().BeTrue("the replacement is independently normalized");
        source.Capture(context, ClaimAssertionDimension.Tenant).Should().BeNull();
        source.Capture(context, ClaimAssertionDimension.Organization).Should().BeNull();
        captured.IsActive.Should().BeTrue("the already decided observation is retained for its owned evidence write");
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("candidate_closed")]
    public async Task Request_or_candidate_revocation_refuses_new_capture_without_erasing_a_decided_MUST_observation(string changed)
    {
        var http = await AdmittedAsync();
        var accessor = new HttpContextAccessor { HttpContext = http };
        using var candidates = new TenantAuthorizationCandidateSource(accessor);
        using var source = new ConfirmedClaimAssertionSource(accessor, candidates, Classifier());
        var context = Context();
        var captured = source.Capture(context, ClaimAssertionDimension.Tenant)!;
        if (changed == "cancelled") http.RequestAborted = new CancellationToken(canceled: true);
        else candidates.Dispose();
        source.Capture(context, ClaimAssertionDimension.Tenant).Should().BeNull();
        source.Capture(context, ClaimAssertionDimension.Organization).Should().BeNull();
        captured.IsActive.Should().BeTrue("cancellation cannot erase a decided refusal's MUST observation");
        source.Dispose();
        captured.IsActive.Should().BeFalse("closing the observation source invalidates its minting reference");
    }

    [Fact]
    public async Task Closing_invocation_or_transport_source_invalidates_a_captured_transport_reference()
    {
        var http = await AdmittedAsync(hostBacked: true);
        var accessor = new HttpContextAccessor { HttpContext = http };
        using var candidates = new TenantAuthorizationCandidateSource(accessor);
        using var source = new HttpAuthorizationTransportSource(accessor, Classifier());
        using var owner = new InvocationSource();
        var invocation = owner.Mint(Context(hostBacked: true), candidates.ForValidatedScope(Tenant, Branch)!);
        var captured = source.Capture(invocation)!;
        captured.Matches(invocation).Should().BeTrue();
        owner.Dispose();
        captured.Matches(invocation).Should().BeFalse();
        source.Capture(invocation).Should().BeNull();
        using var secondOwner = new InvocationSource();
        var second = secondOwner.Mint(Context(hostBacked: true), candidates.ForValidatedScope(Tenant, Branch)!);
        var secondCapture = source.Capture(second)!;
        source.Dispose();
        secondCapture.Matches(second).Should().BeFalse();
        var remint = () => source.Capture(second);
        remint.Should().Throw<ObjectDisposedException>();
    }

    private static async Task<DefaultHttpContext> AdmittedAsync(bool hostBacked = false)
    {
        var http = new DefaultHttpContext();
        http.SetEndpoint(Endpoint());
        http.Features.Set(hostBacked
            ? HostClassification.ForResolution("tenant.example", new HostResolution(Tenant, Branch))
            : HostClassification.Platform("platform.example"));
        http.Features.Set(new TenantCredentialFeature
        {
            VerifiedSubject = new VerifiedProviderSubject("https://issuer.example/realms/learnstack",
                "binding-normalized-subject", null, Tenant, Branch),
        });
        var binding = new TenantIdentityBindingAdmissionMiddleware(_ => Task.CompletedTask,
            NullLogger<TenantIdentityBindingAdmissionMiddleware>.Instance);
        await using var limiter = new TenantSubjectRateLimiter();
        var admission = new TenantSubjectAdmissionMiddleware(context => binding.InvokeAsync(context, new ExistingBinding()), limiter);
        await admission.InvokeAsync(http);
        http.Features.Get<TenantCredentialFeature>()!.Identity.Should().NotBeNull();
        http.Features.Get<TenantSubjectAdmissionProvenance>().Should().NotBeNull();
        return http;
    }

    private static TenantContext Context(bool hostBacked = false) => TenantContextFactory.Create(new TenantResolutionAttempt
    {
        HasValidatedPrincipal = true,
        UserId = Actor,
        ClaimTenantId = Tenant,
        ClaimOrganizationId = Branch,
        HostTenantId = hostBacked ? Tenant : null,
        HostOrganizationId = hostBacked ? Branch : null,
        MembershipCovers = true,
        ClaimedOrganizationBelongsToTenant = true,
        CorrelationId = "staged-claim-assertion-proof",
    }).Value!;

    private static OperationHttpClassifier Classifier() => new(new OperationPolicyCatalog([new TenancyOperationPolicySource()]));

    private static Endpoint Endpoint(string? changed = null)
    {
        var method = typeof(AccountController).GetMethod(nameof(AccountController.GetSite))!;
        var metadata = new List<object>
        {
            method.GetCustomAttribute<OperationPolicyAttribute>()!,
            new EndpointNameMetadata(changed == "name" ? "UnadmittedName" : "GetAccountSite"),
            new HttpMethodMetadata(changed == "method" ? ["POST"] : ["GET"]),
        };
        if (changed != "descriptor") metadata.Add(new ControllerActionDescriptor
        {
            ControllerTypeInfo = typeof(AccountController).GetTypeInfo(),
            MethodInfo = method,
            ControllerName = "Account",
            ActionName = nameof(AccountController.GetSite),
        });
        var endpoint = new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), Canary);
        if (changed is null)
        {
            Classifier().Classify(endpoint, out var policy).Should().Be(HttpOperationClassification.Application);
            policy!.RequestType.Should().Be<GetAccountSiteQuery>();
        }
        return endpoint;
    }

    private static AnonymousRequestIdentity VisitorIdentity() => new(new EffectiveHostAccessor(Options.Create(
        new TrustedHopOptions { Networks = ["127.0.0.1/32"], Secrets = [Secret] })));

    private static void CaptureVisitor(HttpContext http)
    {
        http.Connection.RemoteIpAddress = IPAddress.Loopback;
        http.Request.Headers[TrustedHopOptions.SecretHeaderName] = Secret;
        http.Request.Headers[AnonymousRequestIdentity.VisitorHeaderName] = "203.0.113.19";
        VisitorIdentity().For(http).Refuse.Should().BeFalse();
    }

    private sealed class ExistingBinding : ITenantIdentityBindingResolver
    {
        public Task<IdentityBindingResult> ResolveExistingAsync(VerifiedProviderSubject subject, CancellationToken cancellationToken = default) =>
            Task.FromResult(IdentityBindingResult.Bound(Actor, created: false));
    }

    private sealed class InvocationSource : OperationInvocation.MintingSource, IDisposable
    {
        private bool _closed;
        private OperationInvocation? _current;
        internal OperationInvocation Mint(ITenantContext context, AuthorizationCandidate candidate)
        {
            new OperationPolicyCatalog([new TenancyOperationPolicySource()]).TryGet(typeof(GetAccountSiteQuery), out var policy).Should().BeTrue();
            return _current = ForInvocation(new GetAccountSiteQuery(), policy, context, candidate, null, CancellationToken.None);
        }
        protected override bool IsCurrent(OperationInvocation invocation) => !_closed && ReferenceEquals(_current, invocation);
        public void Dispose() => _closed = true;
    }
}
