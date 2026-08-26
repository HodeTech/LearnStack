namespace LearnStack.SharedKernel.Messaging;

/// <summary>
/// Marks an integration event whose consumer must run inside an organization scope.
/// </summary>
/// <remarks>
/// <see cref="IntegrationEventEnvelope"/> rejects this event shape unless a
/// non-empty organization identifier is supplied. Tenant-wide events do not
/// implement the marker and may deliberately omit the organization.
/// </remarks>
public interface IOrganizationScopedIntegrationEvent : IIntegrationEvent;
