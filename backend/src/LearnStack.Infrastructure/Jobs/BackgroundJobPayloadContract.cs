using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

public enum BackgroundJobPayloadKind
{
    Parameters,
    Plan,
    Outcome,
}

/// <summary>Closed identifier-only payloads with one frozen writer/reader and exact stored-wire admission.</summary>
public static class BackgroundJobPayloadContract
{
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    public static void ValidateShape(Type type, BackgroundJobPayloadKind kind)
    {
        ArgumentNullException.ThrowIfNull(type);
        var marker = kind switch
        {
            BackgroundJobPayloadKind.Parameters => typeof(IBackgroundJobParameters),
            BackgroundJobPayloadKind.Plan => typeof(IBackgroundJobPlan),
            BackgroundJobPayloadKind.Outcome => typeof(IBackgroundJobOutcome),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (!type.IsClass || !type.IsSealed || type.IsAbstract || type.ContainsGenericParameters
            || type.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public)?.ReturnType != type
            || (!marker.IsAssignableFrom(type)
                && !(kind == BackgroundJobPayloadKind.Parameters && type == typeof(BackgroundJobOccurrenceParameters)))
            || typeof(IEnumerable).IsAssignableFrom(type)
            || typeof(IJsonOnSerializing).IsAssignableFrom(type) || typeof(IJsonOnSerialized).IsAssignableFrom(type)
            || typeof(IJsonOnDeserializing).IsAssignableFrom(type) || typeof(IJsonOnDeserialized).IsAssignableFrom(type))
            throw Invalid("Job payloads must be sealed identifier records with the exact payload marker and no serialization callbacks.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            RejectJsonAttributes(current);
            foreach (var member in current.GetMembers(BindingFlags.Instance | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                RejectJsonAttributes(member);
                if (member is FieldInfo field && !field.IsDefined(typeof(CompilerGeneratedAttribute)))
                    throw Invalid("Job payloads declare properties, not instance fields.");
                if (member is not PropertyInfo property || property.Name == "EqualityContract"
                    && property.PropertyType == typeof(Type) && property.GetMethod?.IsPublic != true)
                    continue;
                if (property.GetMethod?.IsPublic != true || property.GetIndexParameters().Length != 0
                    || property.SetMethod?.IsPublic != true
                    || !property.GetMethod.IsDefined(typeof(CompilerGeneratedAttribute))
                    || !property.SetMethod.IsDefined(typeof(CompilerGeneratedAttribute))
                    || property.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)) != true
                    || !names.Add(property.Name) || !IsIdentifier(property.PropertyType))
                    throw Invalid("Job payload properties must be distinct public auto-properties with init-only approved identifiers.");
            }
        }
    }

    /// <summary>Serializes by the registered concrete type and verifies every identifier survives its round-trip.</summary>
    public static string Serialize(object payload, Type declaredType, BackgroundJobPayloadKind kind)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ValidateShape(declaredType, kind);
        if (payload.GetType() != declaredType)
            throw Invalid("The job payload runtime type must equal its registered concrete type.");
        var values = ReadValues(payload, declaredType);
        var json = JsonSerializer.Serialize(payload, declaredType, JsonOptions);
        EnsureWithinBudget(json, kind);
        var restored = Read(json, declaredType, kind);
        if (!SameValues(values, ReadValues(restored, declaredType)) || !SameValues(values, ReadValues(payload, declaredType)))
            throw Invalid("Job payload identifiers changed during serialization or round-trip.");
        return json;
    }

    /// <summary>Rejects missing/extra/duplicate wire members before constructing the declared DTO.</summary>
    public static object Read(string json, Type declaredType, BackgroundJobPayloadKind kind)
    {
        ArgumentNullException.ThrowIfNull(json);
        ValidateShape(declaredType, kind);
        EnsureWithinBudget(json, kind);
        var properties = declaredType.GetProperties(BindingFlags.Instance | BindingFlags.Public);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw Invalid("Job payload JSON must be an object.");
        var wire = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!wire.TryAdd(property.Name, ReadWireIdentifier(property.Value)))
                throw Invalid("Job payload JSON cannot repeat a property.");
        }
        if (wire.Count != properties.Length || properties.Any(property => !wire.ContainsKey(property.Name)))
            throw Invalid("Job payload JSON must contain exactly its declared identifier properties.");

        var payload = JsonSerializer.Deserialize(json, declaredType, JsonOptions)
            ?? throw Invalid("A job payload cannot deserialize to null.");
        var restored = ReadValues(payload, declaredType);
        if (!SameValues(wire, restored))
            throw Invalid("Stored job identifiers must survive the declared typed round-trip.");
        var canonical = JsonSerializer.Serialize(payload, declaredType, JsonOptions);
        EnsureWithinBudget(canonical, kind);
        using var roundTrip = JsonDocument.Parse(canonical);
        var roundTripValues = roundTrip.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => ReadWireIdentifier(property.Value), StringComparer.Ordinal);
        if (!SameValues(wire, roundTripValues))
            throw Invalid("The canonical job payload must preserve every stored identifier.");
        return payload;
    }

    public static void EnsureWithinBudget(string json, BackgroundJobPayloadKind kind)
    {
        ArgumentNullException.ThrowIfNull(json);
        var maximum = kind switch
        {
            BackgroundJobPayloadKind.Parameters => BackgroundJobPayloadLimits.Parameters,
            BackgroundJobPayloadKind.Plan => BackgroundJobPayloadLimits.Plan,
            BackgroundJobPayloadKind.Outcome => BackgroundJobPayloadLimits.Outcome,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (Encoding.UTF8.GetByteCount(json) > maximum)
            throw Invalid("The canonical job payload exceeds its UTF-8 byte budget.");
    }

    private static Dictionary<string, Guid?> ReadValues(object payload, Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public).ToDictionary(property => property.Name,
            property => ReadIdentifier(property.GetValue(payload), Nullable.GetUnderlyingType(property.PropertyType) is not null),
            StringComparer.Ordinal);

    private static Guid? ReadIdentifier(object? value, bool nullable) => value switch
    {
        null when nullable => null,
        Guid id when id != Guid.Empty => id,
        IStronglyTypedId<Guid> id when id.IsInitialized() && id.Value != Guid.Empty => id.Value,
        _ => throw Invalid("A job payload identifier must be initialized and nonempty; only nullable identifiers may be null."),
    };

    private static Guid? ReadWireIdentifier(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var id) || id == Guid.Empty
            || !string.Equals(value.GetString(), id.ToString("D"), StringComparison.Ordinal))
            throw Invalid("Stored job identifiers must use canonical lowercase D GUID text.");
        return id;
    }

    private static bool SameValues(Dictionary<string, Guid?> expected, Dictionary<string, Guid?> actual) =>
        expected.Count == actual.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static bool IsIdentifier(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(Guid) || type == typeof(TenantId) || type == typeof(OrganizationId)
            || type == typeof(UserId) || type == typeof(AuditEntryId) || type == typeof(BackgroundJobId)
            || type == typeof(BackgroundJobAttemptId) || type == typeof(BackgroundJobOccurrenceId)
            || type == typeof(BackgroundJobRecipientId);
    }

    private static void RejectJsonAttributes(MemberInfo member)
    {
        if (member.GetCustomAttributesData().Any(attribute => typeof(JsonAttribute).IsAssignableFrom(attribute.AttributeType)))
            throw Invalid("Job DTOs cannot customize JSON serialization.");
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            PropertyNameCaseInsensitive = false,
            WriteIndented = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        options.MakeReadOnly();
        // MakeReadOnly freezes options, but the default resolver accepts modifiers until its first metadata lookup.
        _ = options.GetTypeInfo(typeof(Guid));
        return options;
    }

    private static ArgumentException Invalid(string message) => new(message);
}
