namespace LearnStack.Api.Authentication;

/// <summary>Request-local outcome; only a validated profile carries identity.</summary>
internal sealed class TenantCredentialFeature
{
    public ValidatedTenantIdentity? Identity { get; set; }
}
