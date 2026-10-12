using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Tenancy.Domain.Events;

/// <summary>The tenant's initial default-organization assignment completes provisioning.</summary>
public sealed record TenantCreatedDomainEvent(TenantId TenantId) : DomainEvent;
