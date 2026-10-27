namespace LearnStack.Api.Authentication;

/// <summary>Separates verified credentials from the existing binding's normalized identity.</summary>
internal sealed class TenantCredentialFeature
{
    public VerifiedProviderSubject? VerifiedSubject { get; set; }
    public ValidatedTenantIdentity? Identity { get; set; }
}
