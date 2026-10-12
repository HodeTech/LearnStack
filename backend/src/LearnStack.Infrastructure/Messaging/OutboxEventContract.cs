using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;

namespace LearnStack.Infrastructure.Messaging;

/// <summary>ADR-0057's closed payload shape and explicit identifier partition policy.</summary>
internal static partial class OutboxEventContract
{
    internal static SerializedEvent Serialize(IIntegrationEvent integrationEvent)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        if (integrationEvent is not IntegrationEventBase fact)
        {
            throw Invalid("Integration events must inherit IntegrationEventBase.");
        }

        var type = fact.GetType();
        ValidateShape(type);
        var source = GetPartitionSource(type);
        var sourceId = ReadIdentifier(source.GetValue(fact));
        var topic = fact.Topic;
        var partitionKey = fact.PartitionKey;
        if (topic is null || !TopicPattern().IsMatch(topic))
        {
            throw Invalid("The topic must follow the learnstack.module.aggregate convention.");
        }

        if (sourceId == Guid.Empty
            || !string.Equals(partitionKey, sourceId.ToString("D"), StringComparison.Ordinal))
        {
            throw Invalid("The partition key must equal its source identifier's canonical lowercase D GUID.");
        }

        var eventId = fact.EventId;
        var tenantId = fact.TenantId;
        var occurredAt = fact.OccurredAt;
        if (eventId == Guid.Empty || occurredAt == default)
        {
            throw Invalid("An integration event requires an event identity and occurrence timestamp.");
        }

        var payload = fact.ToPayloadJson();
        EnsurePayloadFits(payload);

