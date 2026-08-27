using System.Reflection;

namespace LearnStack.Infrastructure.Messaging;

/// <summary>One concrete handler subscription and its stable module identity.</summary>
/// <param name="Handle">
/// The contract's <c>HandleAsync</c>, resolved once when the subscription is built.
/// </param>
/// <remarks>
/// Resolved here rather than per dispatch for two reasons. It keeps a reflection
/// lookup off the delivery path, and it moves the assertion that the method
/// exists to startup — where a contract that has drifted fails immediately and
/// visibly, instead of throwing on the first event of its type in production.
/// </remarks>
public sealed record IntegrationEventSubscription(
    Type EventType,
    Type HandlerType,
    Type ContractType,
    string ModuleName,
    MethodInfo Handle);
