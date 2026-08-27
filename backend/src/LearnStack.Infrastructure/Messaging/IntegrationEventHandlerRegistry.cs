using System.Reflection;
using LearnStack.SharedKernel.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace LearnStack.Infrastructure.Messaging;

/// <summary>
/// Immutable, construction-free subscription metadata for the in-process transport.
/// </summary>
/// <remarks>
/// Enumerating an <c>IEnumerable&lt;IIntegrationEventHandler&lt;T&gt;&gt;</c> constructs
/// every handler graph. This registry lets the transport select subscriptions
/// without resolving any handler, then construct exactly one concrete handler in
/// that subscription's own async scope.
/// </remarks>
public sealed class IntegrationEventHandlerRegistry
{
    private const string HandleMethodName =
        nameof(IIntegrationEventHandler<IIntegrationEvent>.HandleAsync);

    private readonly IReadOnlyDictionary<Type, IntegrationEventSubscription[]> _subscriptions;

    private IntegrationEventHandlerRegistry(IEnumerable<IntegrationEventSubscription> subscriptions)
    {
        _subscriptions = subscriptions
            .GroupBy(subscription => subscription.EventType)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray());
    }

    /// <summary>Discovers concrete handlers in the supplied composition-root assemblies.</summary>
    public static IntegrationEventHandlerRegistry Discover(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var subscriptions = assemblies
            .Distinct()
            .SelectMany(static assembly => assembly.DefinedTypes)
            .Where(static type => type is { IsAbstract: false, IsInterface: false })
            .SelectMany(static handlerType => HandlerContracts(handlerType.AsType())
                .Select(contract => CreateSubscription(handlerType.AsType(), contract)));

        return new IntegrationEventHandlerRegistry(subscriptions);
    }

    /// <summary>
    /// Builds metadata from ordinary Microsoft DI handler registrations.
    /// Intended for composition tests that assemble subscriptions directly.
    /// </summary>
    internal static IntegrationEventHandlerRegistry FromServiceDescriptors(
        IEnumerable<ServiceDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        var subscriptions = descriptors
            .Where(static descriptor => IsHandlerContract(descriptor.ServiceType))
            .Select(descriptor =>
            {
                if (descriptor.ImplementationType is null)
                {
                    throw new InvalidOperationException(
                        $"The {descriptor.ServiceType.Name} registration must name a concrete "
                        + "implementation type so dispatch can isolate its construction.");
                }

                return CreateSubscription(descriptor.ImplementationType, descriptor.ServiceType);
            });

        return new IntegrationEventHandlerRegistry(subscriptions);
    }

    /// <summary>All subscriptions, used once by the composition root for DI registration.</summary>
    public IEnumerable<IntegrationEventSubscription> All =>
        _subscriptions.Values.SelectMany(static subscriptions => subscriptions);

    /// <summary>Subscriptions for exactly one concrete event type.</summary>
    public IReadOnlyList<IntegrationEventSubscription> For(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        return _subscriptions.TryGetValue(eventType, out var subscriptions)
            ? subscriptions
            : [];
    }

    private static IEnumerable<Type> HandlerContracts(Type handlerType) =>
        handlerType.GetInterfaces().Where(IsHandlerContract);

    private static bool IsHandlerContract(Type type) =>
        type.IsGenericType
        && type.GetGenericTypeDefinition() == typeof(IIntegrationEventHandler<>);

    private static IntegrationEventSubscription CreateSubscription(
        Type handlerType, Type contract)
    {
        var eventType = contract.GetGenericArguments()[0];
        var handle = contract.GetMethod(HandleMethodName)
            ?? throw new InvalidOperationException(
                $"{contract.FullName} declares no {HandleMethodName}; the "
                + "integration-event handler contract has drifted.");

        return new IntegrationEventSubscription(
            eventType,
            handlerType,
            contract,
            ModuleName(handlerType, eventType),
            handle);
    }

    private static string ModuleName(Type handlerType, Type eventType)
    {
        var namespaceParts = handlerType.Namespace?.Split('.') ?? [];
        var modulesIndex = Array.FindIndex(
            namespaceParts,
            static part => part.Equals("Modules", StringComparison.Ordinal));

        if (modulesIndex >= 0 && modulesIndex + 1 < namespaceParts.Length)
        {
            return namespaceParts[modulesIndex + 1].ToLowerInvariant();
        }

        var eventNamespaceParts = eventType.Namespace?.Split('.') ?? [];
        var eventModulesIndex = Array.FindIndex(
            eventNamespaceParts,
            static part => part.Equals("Modules", StringComparison.Ordinal));

        return eventModulesIndex >= 0 && eventModulesIndex + 1 < eventNamespaceParts.Length
            ? eventNamespaceParts[eventModulesIndex + 1].ToLowerInvariant()
            : "unknown";
    }
}
