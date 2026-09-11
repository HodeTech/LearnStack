using System.Reflection;
using FluentAssertions;
using LearnStack.SharedKernel;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// The shape every aggregate carries: identity equality declared once, and an identifier
/// the generator emitted — catalogued in
/// <see href="../../../docs/standards/21-architecture-tests-catalogue.md">Standards 21
/// § Repository layout and module boundaries</see>.
/// </summary>
/// <remarks>
/// Both rules are about what a type may <b>not</b> declare, and both failures are silent:
/// a second equality answers differently depending on the static type of the variable
/// holding it, and a hand-written identifier satisfies every interface the domain asks for
/// while carrying none of the converters the infrastructure assumes.
/// </remarks>
public sealed class DomainModelTests
{
    [Fact]
    public void Aggregates_Do_Not_Redeclare_Entity_Equality()
    {
        // Entity<TId> is the single implementation: Equals(object?), GetHashCode() and the
        // operators all route to Equals(Entity<TId>?), and the first two are sealed so a
        // derived type cannot override them (ADR-0023 Amendment 3; Backend Coding Standards
        // § Domain Modeling). What is NOT blocked by the compiler is a derived OVERLOAD —
        // `bool Equals(Course? other)` is a new method, so there is nothing to seal and the
        // compiler says nothing. Measured on such a type: a.Equals(b) answers true while
        // a == b and ((Entity<CourseId>)a).Equals(b) answer false for the same pair. Three
        // answers to one question, decided by the static type at the call site.
        var entities = ProductionAssemblies.All()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(DerivesFromEntity)
            .ToList();

        entities.Should().Contain(typeof(LearnStack.Modules.Tenancy.Domain.Tenant),
            "the premise: the scan sees the aggregates it is meant to hold to this");

        entities.SelectMany(type => RedeclaredEquality(type)
                .Select(member => $"{type.FullName}.{member}"))
            .Should().BeEmpty(
                "identity equality is declared once, in Entity<TId> — a second declaration "
                + "answers differently depending on the static type of the reference");
    }

    [Fact]
    public void Aggregate_Roots_Use_StronglyTypedId()
    {
        // The IAggregateRoot<TId> constraint already makes TId an IStronglyTypedId<Guid>,
        // which a hand-written struct satisfies in an afternoon. The attribute is what makes
        // it a GENERATED value object — the EF Core converter EF binds through, the JSON
        // converter the API serializes with, the TypeConverter route binding needs, and the
        // IsInitialized() the transient guard reads (ADR-0023 § Implementation Notes and
        // Amendment 1). A hand-written id compiles and then fails at the first of those.
        var roots = ProductionAssemblies.All()
            .SelectMany(assembly => assembly.GetTypes())
            .SelectMany(type => AggregateIdentifiers(type).Select(id => (Type: type, Id: id)))
            .ToList();

        roots.Should().Contain(root => root.Id == typeof(TenantId),
            "the premise: the scan sees the aggregate roots and reads their identifiers");

        roots.Where(root => !CarriesTheIdMask(root.Id))
            .Select(root => $"{root.Type.FullName} ({root.Id.Name})")
            .Should().BeEmpty(
                "an aggregate root's id is a Vogen [ValueObject<Guid>] carrying "
                + "LearnStackVogenDefaults.IdMask (ADR-0023)");
    }

    [Fact]
    public void The_Aggregate_Shape_Rules_Can_Actually_Fail()
    {
        // No production type breaks either rule, so both pass whether their predicates work
        // or not. The probes below are the four shapes the catalogue entries name, planted
        // in this assembly, and each must be reported by the same predicate the rule uses.
        RedeclaredEquality(typeof(Probes.OverloadingEntity)).Should().ContainSingle()
            .Which.Should().Be("Equals", "a typed overload is a second answer");
        RedeclaredEquality(typeof(Probes.SelfEquatableEntity)).Should().NotBeEmpty(
            "an explicitly implemented IEquatable<TSelf> is named "
            + "System.IEquatable<TSelf>.Equals in metadata, which a plain name check misses");
        RedeclaredEquality(typeof(Probes.ReimplementingEntity)).Should().NotBeEmpty(
            "re-implementing the INHERITED IEquatable<Entity<TId>> declares no new interface, "
            + "so GetInterfaces().Except(BaseType.GetInterfaces()) sees nothing");
        RedeclaredEquality(typeof(Probes.PlainEntity)).Should().BeEmpty(
            "an aggregate that declares no equality member is what every real one looks like");

        // And the shapes the equality predicate must not confuse with a redeclaration.
        DerivesFromEntity(typeof(Probes.PlainEntity)).Should().BeTrue();
        DerivesFromEntity(typeof(Entity<>)).Should().BeFalse("the base type is not a subject");

        // The identifier predicate: a hand-written struct satisfies IStronglyTypedId<Guid>
        // and carries no attribute; a generated one with a different mask carries the wrong
        // one; a real id passes.
        AggregateIdentifiers(typeof(Probes.HandWrittenIdRoot)).Should().Equal(typeof(Probes.ProbeId));
        CarriesTheIdMask(typeof(Probes.ProbeId)).Should().BeFalse();
        CarriesTheIdMask(typeof(Probes.WrongMaskProbeId)).Should().BeFalse(
            "a value object without the EF Core, JSON and TypeConverter conversions is not an id");
        CarriesTheIdMask(typeof(TenantId)).Should().BeTrue();
    }

