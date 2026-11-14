using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Api.Authentication;

/// <summary>
/// Stock-validated provider evidence; it grants no application actor or tenant authority.
/// <paramref name="FederationTestRecord"/> says only that the token's <c>scope</c> names the optional
/// <c>federation.test_record</c> client scope (ADR-0099 Amendment 6); it authorizes nothing on its own.
/// </summary>
internal sealed record VerifiedProviderSubject(
    string Issuer,
    string Subject,
    UserId? ClaimedActor,
    TenantId? TenantId,
    OrganizationId? OrganizationId,
    bool FederationTestRecord = false
)
{
    public override string ToString() => nameof(VerifiedProviderSubject);
}
