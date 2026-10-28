using System.Net;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Authentication;

public sealed class NativeSessionSourceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-28T12:00:00Z",
        System.Globalization.CultureInfo.InvariantCulture);
    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("01930000-0000-7000-8000-00000000aa01"));
    private static readonly OrganizationId Organization = OrganizationId.From(Guid.Parse("01930000-0000-7000-8000-00000000aa02"));
    private static readonly string CurrentKey = Key(1);
    private static readonly string PreviousKey = Key(2);
    private const string PublicKey = "public-hop-fixture-with-separate-purpose";
    private static readonly string[] PublicKeys = [PublicKey];

    [Fact]
    public void Public_only_configuration_has_no_native_credential_and_never_accepts_the_public_key()
    {
        var disabled = NativeSessionSourcePolicy.Create(null, null, null, PublicKeys, Now);
        disabled.IsConfigured.Should().BeFalse();
        disabled.Matches(CurrentKey, Now).Should().BeFalse();
        Policy().Matches(PublicKey, Now).Should().BeFalse();
    }

    [Theory]
    [InlineData("short")]
    [InlineData("padding")]
    [InlineData("constant")]
    [InlineData("alias")]
    [InlineData("public")]
    [InlineData("previous-only")]
    [InlineData("retirement-only")]
    [InlineData("missing-retirement")]
    [InlineData("same-purpose")]
    [InlineData("long-overlap")]
    [InlineData("non-utc")]
    public void Invalid_or_partial_configuration_refuses_without_echoing_values(string mutation)
    {
        string? current = CurrentKey;
        string? previous = null;
        string? retirement = null;
        IReadOnlyList<string> publicKeys = PublicKeys;
        switch (mutation)
        {
            case "short": current = "private-short-canary"; break;
            case "padding": current += "="; break;
            case "constant": current = WebEncoders.Base64UrlEncode(new byte[32]); break;
            case "alias": current = CurrentKey[..^1] + "B"; break;
            case "public": publicKeys = [CurrentKey]; break;
            case "previous-only": current = null; previous = PreviousKey; break;
            case "retirement-only": retirement = "2026-10-28T12:05:00Z"; break;
            case "missing-retirement": previous = PreviousKey; break;
            case "same-purpose": previous = CurrentKey; retirement = "2026-10-28T12:05:00Z"; break;
            case "long-overlap": previous = PreviousKey; retirement = "2026-10-28T12:15:01Z"; break;
            case "non-utc": previous = PreviousKey; retirement = "2026-10-28T15:05:00+03:00"; break;
            default: throw new InvalidOperationException("Unknown control.");
        }

        var action = () => NativeSessionSourcePolicy.Create(current, previous, retirement, publicKeys, Now);
        action.Should().Throw<InvalidOperationException>().WithMessage("Native session source configuration is invalid.");
    }

    [Fact]
    public void Previous_key_expires_at_the_absolute_boundary_and_restart_never_renews_it()
    {
        var policy = NativeSessionSourcePolicy.Create(CurrentKey, PreviousKey,
            "2026-10-28T12:05:00Z", PublicKeys, Now);
        policy.Matches(CurrentKey, Now.AddMinutes(6)).Should().BeTrue();
        policy.Matches(PreviousKey, Now.AddMinutes(4)).Should().BeTrue();
        policy.Matches(PreviousKey, Now.AddMinutes(5)).Should().BeFalse();
        var restarted = NativeSessionSourcePolicy.Create(CurrentKey, PreviousKey,
            "2026-10-28T12:05:00Z", PublicKeys, Now.AddMinutes(10));
        restarted.Matches(PreviousKey, Now.AddMinutes(10)).Should().BeFalse();
        policy.Matches(new StringValues([CurrentKey, PreviousKey]), Now).Should().BeFalse();
        policy.ToString().Should().Be(nameof(NativeSessionSourcePolicy));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Valid_source_keeps_tenantless_provider_facts_immutable_and_captures_orgless_or_branch(bool branch)
    {
        var http = Request(branch);
        var original = http.Features.Get<TenantCredentialFeature>()!.VerifiedSubject!;
        NativeSessionSourceFeature? captured = null;
        var middleware = new NativeSessionSourceMiddleware(context =>
        {
            context.Features.Get<TenantCredentialFeature>()!.VerifiedSubject.Should().BeSameAs(original);
            context.Features.Get<TenantCredentialFeature>()!.Identity.Should().BeNull();
            captured = context.Features.Get<NativeSessionSourceFeature>();
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(http, Hosts(), Policy(), new Clock());

        captured.Should().NotBeNull();
        captured!.Tenant.Should().Be(Tenant);
        captured.Organization.Should().Be(branch ? Organization : null);
        captured.IsCurrent(http).Should().BeTrue();
        captured.ToString().Should().Be(nameof(NativeSessionSourceFeature));
    }

    [Theory]
    [InlineData("source", 401)]
    [InlineData("hop", 401)]
    [InlineData("peer", 401)]
    [InlineData("quota", 401)]
    [InlineData("provenance", 401)]
    [InlineData("subject", 401)]
    [InlineData("absent-bearer", 404)]
    [InlineData("platform", 404)]
    [InlineData("tenant", 404)]
    [InlineData("uppercase", 400)]
    [InlineData("duplicate", 400)]
    [InlineData("missing-tenant", 400)]
    [InlineData("empty-org", 400)]
    [InlineData("reserved", 400)]
    [InlineData("foreign-header", 400)]
    public async Task Source_refusals_cannot_fall_back_or_reach_binding_io(string mutation, int status)
    {
        var http = Request();
        switch (mutation)
        {
            case "source": http.Request.Headers[NativeSessionSourcePolicy.SecretHeader] = PublicKey; break;
            case "hop": http.Request.Headers[TrustedHopOptions.SecretHeaderName] = "foreign"; break;
            case "peer": http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.5"); break;
            case "quota": http.Features.Set<SubjectAdmissionDecision>(null); break;
            case "provenance": http.Features.Set<TenantSubjectAdmissionProvenance>(null); break;
            case "subject": http.Features.Get<TenantCredentialFeature>()!.VerifiedSubject = Subject() with { Subject = "foreign" }; break;
            case "absent-bearer": http.Features.Get<TenantCredentialFeature>()!.VerifiedSubject = null; break;
            case "platform": http.Features.Set(HostClassification.Platform("tenant.example")); break;
            case "tenant": http.Request.Headers[NativeSessionSourcePolicy.TenantHeader] = "01930000-0000-7000-8000-00000000aa99"; break;
            case "uppercase": http.Request.Headers[NativeSessionSourcePolicy.TenantHeader] = Tenant.Value.ToString("D").ToUpperInvariant(); break;
            case "duplicate": http.Request.Headers[NativeSessionSourcePolicy.TenantHeader] = new StringValues([Tenant.Value.ToString("D"), Tenant.Value.ToString("D")]); break;
            case "missing-tenant": http.Request.Headers.Remove(NativeSessionSourcePolicy.TenantHeader); break;
            case "empty-org": http.Request.Headers[NativeSessionSourcePolicy.OrganizationHeader] = ""; break;
            case "reserved": http.Request.Headers[NativeSessionSourcePolicy.TenantHeader] = TenantId.PlatformSentinel.Value.ToString("D"); break;
            case "foreign-header": http.Request.Headers["X-LearnStack-Native-Role"] = "admin"; break;
            default: throw new InvalidOperationException("Unknown control.");
        }
        var middleware = new NativeSessionSourceMiddleware(_ => throw new InvalidOperationException("Binding I/O cannot run."));

        await middleware.InvokeAsync(http, Hosts(), Policy(), new Clock());

        http.Response.StatusCode.Should().Be(status);
        http.Response.Headers.CacheControl.ToString().Should().Contain("no-store");
        http.Features.Get<NativeSessionSourceFeature>().Should().BeNull();
    }

    [Fact]
    public async Task Source_evidence_expires_when_any_retained_authority_or_selector_is_replaced()
    {
        foreach (var mutation in new[] { "host", "subject", "quota", "provenance", "selector", "source", "path", "foreign-request", "cancel" })
        {
            var http = Request();
            await new NativeSessionSourceMiddleware(_ => Task.CompletedTask).InvokeAsync(http, Hosts(), Policy(), new Clock());
            var feature = http.Features.Get<NativeSessionSourceFeature>()!;
            var candidate = http;
            switch (mutation)
            {
                case "host": http.Features.Set(HostClassification.ForResolution("tenant.example", new HostResolution(Tenant, null))); break;
                case "subject": http.Features.Get<TenantCredentialFeature>()!.VerifiedSubject = Subject(); break;
                case "quota": http.Features.Set(new SubjectAdmissionDecision(SubjectAdmissionOutcome.Acquired, null)); break;
                case "provenance": http.Features.Set(new TenantSubjectAdmissionProvenance(http.Features.Get<TenantCredentialFeature>()!.VerifiedSubject!)); break;
                case "selector": http.Request.Headers[NativeSessionSourcePolicy.OrganizationHeader] = Organization.Value.ToString("D"); break;
                case "source": http.Request.Headers[NativeSessionSourcePolicy.SecretHeader] = PreviousKey; break;
                case "path": http.Request.Path = "/api/v1/identity/self"; break;
                case "foreign-request": candidate = Request(); break;
                case "cancel": http.RequestAborted = new CancellationToken(canceled: true); break;
            }
            feature.IsCurrent(candidate).Should().BeFalse(mutation);
        }
    }

    [Fact]
    public async Task Bound_native_actor_mints_a_live_candidate_without_claiming_provider_scope()
    {
        var http = Request();
        var actor = UserId.From(Guid.Parse("01930000-0000-7000-8000-00000000aa03"));
        var subject = http.Features.Get<TenantCredentialFeature>()!.VerifiedSubject;
        var dispatched = false;
        var binding = new TenantIdentityBindingAdmissionMiddleware(context =>
        {
            var normalized = context.Features.Get<TenantCredentialFeature>()!.Identity!;
            normalized.TenantId.Should().BeNull();
            normalized.OrganizationId.Should().BeNull();
            context.Features.Get<TenantCredentialFeature>()!.VerifiedSubject.Should().BeSameAs(subject);
            var native = context.Features.Get<NativeScopeEvidence>()!;
            native.Actor.Should().Be(actor);
            native.IsCurrent(context).Should().BeTrue();
            using var candidates = new TenantAuthorizationCandidateSource(new HttpContextAccessor { HttpContext = context });
            var candidate = candidates.ForValidatedScope(Tenant, null);
            candidate.Should().NotBeNull();
            candidate!.IsActive.Should().BeTrue();
            candidate.Actor.Should().Be(actor);
            context.Features.Get<TenantCredentialFeature>()!.Identity = normalized with { };
            candidate.IsActive.Should().BeFalse("the normalized binding instance is part of native provenance");
            dispatched = true;
            return Task.CompletedTask;
        }, NullLogger<TenantIdentityBindingAdmissionMiddleware>.Instance);
        var source = new NativeSessionSourceMiddleware(context => binding.InvokeAsync(context, new Resolver(actor)));

        await source.InvokeAsync(http, Hosts(), Policy(), new Clock());

        dispatched.Should().BeTrue();
    }

    [Theory]
    [InlineData("/api/v1/identity/self")]
    [InlineData("/api/v1/identity/self/bootstrap")]
    [InlineData("/api/v1/identity/self/contexts")]
    public async Task Missing_global_self_source_remains_masked_even_with_a_partial_selector_family(string path)
    {
        var http = Request();
        http.Request.Path = path;
        http.Request.Headers.Remove(NativeSessionSourcePolicy.SecretHeader);
        await new NativeSessionSourceMiddleware(_ => throw new InvalidOperationException("Binding cannot run."))
            .InvokeAsync(http, Hosts(), Policy(), new Clock());
        http.Response.StatusCode.Should().Be(404);
        http.Features.Get<NativeSessionSourceFeature>().Should().BeNull();
    }

    [Fact]
    public async Task Retargeting_to_another_valid_rotating_key_does_not_preserve_captured_source_provenance()
    {
        var http = Request();
        http.Request.Headers[NativeSessionSourcePolicy.SecretHeader] = PreviousKey;
        var policy = NativeSessionSourcePolicy.Create(CurrentKey, PreviousKey, "2026-10-28T12:05:00Z", PublicKeys, Now);
        await new NativeSessionSourceMiddleware(_ => Task.CompletedTask).InvokeAsync(http, Hosts(), policy, new Clock());
        var feature = http.Features.Get<NativeSessionSourceFeature>()!;
        feature.IsCurrent(http).Should().BeTrue();
        http.Request.Headers[NativeSessionSourcePolicy.SecretHeader] = CurrentKey;
        feature.IsCurrent(http).Should().BeFalse("the admitted credential cannot be replaced even by another valid key");
    }

    [Theory]
    [InlineData("/api/v1/identity/self")]
    [InlineData("/api/v1/identity/self/bootstrap")]
    [InlineData("/api/v1/identity/self/contexts")]
    public async Task Only_closed_global_self_paths_accept_a_source_without_selectors(string path)
    {
        var http = Request();
        http.Request.Path = path;
        http.Request.Headers.Remove(NativeSessionSourcePolicy.TenantHeader);
        await new NativeSessionSourceMiddleware(_ => Task.CompletedTask).InvokeAsync(http, Hosts(), Policy(), new Clock());
        var feature = http.Features.Get<NativeSessionSourceFeature>();
        feature.Should().NotBeNull();
        feature!.GlobalOperation.Should().BeSameAs(GlobalSelfOperation.ForPath(http.Request.Path));
        feature.IsCurrent(http).Should().BeTrue();
        feature.Tenant.Should().BeNull();

        http.Request.Headers[NativeSessionSourcePolicy.OrganizationHeader] = Organization.Value.ToString("D");
        await new NativeSessionSourceMiddleware(_ => throw new InvalidOperationException("Selectors are forbidden here."))
            .InvokeAsync(http, Hosts(), Policy(), new Clock());
        http.Response.StatusCode.Should().Be(400);
        http.Features.Get<NativeSessionSourceFeature>().Should().BeNull();
    }

    private static DefaultHttpContext Request(bool branch = false)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/v1/tenancy/account/site";
        http.Request.Host = new HostString("api.example");
        http.Connection.RemoteIpAddress = IPAddress.Loopback;
        http.Features.Set(HostClassification.ForResolution("tenant.example", new HostResolution(Tenant, branch ? Organization : null)));
        var subject = Subject();
        http.Features.Set(new TenantCredentialFeature { VerifiedSubject = subject });
        http.Features.Set(new TenantSubjectAdmissionProvenance(subject));
        http.Features.Set(new SubjectAdmissionDecision(SubjectAdmissionOutcome.Acquired, null));
        http.Request.Headers[TrustedHopOptions.HostHeaderName] = "tenant.example";
        http.Request.Headers[TrustedHopOptions.SecretHeaderName] = PublicKey;
        http.Request.Headers[NativeSessionSourcePolicy.SecretHeader] = CurrentKey;
        http.Request.Headers[NativeSessionSourcePolicy.TenantHeader] = Tenant.Value.ToString("D");
        if (branch) http.Request.Headers[NativeSessionSourcePolicy.OrganizationHeader] = Organization.Value.ToString("D");
        return http;
    }

    private static VerifiedProviderSubject Subject() => new("https://issuer.example/realms/learnstack", "subject-canary", null, null, null);
    private static NativeSessionSourcePolicy Policy() => NativeSessionSourcePolicy.Create(CurrentKey, null, null, PublicKeys, Now);
    private static EffectiveHostAccessor Hosts() => new(Options.Create(new TrustedHopOptions { Networks = ["127.0.0.0/8"], Secrets = PublicKeys }));
    private static string Key(byte start) => WebEncoders.Base64UrlEncode(Enumerable.Range(start, 32).Select(value => (byte)value).ToArray());
    private sealed class Clock : IClock { public DateTimeOffset UtcNow => Now; }
    private sealed class Resolver(UserId actor) : ITenantIdentityBindingResolver
    {
        public Task<IdentityBindingResult> ResolveExistingAsync(VerifiedProviderSubject subject,
            CancellationToken cancellationToken = default) => Task.FromResult(IdentityBindingResult.Bound(actor, created: false));
    }
}