        // Getters are executable code. Compare the bytes we will persist to the captured
        // values instead of trusting that a computed property returned the same value twice.
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.GetProperty(nameof(fact.EventId)).GetGuid() != eventId
            || root.GetProperty(nameof(fact.TenantId)).GetGuid() != tenantId
            || root.GetProperty(nameof(fact.OccurredAt)).GetDateTimeOffset() != occurredAt
            || root.GetProperty(nameof(fact.Topic)).GetString() != topic
            || root.GetProperty(nameof(fact.PartitionKey)).GetString() != partitionKey
            || root.GetProperty(source.Name).GetGuid() != sourceId
            || fact.EventId != eventId || fact.TenantId != tenantId || fact.OccurredAt != occurredAt)
        {
            throw Invalid("An integration event must serialize the same identity and routing it declares.");
        }

        return new SerializedEvent(eventId, tenantId, occurredAt, topic, partitionKey, payload,
            type.AssemblyQualifiedName
                ?? throw Invalid("An integration event requires an assembly-qualified type name."));
    }

    internal static void EnsurePayloadFits(string payload)
    {
        if (Encoding.UTF8.GetByteCount(payload) > OutboxPayloadLimits.MaxBytes)
        {
            throw Invalid($"The canonical UTF-8 outbox payload exceeds {OutboxPayloadLimits.MaxBytes} bytes.");
        }
    }

    private static void ValidateShape(Type type)
    {
        if (typeof(IEnumerable).IsAssignableFrom(type)
            || typeof(IJsonOnSerializing).IsAssignableFrom(type)
            || typeof(IJsonOnSerialized).IsAssignableFrom(type)
            || typeof(IJsonOnDeserializing).IsAssignableFrom(type)
            || typeof(IJsonOnDeserialized).IsAssignableFrom(type))
        {
            throw Invalid("Integration events cannot override their object serialization contract.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(IIntegrationEvent.EventId), nameof(IIntegrationEvent.TenantId),
            nameof(IIntegrationEvent.OccurredAt), nameof(IIntegrationEvent.Topic),
            nameof(IIntegrationEvent.PartitionKey),
        };

        for (var current = type; current != typeof(IntegrationEventBase); current = current.BaseType!)
        {
            RejectSerializerAttributes(current);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var member in current.GetMembers(flags))
            {
                RejectSerializerAttributes(member);
                if (member is FieldInfo field && !field.IsDefined(typeof(CompilerGeneratedAttribute)))
                {
                    throw Invalid("Integration event payloads use identifier properties, not instance fields.");
                }

                if (member is not PropertyInfo property || property.GetMethod?.IsPublic != true)
                {
                    continue;
                }

                if (property.GetIndexParameters().Length != 0)
                {
                    throw Invalid("Integration event payloads cannot contain indexers.");
                }

                if (IsRoutingOverride(property)) continue;
                if (!names.Add(property.Name) || !IsIdentifierType(property.PropertyType))
                {
                    throw Invalid("Additional integration event payload properties must be GUID identifiers; base members cannot be hidden.");
                }
            }
        }

        var map = type.GetInterfaceMap(typeof(IIntegrationEvent));
        if (map.TargetMethods.Any(method => method.GetBaseDefinition().DeclaringType != typeof(IntegrationEventBase)))
        {
            throw Invalid("An integration event cannot replace the base event's interface members.");
        }
    }

    private static bool IsRoutingOverride(PropertyInfo property) =>
        property.Name is nameof(IIntegrationEvent.Topic) or nameof(IIntegrationEvent.PartitionKey)
        && property.GetMethod!.GetBaseDefinition().DeclaringType == typeof(IntegrationEventBase);

    private static void RejectSerializerAttributes(MemberInfo member)
    {
        if (member.GetCustomAttributesData().Any(attribute => typeof(JsonAttribute).IsAssignableFrom(attribute.AttributeType)))
        {
            throw Invalid("Integration events cannot declare JSON serializer attributes.");
        }
    }

    private static PropertyInfo GetPartitionSource(Type type)
    {
        var partition = type.GetCustomAttribute<IntegrationEventPartitionAttribute>(inherit: false)
            ?? throw Invalid("Every concrete integration event must declare its partition source.");
        var source = string.IsNullOrWhiteSpace(partition.SourceProperty)
            ? null
            : type.GetProperty(partition.SourceProperty, BindingFlags.Instance | BindingFlags.Public);
        if (source is null || source.GetMethod?.IsPublic != true
            || source.GetIndexParameters().Length != 0
            || !IsIdentifierType(source.PropertyType)
            || Nullable.GetUnderlyingType(source.PropertyType) is not null
            || source.Name == nameof(IIntegrationEvent.EventId))
        {
            throw Invalid("A partition source must name a non-nullable aggregate identifier or explicitly tenant-wide TenantId.");
        }

        if ((source.Name == nameof(IIntegrationEvent.TenantId)) != partition.TenantWide)
        {
            throw Invalid("Partitioning on TenantId requires exactly an explicit tenant-wide declaration.");
        }

        return source;
    }

    private static bool IsIdentifierType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        // Deliberately closed. Implementing IStronglyTypedId alone could smuggle a
        // custom JSON converter into the supposedly identifier-only payload.
        return type == typeof(Guid) || type == typeof(TenantId)
            || type == typeof(OrganizationId) || type == typeof(UserId);
    }

    private static Guid ReadIdentifier(object? value) => value switch
    {
        Guid id => id,
        IStronglyTypedId<Guid> id when id.IsInitialized() => id.Value,
        _ => throw Invalid("A partition source must contain an initialized identifier."),
    };

    private static ArgumentException Invalid(string message) => new(message);

    // The same core three-segment and Hub-only four-segment grammar enforced by
    // Integration_Event_TopicNames_FollowConvention; \z refuses a trailing newline.
    [GeneratedRegex(@"\Alearnstack\.([a-z][a-z0-9-]*[a-z0-9]|[a-z])\.([a-z][a-z0-9-]*[a-z0-9]|[a-z])\z|\Alearnstack\.hub\.([a-z][a-z0-9-]*[a-z0-9]|[a-z])\.([a-z][a-z0-9-]*[a-z0-9]|[a-z])\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TopicPattern();

    internal sealed record SerializedEvent(
        Guid EventId, Guid TenantId, DateTimeOffset OccurredAt,
        string Topic, string PartitionKey, string Payload, string TypeName);
}
