using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Authentication;

public sealed class TenantSurfaceAdmissionTests
{
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly OrganizationId Organization = OrganizationId.From(Guid.CreateVersion7());
    private static readonly UserId Actor = UserId.From(Guid.CreateVersion7());

    [Theory]
    [InlineData("actor")]
    [InlineData("actor-null")]
    [InlineData("actor-default")]
    [InlineData("actor-nil")]
    [InlineData("actor-system")]
    [InlineData("tenant")]
    [InlineData("tenant-default")]
    [InlineData("tenant-nil")]
    [InlineData("tenant-sentinel")]
    [InlineData("organization")]
    [InlineData("invented-organization")]
    [InlineData("organization-default")]
    [InlineData("organization-nil")]
    [InlineData("host-organization")]
    [InlineData("host-absent")]
    [InlineData("host-only")]
    [InlineData("unresolved")]
    public async Task Independent_identity_context_disagreements_refuse_before_dispatch(string fault)
    {
        var context = Resolved();
        var identity = Identity();
        switch (fault)
        {
            case "actor": context = context with { UserId = UserId.From(Guid.CreateVersion7()) }; break;
            case "actor-null": context = context with { UserId = null }; break;
            case "actor-default": context = context with { UserId = (new UserId[1])[0] }; break;
            case "actor-nil": context = context with { UserId = UserId.From(Guid.Empty) }; break;
            case "actor-system": context = context with { UserId = UserId.SystemActor }; break;
            case "tenant": identity = identity with { TenantId = TenantId.From(Guid.CreateVersion7()) }; break;
            case "tenant-default": context = context with { TenantId = (new TenantId[1])[0] }; break;
            case "tenant-nil": context = context with { TenantId = TenantId.From(Guid.Empty) }; break;
            case "tenant-sentinel": context = context with { TenantId = TenantId.PlatformSentinel }; break;
            case "invented-organization":
                identity = identity with { OrganizationId = null };
                context = context with { HostScope = Host(null) };
                break;
            case "organization": identity = identity with { OrganizationId = OrganizationId.From(Guid.CreateVersion7()) }; break;
            case "organization-default": context = context with { OrganizationId = (new OrganizationId[1])[0] }; break;
            case "organization-nil": context = context with { OrganizationId = OrganizationId.From(Guid.Empty) }; break;
            case "host-organization": context = context with { HostScope = Host(OrganizationId.From(Guid.CreateVersion7())) }; break;
            case "host-absent": context = context with { HostScope = null }; break;
            case "host-only": context = context with { Origin = TenantContextOrigin.HostOnly }; break;
            case "unresolved": context = context with { IsResolved = false }; break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
        await Invoke(context, identity, expectedCalls: 0);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("HEAD")]
    public async Task Path_fallback_requires_authority_for_every_method(string method)
    {
        await Invoke(Resolved(), null, expectedCalls: 0, method);
        await Invoke(Resolved(), Identity(), expectedCalls: 1, method);
    }

    [Fact]
    public async Task Matching_claim_and_host_scopes_allow_dispatch_including_host_supplied_organization()
    {
        await Invoke(Resolved() with { HostScope = Host(null), OrganizationId = null },
            Identity() with { OrganizationId = null }, expectedCalls: 1);
        await Invoke(Resolved(), Identity(), expectedCalls: 1);
        await Invoke(Resolved(), Identity() with { OrganizationId = null }, expectedCalls: 1);
        await Invoke(Resolved() with { HostScope = Host(null) }, Identity(), expectedCalls: 1);
    }

    private static async Task Invoke(Context context, ValidatedTenantIdentity? identity, int expectedCalls, string method = "GET")
    {
        var http = new DefaultHttpContext();
        http.Request.Path = TenantSurfaceAdmissionMiddleware.AccountSitePath;
        http.Request.Method = method;
        http.Features.Set(HostClassification.Platform("platform.example"));
        http.Features.Set(new TenantCredentialFeature { Identity = identity });
        var calls = 0;
        var middleware = new TenantSurfaceAdmissionMiddleware(_ => { calls++; return Task.CompletedTask; });
        await middleware.InvokeAsync(http, context);
        calls.Should().Be(expectedCalls);
        http.Response.StatusCode.Should().Be(expectedCalls == 0 ? 404 : 200);
    }

    private static ValidatedTenantIdentity Identity() => new("https://issuer.example/realms/learnstack", "subject", Actor, Tenant, Organization);
    private static Context Resolved() => new() { HostScope = Host(Organization) };
    private static HostScope Host(OrganizationId? organization) => TenantContextFactory.Create(new TenantResolutionAttempt
    { HostTenantId = Tenant, HostOrganizationId = organization }).Value!.HostScope!;
    private sealed record Context : ITenantContext
    {
        public bool IsResolved { get; init; } = true;
        public TenantId TenantId { get; init; } = Tenant;
        public OrganizationId? OrganizationId { get; init; } = Organization;
        public UserId? UserId { get; init; } = Actor;
        public TenantContextOrigin? Origin { get; init; } = TenantContextOrigin.HostAndClaim;
        public HostScope? HostScope { get; init; }
        public string? CorrelationId => null;
        public string? ModuleName => null;
    }
}
