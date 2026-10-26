using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Api.Authentication;

/// <summary>Stock-validated provider evidence; it grants no application actor or tenant authority.</summary>
internal sealed record VerifiedProviderSubject(
    string Issuer,
    string Subject,
    UserId? ClaimedActor,
    TenantId? TenantId,
    OrganizationId? OrganizationId
);
