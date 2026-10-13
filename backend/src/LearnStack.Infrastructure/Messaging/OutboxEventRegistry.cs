using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using LearnStack.SharedKernel.Messaging;

namespace LearnStack.Infrastructure.Messaging;

/// <summary>The declared delivery policy for one stored integration-event identity.</summary>
public enum OutboxEventDisposition
{
    Pending,
    Active,
    IntentionallyUnsubscribed,
    Retired,
    Unknown,
}

/// <summary>Readiness of one stable, independently deduplicated subscription.</summary>
public enum OutboxSubscriptionReadiness
{
    Pending,
    Active,
}

/// <summary>Construction-free metadata for one subscription.</summary>
public sealed class OutboxSubscriptionManifest
{
    private OutboxSubscriptionManifest(
        string id, OutboxSubscriptionReadiness readiness, Type? handlerType)
    {
        Id = id;
        Readiness = readiness;
        HandlerType = handlerType;
    }

    public string Id { get; }

    public OutboxSubscriptionReadiness Readiness { get; }

    /// <summary>Only an Active subscription names a concrete handler.</summary>
    public Type? HandlerType { get; }

    public static OutboxSubscriptionManifest Pending(string id) =>
        new(id, OutboxSubscriptionReadiness.Pending, null);

    public static OutboxSubscriptionManifest Active(string id, Type handlerType) =>
        new(id, OutboxSubscriptionReadiness.Active, handlerType);
}

/// <summary>A concrete contract, its declared aliases and its delivery policy.</summary>
public sealed class OutboxEventRegistration
{
    private OutboxEventRegistration(
        Type eventType,
        IReadOnlyList<OutboxSubscriptionManifest> subscriptions,
        IReadOnlyList<string> aliases,
        bool intentionallyUnsubscribed)
    {
        EventType = eventType;
        Subscriptions = subscriptions;
        CompatibleAliases = aliases;
        IsIntentionallyUnsubscribed = intentionallyUnsubscribed;
    }

    public Type EventType { get; }

    public IReadOnlyList<OutboxSubscriptionManifest> Subscriptions { get; }

    public IReadOnlyList<string> CompatibleAliases { get; }

    public bool IsIntentionallyUnsubscribed { get; }

    public static OutboxEventRegistration Required(
        Type eventType,
        IEnumerable<OutboxSubscriptionManifest> subscriptions,
        params string[] compatibleAliases)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(compatibleAliases);
        return new OutboxEventRegistration(
            eventType,
            Array.AsReadOnly(subscriptions.ToArray()),
            Array.AsReadOnly(compatibleAliases.ToArray()),
            intentionallyUnsubscribed: false);
    }

    public static OutboxEventRegistration IntentionallyUnsubscribed(
        Type eventType,
        params string[] compatibleAliases)
    {
        ArgumentNullException.ThrowIfNull(compatibleAliases);
        return new OutboxEventRegistration(
            eventType,
            Array.Empty<OutboxSubscriptionManifest>(),
            Array.AsReadOnly(compatibleAliases.ToArray()),
            intentionallyUnsubscribed: true);
    }
}

/// <summary>A bounded lookup result; stored text never becomes a CLR type lookup.</summary>
public sealed record OutboxEventResolution(
    OutboxEventDisposition Disposition,
    Type? EventType,
    string EventTypeLabel,
    IReadOnlyList<OutboxSubscriptionManifest> Subscriptions);

/// <summary>
/// Exact stored-type resolver and readiness manifest for the durable outbox.
/// </summary>
/// <remarks>
/// The registry is assembled from known contract types and the existing handler
/// registry. It never constructs a handler or calls <c>Type.GetType</c> on a row.
/// Pending identities include all declared compatible aliases, so the dispatcher
/// can exclude them before reading or validating a payload.
/// </remarks>
public sealed class OutboxEventRegistry
{
    private static readonly IReadOnlyList<OutboxSubscriptionManifest> NoSubscriptions =
        Array.Empty<OutboxSubscriptionManifest>();

