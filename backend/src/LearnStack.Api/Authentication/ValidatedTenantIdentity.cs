using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Api.Authentication;

internal sealed record ValidatedTenantIdentity(
    string Issuer,
    string Subject,
    UserId ActorUserId,
    TenantId? TenantId,
    OrganizationId? OrganizationId
);
