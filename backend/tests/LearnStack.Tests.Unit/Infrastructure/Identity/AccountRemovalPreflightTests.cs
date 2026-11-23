using System.Net;
using FluentAssertions;
using LearnStack.Infrastructure.Identity.AccountRemoval;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Resilience;
using LearnStack.SharedKernel.Secrets;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Identity;

/// <summary>
/// ADR-0107 § 7: the account-removal port's local preflight, which a closure's Begin asks before the person leaves Active.
/// The fail-closed default could never delete anything; the Keycloak adapter accepts exactly the subjects its own deletion
/// would send, by the same pure check, and the preflight sends nothing.
/// </summary>
public sealed class AccountRemovalPreflightTests
{
    private const string Issuer = "https://provider.invalid/realms/learnstack";
    private const string Canonical = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d";

    [Theory]
    [InlineData(Canonical)]
    [InlineData("")]
    [InlineData("anything")]
    public void The_fail_closed_default_could_never_delete_any_account(string subject) =>
        new RefusingIdentityProviderAccountRemoval().CanRemove(subject).Should().BeFalse("nothing is configured");

    [Theory]
    [InlineData(Canonical, true)]
    [InlineData("0A1B2C3D-4E5F-4A6B-8C7D-9E0F1A2B3C4D", false)]
    [InlineData("{0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d}", false)]
    [InlineData("0a1b2c3d4e5f4a6b8c7d9e0f1a2b3c4d", false)]
    [InlineData("00000000-0000-0000-0000-000000000000", false)]
    [InlineData("an-imported-user", false)]
    [InlineData("", false)]
    public async Task The_adapter_accepts_exactly_the_subjects_its_deletion_would_send_and_the_preflight_sends_nothing(
        string subject, bool accepted)
    {
        var provider = new Recording();
        using var adapter = new KeycloakAccountRemoval(new KeycloakAccountRemovalOptions(Issuer), new Secrets(), new Resilience(), NullLogger.Instance, provider);

        adapter.CanRemove(subject).Should().Be(accepted);
        provider.Requests.Should().Be(0, "the preflight is local");

        // The deletion refuses by the same check before any network effect: a subject the preflight refuses is never sent,
        // and one it accepts reaches the provider (here a token endpoint that refuses the client, so nothing is deleted).
        (await adapter.RemoveAsync(subject)).Should().Be(AccountRemovalOutcome.Refused);
        provider.Requests.Should().Be(accepted ? 1 : 0);
    }

    private sealed class Secrets : ISecretProvider
    {
        public string? GetSecret(string key) => key == "AccountRemoval:ClientSecret" ? "account-removal-secret-canary" : null;
    }

    private sealed class Resilience : IProviderResilience<IIdentityProviderAccountRemoval>
    {
        public ResiliencePipeline Pipeline => ResiliencePipeline.Empty;
        public string PortName => "accountRemoval";
    }

    /// <summary>A provider whose token endpoint refuses the client, and which counts every request it is sent.</summary>
    private sealed class Recording : HttpMessageHandler
    {
        private int _requests;

        internal int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }
}
