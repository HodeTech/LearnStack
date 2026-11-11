using LearnStack.SharedKernel.Messaging;

namespace LearnStack.Modules.Identity.Application.Contracts.IntegrationEvents;

[IntegrationEventPartition(nameof(TargetId))]
public sealed record UserProviderEventObservedIntegrationEventV1 : IntegrationEventBase
{
    public required Guid TargetId { get; init; }
    public override string Topic => "learnstack.identity.user";
    public override string PartitionKey => TargetId.ToString("D");
}