    private readonly FrozenDictionary<string, OutboxEventResolution> _known;
    private readonly FrozenSet<string> _retired;

    private OutboxEventRegistry(
        FrozenDictionary<string, OutboxEventResolution> known,
        FrozenSet<string> retired,
        IReadOnlyList<string> pendingIdentities,
        IReadOnlyList<string> eventTypeLabels)
    {
        _known = known;
        _retired = retired;
        PendingIdentities = pendingIdentities;
        EventTypeLabels = eventTypeLabels;
    }

    /// <summary>Exact canonical and alias identities held before acquisition.</summary>
    public IReadOnlyList<string> PendingIdentities { get; }

    /// <summary>The finite labels allowed for declared event types in metrics.</summary>
    public IReadOnlyList<string> EventTypeLabels { get; }

    /// <summary>
    /// Builds a closed policy from explicitly known assemblies and concrete types.
    /// Active entries must match the construction-free handler census exactly.
    /// </summary>
    public static OutboxEventRegistry Create(
        IEnumerable<OutboxEventRegistration> registrations,
        IEnumerable<string> retiredIdentities,
        IntegrationEventHandlerRegistry handlers,
        IEnumerable<Assembly> knownContractAssemblies)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(retiredIdentities);
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(knownContractAssemblies);

        var assemblies = knownContractAssemblies.ToHashSet();
        if (assemblies.Count == 0)
        {
            throw new InvalidOperationException("The outbox registry needs a known contract assembly.");
        }

        var known = new Dictionary<string, OutboxEventResolution>(StringComparer.Ordinal);
        var pending = new List<string>();
        var subscriptionIds = new HashSet<string>(StringComparer.Ordinal);
        var activeHandlers = new HashSet<(Type EventType, Type HandlerType)>();
        var declaredTypes = new HashSet<Type>();

        foreach (var registration in registrations)
        {
            if (registration is null)
            {
                throw new InvalidOperationException("An outbox contract registration is null.");
            }

            var eventType = registration.EventType;
            if (eventType is null || !assemblies.Contains(eventType.Assembly)
                || !eventType.IsClass || eventType.IsAbstract || eventType.ContainsGenericParameters
                || !typeof(IntegrationEventBase).IsAssignableFrom(eventType)
                || !declaredTypes.Add(eventType))
            {
                throw new InvalidOperationException(
                    "Outbox contracts must be distinct concrete IntegrationEventBase types "
                    + "from explicitly known contract assemblies.");
            }

            var subscriptions = registration.Subscriptions;
            if (registration.IsIntentionallyUnsubscribed == (subscriptions.Count > 0))
            {
                throw new InvalidOperationException(
                    "An outbox contract needs required subscriptions or explicit intentional absence.");
            }

            foreach (var subscription in subscriptions)
            {
                if (subscription is null || !ValidSubscriptionId(subscription.Id)
                    || !subscriptionIds.Add(subscription.Id))
                {
                    throw new InvalidOperationException(
                        "Outbox subscription IDs must be valid and globally unique.");
                }

                if (subscription.Readiness == OutboxSubscriptionReadiness.Pending)
                {
                    if (subscription.HandlerType is not null)
                    {
                        throw new InvalidOperationException("Pending subscriptions cannot name a handler.");
                    }
                }
                else if (subscription.Readiness == OutboxSubscriptionReadiness.Active
                    && subscription.HandlerType is
                    {
                        IsClass: true, IsAbstract: false,
                        ContainsGenericParameters: false
                    } handlerType)
                {
                    if (!activeHandlers.Add((eventType, handlerType)))
                    {
                        throw new InvalidOperationException(
                            "One handler cannot stand for two independent Active subscriptions.");
                    }
                }
                else
                {
                    throw new InvalidOperationException(
                        "An Active subscription needs a concrete handler type.");
                }
            }

            var disposition = registration.IsIntentionallyUnsubscribed
                ? OutboxEventDisposition.IntentionallyUnsubscribed
                : subscriptions.Any(static subscription =>
                    subscription.Readiness == OutboxSubscriptionReadiness.Pending)
                    ? OutboxEventDisposition.Pending
                    : OutboxEventDisposition.Active;

            var resolution = new OutboxEventResolution(
                disposition, eventType, eventType.Name, subscriptions);
            AddIdentity(eventType.AssemblyQualifiedName, resolution);
            foreach (var alias in registration.CompatibleAliases)
            {
                AddIdentity(alias, resolution);
            }
        }

