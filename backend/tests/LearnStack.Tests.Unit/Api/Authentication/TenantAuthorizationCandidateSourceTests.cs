using System.Security.Claims;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.Api.Tenancy;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Authentication;

public sealed class TenantAuthorizationCandidateSourceTests
{
    private static readonly UserId Actor = UserId.From(Guid.Parse("01930000-0000-7000-8000-00000000aa01"));
    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("01930000-0000-7000-8000-00000000aa02"));
    private static readonly OrganizationId Branch = OrganizationId.From(Guid.Parse("01930000-0000-7000-8000-00000000aa03"));

    [Fact]
    public async Task Quota_and_existing_binding_supply_actor_without_a_wire_actor_claim()
    {
        var http = await AdmittedAsync();
        using var source = Source(http);

        var candidate = source.ForValidatedScope(Tenant, Branch);

        candidate.Should().NotBeNull();
        candidate!.Actor.Should().Be(Actor);
        candidate.TenantId.Should().Be(Tenant);
        candidate.OrganizationId.Should().Be(Branch);
        candidate.SourceKind.Should().Be(AuthorizationSourceKind.Http);
        candidate.IsActive.Should().BeTrue();
        candidate.ToString().Should().Be(nameof(AuthorizationCandidate));
    }

    [Fact]
    public void A_claims_principal_does_not_substitute_for_normalized_binding_provenance()
    {
        var http = new DefaultHttpContext();
        http.Features.Set(HostClassification.Platform("platform.example"));
        http.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("user_id", Actor.Value.ToString("D")),
            new Claim("tenant_id", Tenant.Value.ToString("D")),
            new Claim("organization_id", Branch.Value.ToString("D"))], "test"));
        using var source = Source(http);

        source.ForValidatedScope(Tenant, Branch).Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_org_claim_uses_only_the_independently_classified_host_question(bool branchHost)
    {
        var http = await AdmittedAsync(includeOrganizationClaim: false);
        http.Features.Set(HostClassification.ForResolution("tenant.example",
            new HostResolution(Tenant, branchHost ? Branch : null)));
        using var source = Source(http);

        var candidate = source.ForValidatedScope(Tenant, branchHost ? Branch : null);

        candidate.Should().NotBeNull();
        candidate!.OrganizationId.Should().Be(branchHost ? Branch : null);
        if (!branchHost) source.ForValidatedScope(Tenant, Branch).Should().BeNull();
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("subject")]
    [InlineData("quota")]
    [InlineData("quota_refused")]
    [InlineData("provenance")]
    public async Task Missing_or_conflicting_admission_evidence_cannot_mint(string change)
    {
        var http = await AdmittedAsync();
        var feature = http.Features.Get<TenantCredentialFeature>()!;
        if (change == "binding") feature.Identity = null;
        if (change == "subject") feature.VerifiedSubject = feature.VerifiedSubject! with { Subject = "different-subject" };
        if (change == "quota") http.Features.Set<SubjectAdmissionDecision>(null);
        if (change == "quota_refused") http.Features.Set(new SubjectAdmissionDecision(SubjectAdmissionOutcome.QuotaExhausted, null));
        if (change == "provenance") http.Features.Set<TenantSubjectAdmissionProvenance>(null);
        using var source = Source(http);

        source.ForValidatedScope(Tenant, Branch).Should().BeNull();
    }

    [Fact]
    public async Task Foreign_or_wider_questions_and_disagreeing_hosts_cannot_mint()
    {
        var http = await AdmittedAsync();
        using var source = Source(http);
        var otherTenant = TenantId.From(Guid.CreateVersion7());
        var otherBranch = OrganizationId.From(Guid.CreateVersion7());

        source.ForValidatedScope(otherTenant, Branch).Should().BeNull();
        source.ForValidatedScope(Tenant, null).Should().BeNull();
        source.ForValidatedScope(Tenant, otherBranch).Should().BeNull();
        http.Features.Set(HostClassification.ForResolution("foreign.example", new HostResolution(otherTenant, null)));
        source.ForValidatedScope(Tenant, Branch).Should().BeNull();
        http.Features.Set(HostClassification.ForResolution("branch.example", new HostResolution(Tenant, otherBranch)));
        source.ForValidatedScope(Tenant, Branch).Should().BeNull();
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("subject")]
    [InlineData("quota")]
    [InlineData("host")]
    [InlineData("request")]
    [InlineData("cancelled")]
    [InlineData("disposed")]
    public async Task Captured_candidate_loses_authority_when_its_actual_source_changes(string change)
    {
        var http = await AdmittedAsync();
        var accessor = new HttpContextAccessor { HttpContext = http };
        using var source = new TenantAuthorizationCandidateSource(accessor);
        var candidate = source.ForValidatedScope(Tenant, Branch)!;
        candidate.IsActive.Should().BeTrue();
        var feature = http.Features.Get<TenantCredentialFeature>()!;
        if (change == "binding") feature.Identity = feature.Identity! with { ActorUserId = UserId.From(Guid.CreateVersion7()) };
        if (change == "subject") feature.VerifiedSubject = feature.VerifiedSubject! with { Subject = "replacement" };
        if (change == "quota") http.Features.Set(new SubjectAdmissionDecision(SubjectAdmissionOutcome.Acquired, null));
        if (change == "host") http.Features.Set(HostClassification.Platform("replacement.example"));
        if (change == "request") accessor.HttpContext = new DefaultHttpContext();
        if (change == "cancelled") http.RequestAborted = new CancellationToken(canceled: true);
        if (change == "disposed") source.Dispose();

        candidate.IsActive.Should().BeFalse();
    }

    private static TenantAuthorizationCandidateSource Source(HttpContext http) =>
        new(new HttpContextAccessor { HttpContext = http });

    private static async Task<DefaultHttpContext> AdmittedAsync(bool includeOrganizationClaim = true)
    {
        var http = new DefaultHttpContext();
        http.Features.Set(HostClassification.Platform("platform.example"));
        http.Features.Set(new TenantCredentialFeature
        {
            VerifiedSubject = new VerifiedProviderSubject("https://issuer.example/realms/learnstack",
                "subject-without-actor", null, Tenant, includeOrganizationClaim ? Branch : null)
        });
        var binding = new TenantIdentityBindingAdmissionMiddleware(_ => Task.CompletedTask,
            NullLogger<TenantIdentityBindingAdmissionMiddleware>.Instance);
        await using var limiter = new TenantSubjectRateLimiter();
        var admission = new TenantSubjectAdmissionMiddleware(context => binding.InvokeAsync(context, new ExistingBinding()), limiter);
        await admission.InvokeAsync(http);
        http.Features.Get<TenantCredentialFeature>()!.Identity.Should().NotBeNull();
        return http;
    }

    private sealed class ExistingBinding : ITenantIdentityBindingResolver
    {
        public Task<IdentityBindingResult> ResolveExistingAsync(VerifiedProviderSubject subject,
            CancellationToken cancellationToken = default) => Task.FromResult(IdentityBindingResult.Bound(Actor, created: false));
    }
}
