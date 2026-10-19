namespace LearnStack.Api.Authentication;

/// <summary>Request-local outcome; only a validated profile carries identity.</summary>
internal sealed class TenantCredentialFeature
{
    public bool IsPresent { get; init; }

    public ValidatedTenantIdentity? Identity { get; set; }
}