        if (declaredTypes.Count == 0)
        {
            throw new InvalidOperationException("The outbox registry cannot have an empty contract census.");
        }

        var retired = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identity in retiredIdentities)
        {
            if (!ValidIdentity(identity) || known.ContainsKey(identity) || !retired.Add(identity))
            {
                throw new InvalidOperationException(
                    "Retired outbox identities must be nonempty, unique and distinct from live identities.");
            }
        }

        var discovered = handlers.All.ToArray();
        if (discovered.Length != activeHandlers.Count
            || discovered.Any(subscription =>
                !activeHandlers.Contains((subscription.EventType, subscription.HandlerType))))
        {
            throw new InvalidOperationException(
                "Every Active outbox subscription must have exactly one discovered handler, "
                + "and every discovered handler must have an Active declaration.");
        }

        return new OutboxEventRegistry(
            known.ToFrozenDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            retired.ToFrozenSet(StringComparer.Ordinal),
            Array.AsReadOnly(pending.Order(StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(declaredTypes.Select(type => type.Name)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));

        void AddIdentity(string? identity, OutboxEventResolution resolution)
        {
            if (!ValidIdentity(identity) || !known.TryAdd(identity, resolution))
            {
                throw new InvalidOperationException(
                    "Canonical and compatible outbox identities must be nonempty and unique.");
            }

            if (resolution.Disposition == OutboxEventDisposition.Pending)
            {
                pending.Add(identity);
            }
        }
    }

    /// <summary>Looks up exact stored text without loading or reflecting over that text.</summary>
    public OutboxEventResolution Resolve(string? storedIdentity)
    {
        if (storedIdentity is not null && _known.TryGetValue(storedIdentity, out var known))
        {
            return known;
        }

        return storedIdentity is not null && _retired.Contains(storedIdentity)
            ? new OutboxEventResolution(OutboxEventDisposition.Retired, null, "retired", NoSubscriptions)
            : new OutboxEventResolution(OutboxEventDisposition.Unknown, null, "unknown", NoSubscriptions);
    }

    private static bool ValidIdentity([NotNullWhen(true)] string? identity) =>
        !string.IsNullOrWhiteSpace(identity)
        && string.Equals(identity, identity.Trim(), StringComparison.Ordinal);

    internal static bool ValidSubscriptionId(string? id)
    {
        if (id is null) return false;

        var firstDot = id.IndexOf('.');
        var lastDot = id.LastIndexOf('.');
        if (firstDot < 1 || lastDot <= firstDot + 1 || lastDot == id.Length - 1)
        {
            return false;
        }

        var module = id.AsSpan(0, firstDot);
        var subscription = id.AsSpan(firstDot + 1, lastDot - firstDot - 1);
        var version = id.AsSpan(lastDot + 1);
        return IsLowerLetter(module[0])
            && IsAsciiTail(module[1..], allowHyphen: false)
            && IsLowerLetter(subscription[0])
            && IsAsciiTail(subscription[1..], allowHyphen: true)
            && version.Length >= 2 && version[0] == 'v'
            && version[1] is >= '1' and <= '9'
            && IsDecimalTail(version[2..]);
    }

    private static bool IsLowerLetter(char value) => value is >= 'a' and <= 'z';

    private static bool IsAsciiTail(ReadOnlySpan<char> value, bool allowHyphen)
    {
        foreach (var character in value)
        {
            if (!IsLowerLetter(character)
                && character is not (>= '0' and <= '9')
                && (!allowHyphen || character != '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDecimalTail(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9'))
            {
                return false;
            }
        }

        return true;
    }
}