    /// <summary>Whether a type is an aggregate or entity below <see cref="Entity{TId}"/>.</summary>
    private static bool DerivesFromEntity(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Entity<>))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The equality members a type declares itself — the ones <see cref="Entity{TId}"/>
    /// cannot seal.
    /// </summary>
    /// <remarks>
    /// Declared slots, not the interface list: a type that re-implements the inherited
    /// <c>IEquatable&lt;Entity&lt;TId&gt;&gt;</c> explicitly adds no interface and no method
    /// named <c>Equals</c> — the slot is <c>System.IEquatable&lt;Entity&lt;TId&gt;&gt;.Equals</c>
    /// — so the name is matched with <c>EndsWith</c> and the members are read with
    /// <see cref="BindingFlags.DeclaredOnly"/>.
    /// </remarks>
    private static IEnumerable<string> RedeclaredEquality(Type type)
    {
        const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        foreach (var method in type.GetMethods(Declared))
        {
            if (method.Name.EndsWith("Equals", StringComparison.Ordinal)
                || method.Name.EndsWith("GetHashCode", StringComparison.Ordinal)
                || method.Name is "op_Equality" or "op_Inequality")
            {
                yield return method.Name;
            }
        }

        if (!type.IsGenericTypeDefinition
            && typeof(IEquatable<>).MakeGenericType(type).IsAssignableFrom(type))
        {
            yield return $"IEquatable<{type.Name}>";
        }
    }

    /// <summary>The identifiers a type is an aggregate root of, as declared on it.</summary>
    private static IEnumerable<Type> AggregateIdentifiers(Type type) =>
        type.GetInterfaces()
            .Where(contract => contract.IsGenericType
                && contract.GetGenericTypeDefinition() == typeof(IAggregateRoot<>))
            .Select(contract => contract.GenericTypeArguments[0])
            .Where(id => !id.IsGenericParameter)
            .Distinct();

    /// <summary>
    /// Whether an identifier is a Vogen <c>[ValueObject&lt;Guid&gt;]</c> declared with
    /// <see cref="LearnStackVogenDefaults.IdMask"/>.
    /// </summary>
    /// <remarks>
    /// The mask is read from the attribute's <c>conversions</c> argument rather than assumed
    /// from the attribute's presence: the converters are the reason the attribute is
    /// required, and a value object declared without them is an id EF Core cannot bind.
    /// </remarks>
    private static bool CarriesTheIdMask(Type identifier) =>
        identifier.GetCustomAttributesData().Any(attribute =>
            attribute.AttributeType.IsGenericType
            && attribute.AttributeType.GetGenericTypeDefinition().FullName == "Vogen.ValueObjectAttribute`1"
            && attribute.AttributeType.GenericTypeArguments[0] == typeof(Guid)
            && Conversions(attribute) == (int)LearnStackVogenDefaults.IdMask);

    /// <summary>The attribute's <c>conversions</c> argument, or <c>null</c> when it has none.</summary>
    private static int? Conversions(CustomAttributeData attribute)
    {
        var parameters = attribute.Constructor.GetParameters();

        for (var index = 0; index < parameters.Length && index < attribute.ConstructorArguments.Count; index++)
        {
            if (parameters[index].Name == "conversions")
            {
                return Convert.ToInt32(attribute.ConstructorArguments[index].Value, provider: null);
            }
        }

        return attribute.NamedArguments
            .Where(argument => argument.MemberName == "Conversions")
            .Select(argument => (int?)Convert.ToInt32(argument.TypedValue.Value, provider: null))
            .FirstOrDefault();
    }
}
