namespace LearnStack.Infrastructure.Messaging;

/// <summary>One concrete handler subscription and its stable module identity.</summary>
public sealed record IntegrationEventSubscription(
    Type EventType,
    Type HandlerType,
    Type ContractType,
    string ModuleName);
