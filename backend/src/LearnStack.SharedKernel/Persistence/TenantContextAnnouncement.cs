using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Persistence;

/// <summary>The normalized tenant scope successfully announced on one physical transaction.</summary>
/// <remarks>
/// Read-only evidence, not authority supplied by a caller. Producers read the live snapshot
/// from their unit of work and compare its generation with their execution frame (ADR-0057).
/// </remarks>
public sealed record TenantContextAnnouncement(
    long Generation,
    TenantId TenantId,
    OrganizationId? OrganizationId);
