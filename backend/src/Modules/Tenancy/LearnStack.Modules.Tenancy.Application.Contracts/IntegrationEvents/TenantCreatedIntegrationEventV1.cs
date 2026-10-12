using LearnStack.SharedKernel.Messaging;

namespace LearnStack.Modules.Tenancy.Application.Contracts.IntegrationEvents;

/// <summary>A completed tenant provisioning, ordered deliberately within its tenant.</summary>
[IntegrationEventPartition(nameof(TenantId), tenantWide: true)]
public sealed record TenantCreatedIntegrationEventV1 : IntegrationEventBase
{
    /// <inheritdoc />
    public override string Topic => "learnstack.tenancy.tenant";

    /// <inheritdoc />
    public override string PartitionKey => TenantId.ToString("D");
}
