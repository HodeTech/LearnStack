using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Authentication;

/// <summary>ADR-0107 § 4: a download ticket binds the validated credential, actor and group of the request that minted it.</summary>
public sealed class HttpDataExportSessionBindingTests
{
    private static readonly UserId Actor = UserId.From(Guid.CreateVersion7());
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly OrganizationId Organization = OrganizationId.From(Guid.CreateVersion7());

    [Fact]
    public void The_binding_is_a_stable_digest_of_the_presented_credential_actor_and_group_and_never_the_credential()
    {
        var binding = Capture(Request("token-one"));
        binding.Should().MatchRegex("^[0-9a-f]{64}$");
        binding.Should().NotContain("token-one");
        Capture(Request("token-one")).Should().Be(binding, "the same session binds the same way on every request");
        Capture(Request("token-two")).Should().NotBe(binding, "another session's credential binds differently");
        Capture(Request("token-one"), organization: Organization).Should().NotBe(binding, "the group is bound");
        Capture(Request("token-one"), tenant: TenantId.From(Guid.CreateVersion7())).Should().NotBe(binding);
    }

    [Theory]
    [InlineData("no-credential")]
    [InlineData("other-actor")]
    [InlineData("two-headers")]
    [InlineData("not-bearer")]
    [InlineData("empty-bearer")]
    [InlineData("aborted")]
    [InlineData("no-context")]
    public void Without_one_validated_bearer_credential_of_the_same_actor_there_is_no_binding(string variant)
    {
        var http = variant == "no-context" ? null : Request("token-one");
        switch (variant)
        {
            case "no-credential": http!.Features.Set<TenantCredentialFeature>(null); break;
            case "other-actor":
                http!.Features.Get<TenantCredentialFeature>()!.Identity = new ValidatedTenantIdentity("https://issuer.test/realms/learnstack",
                    "subject", UserId.From(Guid.CreateVersion7()), Tenant, null);
                break;
            case "two-headers": http!.Request.Headers.Authorization = new(["Bearer token-one", "Bearer token-two"]); break;
            case "not-bearer": http!.Request.Headers.Authorization = "Basic token-one"; break;
            case "empty-bearer": http!.Request.Headers.Authorization = "Bearer "; break;
            case "aborted":
                using (var source = new CancellationTokenSource())
                {
                    source.Cancel();
                    http!.RequestAborted = source.Token;
                }
                break;
        }

        new HttpDataExportSessionBinding(new HttpContextAccessor { HttpContext = http }).Capture(Actor, Tenant, null).Should().BeNull();
    }

    private static string? Capture(DefaultHttpContext http, TenantId? tenant = null, OrganizationId? organization = null) =>
        new HttpDataExportSessionBinding(new HttpContextAccessor { HttpContext = http }).Capture(Actor, tenant ?? Tenant, organization);

    private static DefaultHttpContext Request(string token)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = "Bearer " + token;
        http.Features.Set(new TenantCredentialFeature
        {
            VerifiedSubject = new VerifiedProviderSubject("https://issuer.test/realms/learnstack", "subject", Actor, Tenant, null),
            Identity = new ValidatedTenantIdentity("https://issuer.test/realms/learnstack", "subject", Actor, Tenant, null),
        });
        return http;
    }
}
