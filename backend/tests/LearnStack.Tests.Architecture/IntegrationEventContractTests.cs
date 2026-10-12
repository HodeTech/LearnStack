using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using LearnStack.Modules.Tenancy.Application.Contracts.IntegrationEvents;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>Production event contracts under ADR-0057, independent of enqueue's admission validator.</summary>
public sealed class IntegrationEventContractTests
{
    private const BindingFlags DeclaredMembers = BindingFlags.Instance | BindingFlags.Public
        | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private static readonly string[] BaseNames =
    [
        nameof(IIntegrationEvent.EventId), nameof(IIntegrationEvent.TenantId),
        nameof(IIntegrationEvent.OccurredAt), nameof(IIntegrationEvent.Topic),
        nameof(IIntegrationEvent.PartitionKey),
    ];
    private static readonly Type[] SerializationInterfaces =
    [
        typeof(IEnumerable), typeof(IJsonOnSerializing), typeof(IJsonOnSerialized),
        typeof(IJsonOnDeserializing), typeof(IJsonOnDeserialized),
    ];

    /// <summary>Standards 21 and ADR-0006: every production fact uses the common record contract.</summary>
    [Fact]
    public void Integration_Events_Inherit_From_IntegrationEventBase()
    {
        var violations = ProductionEvents().SelectMany(InheritanceViolations).ToList();
        var contract = typeof(IntegrationEventBase);
        if (!contract.IsAbstract || !IsRecord(contract)) violations.Add("IntegrationEventBase must be an abstract record.");
        foreach (var (name, expectedType) in new[]
        {
            (nameof(IIntegrationEvent.EventId), typeof(Guid)),
            (nameof(IIntegrationEvent.TenantId), typeof(Guid)),
            (nameof(IIntegrationEvent.OccurredAt), typeof(DateTimeOffset)),
        })
        {
            var property = contract.GetProperty(name);
            if (property?.PropertyType != expectedType
                || !property.IsDefined(typeof(RequiredMemberAttribute))
                || property.SetMethod?.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)) != true)
            {
                violations.Add($"IntegrationEventBase.{name} must remain a required {expectedType.Name} init property.");
            }
        }

        foreach (var name in new[] { nameof(IIntegrationEvent.Topic), nameof(IIntegrationEvent.PartitionKey) })
        {
            var property = contract.GetProperty(name);
            if (property?.PropertyType != typeof(string) || property.GetMethod?.IsAbstract != true)
                violations.Add($"IntegrationEventBase.{name} must remain an abstract string getter.");
        }

        var writer = contract.GetMethod(nameof(IntegrationEventBase.ToPayloadJson), Type.EmptyTypes);
        if (writer is null || writer.IsVirtual || writer.ReturnType != typeof(string))
            violations.Add("IntegrationEventBase.ToPayloadJson must remain the non-virtual payload writer.");
        if (!IntegrationEventBase.PayloadJsonOptions.IsReadOnly
            || IntegrationEventBase.PayloadJsonOptions.PropertyNamingPolicy is not null)
            violations.Add("The common payload options must remain frozen and preserve property names.");

        violations.AddRange(ControlViolations(InheritanceViolations,
            [typeof(DirectInterfaceFact)], [typeof(ApprovedIdentifiersFact)]));
        AssertClean(violations, "inherit IntegrationEventBase; keep its required identity, abstract routing and frozen writer contract");
    }

    /// <summary>ADR-0057: declare and derive canonical GUID partition keys for every concrete production event.</summary>
    [Fact]
    public void Integration_Event_Declares_PartitionKey()
    {
        var violations = ProductionEvents().Where(type => !type.IsAbstract)
            .SelectMany(PartitionViolations).ToList();
        violations.AddRange(ControlViolations(PartitionViolations,
            [typeof(ConstantPartitionFact), typeof(UnrelatedPartitionFact), typeof(MissingPartitionFact),
                typeof(EventIdentityPartitionFact), typeof(NullablePartitionFact), typeof(ImplicitTenantFact),
                typeof(MislabelledTenantFact), typeof(UnknownSourceFact), typeof(ComputedSourceFact),
                typeof(EmptySourceFact), typeof(UppercasePartitionFact)],
            [typeof(ApprovedIdentifiersFact), typeof(TenantWideFact), typeof(TypedPartitionFact)]));
        AssertClean(violations, "declare [IntegrationEventPartition] on each concrete event and derive PartitionKey from its stored, non-nullable aggregate identifier using ToString(\"D\"); TenantId requires tenantWide: true");
    }

    /// <summary>ADR-0057: a closed identifier-only shape and concrete-runtime JSON round-trip across production.</summary>
    [Fact]
    public void Integration_Event_Payloads_Contain_Only_Identifiers()
    {
        var events = ProductionEvents();
        var violations = events.SelectMany(PayloadShapeViolations).ToList();
        violations.AddRange(ControlViolations(PayloadShapeViolations,
            [typeof(PayloadFact<string>), typeof(PayloadFact<object>), typeof(PayloadFact<List<Guid>>),
                typeof(PayloadFact<Guid[]>), typeof(PayloadFact<byte[]>), typeof(PayloadFact<JsonElement>),
                typeof(PayloadFact<UnapprovedId>), typeof(InheritedTextFact), typeof(ExtraFieldFact),
                typeof(HiddenRoutingFact), typeof(RemappedInterfaceFact),
                typeof(IgnoredIdentifierFact), typeof(RenamedIdentifierFact), typeof(ConvertedFact),
                typeof(SerializingFact), typeof(SerializedFact), typeof(DeserializingFact),
                typeof(DeserializedFact), typeof(EnumerableFact), typeof(HiddenWriterFact)],
            [typeof(ApprovedIdentifiersFact), typeof(TenantWideFact), typeof(TypedPartitionFact)]));

        // Real production coverage is mandatory; the clean fixture supplies extra fields
        // the deliberately minimal TenantCreated fact does not need. Both use this writer.
        violations.AddRange(events.Where(type => !type.IsAbstract)
            .Append(typeof(ApprovedIdentifiersFact)).SelectMany(PayloadRoundTripViolations));
        AssertClean(violations, "carry only Guid, TenantId, OrganizationId or UserId properties (nullable is allowed); remove hidden members, fields and serialization overrides, and preserve every identifier through the concrete runtime payload");
    }

    private static Type[] ProductionEvents()
    {
        var assemblies = ProductionAssemblies.All().ToArray();
        var loaded = assemblies.Select(assembly => assembly.GetName().Name!).Order(StringComparer.Ordinal).ToArray();
        var expected = ProductionAssemblies.Names().ToArray();
        var moduleProjects = Modules.Names.SelectMany(module => new[]
        {
            $"LearnStack.Modules.{module}.Domain", $"LearnStack.Modules.{module}.Application.Contracts",
            $"LearnStack.Modules.{module}.Application", $"LearnStack.Modules.{module}.Infrastructure",
        }).Order(StringComparer.Ordinal).ToArray();
        Assert.True(expected.Length != 0 && Modules.Names.Count != 0
            && loaded.SequenceEqual(expected)
            && loaded.Where(name => name.StartsWith("LearnStack.Modules.", StringComparison.Ordinal)).SequenceEqual(moduleProjects),
            "The integration-event census must load every production project and all four layers of every discovered module. "
            + $"Missing projects: {string.Join(", ", expected.Except(loaded).Concat(moduleProjects.Except(loaded)).Order(StringComparer.Ordinal))}. "
            + $"Unexpected module projects: {string.Join(", ", loaded.Where(name => name.StartsWith("LearnStack.Modules.", StringComparison.Ordinal)).Except(moduleProjects).Order(StringComparer.Ordinal))}. "
            + "Fix: reference all production projects from LearnStack.Tests.Architecture and preserve the four-layer module layout.");

        var events = assemblies.SelectMany(assembly => assembly.GetTypes())
            .Where(type => !type.IsInterface && type != typeof(IntegrationEventBase)
                && typeof(IIntegrationEvent).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
        Assert.True(events.Any(type => !type.IsAbstract) && events.Contains(typeof(TenantCreatedIntegrationEventV1)),
            "The production integration-event census is empty or omitted TenantCreatedIntegrationEventV1. "
            + "Fix: scan IIntegrationEvent implementations across ProductionAssemblies.All(), including Application.Contracts.");
        return events;
    }

    private static IEnumerable<string> InheritanceViolations(Type type)
    {
        if (!typeof(IntegrationEventBase).IsAssignableFrom(type) || !IsRecord(type))
            yield return $"{type.FullName}: integration events must inherit the IntegrationEventBase record.";
    }

    private static bool IsRecord(Type type) =>
        type.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public)?.ReturnType == type;

    private static IEnumerable<Type> EventHierarchy(Type type)
    {
        for (var current = type; current is not null && current != typeof(IntegrationEventBase); current = current.BaseType)
            yield return current;
    }

    private static IEnumerable<string> PayloadShapeViolations(Type type)
    {
        if (!typeof(IntegrationEventBase).IsAssignableFrom(type))
        {
            yield return $"{type.FullName}: payload does not use IntegrationEventBase.";
            yield break;
        }

        foreach (var contract in SerializationInterfaces.Where(contract => contract.IsAssignableFrom(type)))
            yield return $"{type.FullName}: serialization interface {contract.Name} changes the object contract.";

        var names = new HashSet<string>(BaseNames, StringComparer.Ordinal);
        foreach (var current in EventHierarchy(type))
        {
            foreach (var member in current.GetMembers(DeclaredMembers).Prepend(current))
            {
                if (member.GetCustomAttributesData().Any(attribute => typeof(JsonAttribute).IsAssignableFrom(attribute.AttributeType)))
                    yield return $"{type.FullName}: JSON attribute on {current.Name}.{member.Name}.";
                if (member is FieldInfo field && !field.IsDefined(typeof(CompilerGeneratedAttribute)))
                    yield return $"{type.FullName}: instance field {current.Name}.{field.Name}; use identifier properties.";
                if (member is MethodInfo method && method.Name == nameof(IntegrationEventBase.ToPayloadJson)
                    && method.GetParameters().Length == 0)
                    yield return $"{type.FullName}: hides the common payload writer.";
                if (member is not PropertyInfo property || property.GetMethod?.IsPublic != true) continue;
                if (property.GetIndexParameters().Length != 0)
                    yield return $"{type.FullName}: indexer {current.Name}.{property.Name}.";
                if (property.Name is nameof(IIntegrationEvent.Topic) or nameof(IIntegrationEvent.PartitionKey)
                    && property.GetMethod.GetBaseDefinition().DeclaringType == typeof(IntegrationEventBase)) continue;
                if (!names.Add(property.Name))
                    yield return $"{type.FullName}: hidden or duplicate payload member {current.Name}.{property.Name}.";
                if (!IsIdentifier(property.PropertyType))
                    yield return $"{type.FullName}: {current.Name}.{property.Name} has forbidden payload type {property.PropertyType}.";
            }
        }

        foreach (var target in type.GetInterfaceMap(typeof(IIntegrationEvent)).TargetMethods
            .Where(target => target.GetBaseDefinition().DeclaringType != typeof(IntegrationEventBase)))
            yield return $"{type.FullName}: replaces IIntegrationEvent member {target.Name}.";
    }

    private static bool IsIdentifier(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(Guid) || type == typeof(TenantId)
            || type == typeof(OrganizationId) || type == typeof(UserId);
    }

    private static IEnumerable<string> PartitionViolations(Type type)
    {
        var prefix = $"{type.FullName}: ";
        var declaration = type.GetCustomAttribute<IntegrationEventPartitionAttribute>(inherit: false);
        if (declaration is null)
        {
            yield return prefix + "missing its own [IntegrationEventPartition] declaration.";
            yield break;
        }

        var source = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(property => property.Name == declaration.SourceProperty);
        if (source?.GetMethod?.IsPublic != true || source.GetIndexParameters().Length != 0
            || !IsIdentifier(source.PropertyType) || Nullable.GetUnderlyingType(source.PropertyType) is not null
            || source.Name == nameof(IIntegrationEvent.EventId))
        {
            yield return prefix + $"{declaration.SourceProperty} is not a public, non-nullable aggregate identifier source.";
            yield break;
        }

        if ((source.Name == nameof(IIntegrationEvent.TenantId)) != declaration.TenantWide)
            yield return prefix + "TenantId partitioning requires exactly an explicit tenant-wide declaration.";
        if (!IsStoredIdentifier(source))
            yield return prefix + $"{source.Name} must read its own stored identifier, rather than compute an unrelated identity.";
        var partition = type.GetProperty(nameof(IIntegrationEvent.PartitionKey))?.GetMethod;
        if (partition is null || !DerivesCanonicalKey(partition, source))
            yield return prefix + $"PartitionKey must derive directly from {source.Name}'s GUID using ToString(\"D\").";

        // Change tenant, event and aggregate IDs independently. A coincidentally matching
        // sample must not rescue a constant or another identifier's partition key.
        for (var variation = 0; variation < 4; variation++)
        {
            string? failure;
            try
            {
                var fact = Sample(type, variation);
                var sourceId = IdentifierValue(source.GetValue(fact));
                var expected = SampleIdentifier(source.Name, variation);
                failure = sourceId == Guid.Empty || sourceId != expected
                    || !string.Equals(fact.PartitionKey, sourceId.Value.ToString("D"), StringComparison.Ordinal)
                    ? prefix + $"source/key value probe {variation} did not preserve the initialized source's canonical GUID."
                    : null;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                or NotSupportedException or JsonException or TargetInvocationException)
            {
                failure = prefix + $"source/key value probe {variation} could not create/read the concrete event ({exception.GetType().Name}).";
            }
            if (failure is not null) yield return failure;
        }
    }

    private static bool IsStoredIdentifier(PropertyInfo property)
    {
        var getter = property.GetMethod!;
        using var module = ModuleDefinition.ReadModule(getter.Module.Assembly.Location);
        var method = (MethodDefinition)module.LookupToken(getter.MetadataToken);
        if (!method.HasBody) return false;
        var instructions = method.Body.Instructions.Where(instruction => instruction.OpCode.Code != Code.Nop).ToArray();
        var backing = property.DeclaringType!.GetField($"<{property.Name}>k__BackingField", DeclaredMembers);
        return backing?.IsDefined(typeof(CompilerGeneratedAttribute)) == true
            && backing.FieldType == property.PropertyType
            && instructions.Length == 3 && instructions[0].OpCode.Code == Code.Ldarg_0
            && instructions[1].OpCode.Code == Code.Ldfld && instructions[2].OpCode.Code == Code.Ret
            && instructions[1].Operand is FieldReference field
            && field.Name == backing.Name && ReflectionName(field.DeclaringType) == property.DeclaringType.FullName;
    }

    private static bool DerivesCanonicalKey(MethodInfo getter, PropertyInfo source)
    {
        using var module = ModuleDefinition.ReadModule(getter.Module.Assembly.Location);
        var method = (MethodDefinition)module.LookupToken(getter.MetadataToken);
        if (!method.HasBody) return false;
        var instructions = method.Body.Instructions.ToArray();
        // Accept compiler local spills in Debug/Release, but no constants other than D,
        // conditional paths, arithmetic, other field reads or helper calls. Unsupported
        // derivations fail loudly; add a proof before extending this deliberately small grammar.
        if (instructions.Any(instruction => instruction.OpCode.Code is not
            (Code.Nop or Code.Ldarg_0 or Code.Ldstr or Code.Call or Code.Callvirt or Code.Ret
            or Code.Ldloc or Code.Ldloc_S or Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3
            or Code.Ldloca or Code.Ldloca_S or Code.Stloc or Code.Stloc_S or Code.Stloc_0
            or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3 or Code.Br or Code.Br_S))) return false;
        if (!instructions.Where(instruction => instruction.OpCode.Code == Code.Ldstr)
            .Select(instruction => instruction.Operand).SequenceEqual(new object[] { "D" })) return false;
        var calls = instructions.Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
            .Select(instruction => (MethodReference)instruction.Operand).ToArray();
        var typed = source.PropertyType != typeof(Guid);
        return calls.Length == (typed ? 3 : 2)
            && calls[0].Name == source.GetMethod!.Name && calls[0].Parameters.Count == 0
            && ReflectionName(calls[0].DeclaringType) == source.DeclaringType!.FullName
            && (!typed || (calls[1].Name == "get_Value" && calls[1].Parameters.Count == 0
                && ReflectionName(calls[1].DeclaringType) == source.PropertyType.FullName))
            && calls[^1].DeclaringType.FullName == typeof(Guid).FullName && calls[^1].Name == nameof(Guid.ToString)
            && calls[^1].Parameters.Count == 1 && calls[^1].Parameters[0].ParameterType.FullName == typeof(string).FullName;
    }

    private static string ReflectionName(TypeReference type) => type.FullName.Replace('/', '+');

    private static IntegrationEventBase Sample(Type type, int variation)
    {
        var values = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetIndexParameters().Length == 0 && IsIdentifier(property.PropertyType))
            .ToDictionary(property => property.Name,
                property => (object?)(Nullable.GetUnderlyingType(property.PropertyType) is not null && variation == 1
                    ? null : SampleIdentifier(property.Name, variation)), StringComparer.Ordinal);
        values[nameof(IIntegrationEvent.OccurredAt)] = DateTimeOffset.UnixEpoch.AddHours(variation);
        return (IntegrationEventBase)(JsonSerializer.Deserialize(JsonSerializer.Serialize(values), type,
            IntegrationEventBase.PayloadJsonOptions) ?? throw new InvalidOperationException("The concrete event deserialized to null."));
    }

    private static Guid SampleIdentifier(string property, int variation)
    {
        // Ordinal character folding gives every property a repeatable distinct identifier;
        // no randomized string hashes, clocks or random IDs enter an architecture failure.
        var discriminator = property.Aggregate(0, (value, character) => (value * 31 + character) & 0x7fff);
        var changed = property == nameof(IIntegrationEvent.TenantId) ? variation == 1
            : property == nameof(IIntegrationEvent.EventId) ? variation == 2 : variation == 3;
        return new Guid(0x01988331 + (changed ? 1 : 0), (short)discriminator, 0x7abc,
            0x8d, 0xef, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff);
    }

    private static Guid? IdentifierValue(object? value) => value switch
    {
        Guid id => id,
        IStronglyTypedId<Guid> id when id.IsInitialized() => id.Value,
        _ => null,
    };

    private static IEnumerable<string> PayloadRoundTripViolations(Type type)
    {
        if (PayloadShapeViolations(type).Any()) yield break;
        for (var variation = 0; variation < 2; variation++)
        {
            var failures = new List<string>();
            try
            {
                IntegrationEventBase fact = Sample(type, variation);
                var json = fact.ToPayloadJson();
                using var document = JsonDocument.Parse(json);
                var revived = JsonSerializer.Deserialize(json, fact.GetType(), IntegrationEventBase.PayloadJsonOptions) as IntegrationEventBase;
                var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
                        .SequenceEqual(properties.Select(property => property.Name).Order(StringComparer.Ordinal)))
                    failures.Add($"{type.FullName}: concrete payload members differ from the reflected event shape.");
                if (revived?.GetType() != type || revived.EventId != fact.EventId || revived.TenantId != fact.TenantId
                    || revived.OccurredAt != fact.OccurredAt || revived.Topic != fact.Topic || revived.PartitionKey != fact.PartitionKey)
                    failures.Add($"{type.FullName}: concrete runtime JSON did not round-trip the common event contract.");
                foreach (var property in properties.Where(property => IsIdentifier(property.PropertyType)))
                {
                    var expected = IdentifierValue(property.GetValue(fact));
                    if (!document.RootElement.TryGetProperty(property.Name, out var serialized)
                        || (expected.HasValue ? serialized.ValueKind != JsonValueKind.String || serialized.GetGuid() != expected.Value
                            : serialized.ValueKind != JsonValueKind.Null)
                        || revived is null || IdentifierValue(property.GetValue(revived)) != expected)
                        failures.Add($"{type.FullName}: identifier {property.Name} was dropped or changed by concrete runtime JSON (probe {variation}).");
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                or NotSupportedException or JsonException or TargetInvocationException)
            {
                failures.Add($"{type.FullName}: concrete runtime JSON probe {variation} failed ({exception.GetType().Name}).");
            }
            foreach (var failure in failures) yield return failure;
        }
    }

    private static IEnumerable<string> ControlViolations(Func<Type, IEnumerable<string>> predicate, Type[] dirty, Type[] clean)
    {
        foreach (var type in dirty.Where(type => !predicate(type).Any()))
            yield return $"Planted violation {type.FullName} escaped the production predicate.";
        foreach (var type in clean)
            foreach (var failure in predicate(type))
                yield return $"Clean control rejected: {failure}";
    }

    private static void AssertClean(IEnumerable<string> violations, string fix)
    {
        var failures = violations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.True(failures.Length == 0, string.Join(Environment.NewLine, failures) + $"{Environment.NewLine}Fix: {fix}.");
    }

    // These fixtures are intentionally outside ProductionAssemblies.All(). The same
    // predicates must distinguish them; enqueue's internal validator is never called.
    private sealed class DirectInterfaceFact : IIntegrationEvent
    {
        public Guid EventId => Guid.Empty;
        public Guid TenantId => Guid.Empty;
        public DateTimeOffset OccurredAt => DateTimeOffset.UnixEpoch;
        public string Topic => "learnstack.test.fact";
        public string PartitionKey => "constant";
    }

    private abstract record AggregateFact : IntegrationEventBase
    {
        public Guid AggregateId { get; init; }
        public override string Topic => "learnstack.test.fact";
        public override string PartitionKey => AggregateId.ToString("D");
    }

    [IntegrationEventPartition(nameof(AggregateId))]
    private sealed record ApprovedIdentifiersFact : AggregateFact
    {
        public Guid RelatedId { get; init; }
        public Guid? OptionalId { get; init; }
        public TenantId RelatedTenant { get; init; }
        public TenantId? OptionalTenant { get; init; }
        public OrganizationId RelatedOrganization { get; init; }
        public OrganizationId? OptionalOrganization { get; init; }
        public UserId RelatedUser { get; init; }
        public UserId? OptionalUser { get; init; }
    }

    [IntegrationEventPartition(nameof(TenantId), tenantWide: true)]
    private sealed record TenantWideFact : AggregateFact
    {
        public override string PartitionKey => TenantId.ToString("D");
    }

    [IntegrationEventPartition(nameof(SubjectId))]
    private sealed record TypedPartitionFact : AggregateFact
    {
        public OrganizationId SubjectId { get; init; }
        public override string PartitionKey => SubjectId.Value.ToString("D");
    }

    [IntegrationEventPartition(nameof(AggregateId))]
    private sealed record ConstantPartitionFact : AggregateFact
    {
        public override string PartitionKey => "01988331-aaaa-7abc-8def-aabbccddeeff";
    }

    [IntegrationEventPartition(nameof(AggregateId))]
    private sealed record UnrelatedPartitionFact : AggregateFact
    {
        public override string PartitionKey => EventId.ToString("D");
    }

    private sealed record MissingPartitionFact : AggregateFact;
    [IntegrationEventPartition(nameof(EventId))] private sealed record EventIdentityPartitionFact : AggregateFact;
    [IntegrationEventPartition(nameof(TenantId))] private sealed record ImplicitTenantFact : AggregateFact;
    [IntegrationEventPartition(nameof(AggregateId), tenantWide: true)] private sealed record MislabelledTenantFact : AggregateFact;
    [IntegrationEventPartition("UnknownId")] private sealed record UnknownSourceFact : AggregateFact;

    [IntegrationEventPartition(nameof(SubjectId))]
    private sealed record NullablePartitionFact : AggregateFact
    {
        public Guid? SubjectId { get; init; }
    }

    [IntegrationEventPartition(nameof(SubjectId))]
    private sealed record ComputedSourceFact : AggregateFact
    {
        public Guid SubjectId => EventId;
        public override string PartitionKey => SubjectId.ToString("D");
    }

    [IntegrationEventPartition(nameof(SubjectId))]
    private sealed record EmptySourceFact : AggregateFact
    {
        public Guid SubjectId { get; } = Guid.Empty;
        public override string PartitionKey => SubjectId.ToString("D");
    }

    [IntegrationEventPartition(nameof(AggregateId))]
    private sealed record UppercasePartitionFact : AggregateFact
    {
        public override string PartitionKey => AggregateId.ToString("D").ToUpperInvariant();
    }

    private record PayloadFact<T> : AggregateFact
    {
        public T Data { get; init; } = default!;
    }
    private sealed record InheritedTextFact : PayloadFact<string>;
    private readonly record struct UnapprovedId(Guid Value) : IStronglyTypedId<Guid>
    {
        public bool IsInitialized() => true;
    }
    private sealed record ExtraFieldFact : AggregateFact { public Guid ExtraId = Guid.Empty; }
    private sealed record HiddenRoutingFact : AggregateFact { public new Guid Topic { get; init; } }
    private sealed record RemappedInterfaceFact : AggregateFact, IIntegrationEvent { Guid IIntegrationEvent.TenantId => Guid.Empty; }
    private sealed record IgnoredIdentifierFact : AggregateFact { [JsonIgnore] public Guid RelatedId { get; init; } }
    private sealed record RenamedIdentifierFact : AggregateFact { [JsonPropertyName("OtherId")] public Guid RelatedId { get; init; } }
    private sealed record HiddenWriterFact : AggregateFact { public new string ToPayloadJson() => EventId.ToString("D"); }
    private sealed record SerializingFact : AggregateFact, IJsonOnSerializing { public void OnSerializing() { } }
    private sealed record SerializedFact : AggregateFact, IJsonOnSerialized { public void OnSerialized() { } }
    private sealed record DeserializingFact : AggregateFact, IJsonOnDeserializing { public void OnDeserializing() { } }
    private sealed record DeserializedFact : AggregateFact, IJsonOnDeserialized { public void OnDeserialized() { } }
    private sealed record EnumerableFact : AggregateFact, IEnumerable
    {
        public IEnumerator GetEnumerator() => Array.Empty<Guid>().GetEnumerator();
    }

    [JsonConverter(typeof(FactConverter))]
    private sealed record ConvertedFact : AggregateFact;
    private sealed class FactConverter : JsonConverter<ConvertedFact>
    {
        public override ConvertedFact? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, ConvertedFact value, JsonSerializerOptions options) => throw new NotSupportedException();
    }
}
