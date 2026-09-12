using System.Diagnostics.CodeAnalysis;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using Vogen;

namespace LearnStack.Tests.Architecture.Probes;

/// <summary>
/// A hand-written identifier: it satisfies every constraint <see cref="Entity{TId}"/> and
/// <see cref="IAggregateRoot{TId}"/> impose and carries none of the generated converters.
/// For <c>The_Aggregate_Shape_Rules_Can_Actually_Fail</c>.
/// </summary>
internal readonly record struct ProbeId(Guid Value) : IStronglyTypedId<Guid>
{
    public bool IsInitialized() => Value != Guid.Empty;
}

/// <summary>
/// A value object declared without the conversions an id needs — the generator emits no EF
/// Core converter for it. Inert here: this project references Vogen's attributes, not its
/// generator.
/// </summary>
[ValueObject<Guid>(Conversions.SystemTextJson)]
internal readonly partial struct WrongMaskProbeId;

/// <summary>An aggregate that declares no equality member, as every real one does.</summary>
internal sealed class PlainEntity : Entity<ProbeId>;

/// <summary>
/// Declares a domain predicate whose name ends in <c>Equals</c> and redeclares nothing.
/// </summary>
internal sealed class PredicateEntity : Entity<ProbeId>
{
    public bool SlugEquals(string slug) => slug.Length == Id.Value.ToString().Length;
}

/// <summary>Declares a typed <c>Equals</c> overload — the three-answers case.</summary>
internal sealed class OverloadingEntity : Entity<ProbeId>
{
    public bool Equals(OverloadingEntity? other) => other is not null && Id.Equals(other.Id);
}

/// <summary>Declares <c>IEquatable&lt;TSelf&gt;</c> and implements it explicitly.</summary>
[SuppressMessage("Design", "CA1067:Override Object.Equals(object) when implementing IEquatable<T>",
    Justification = "Entity<TId> seals Equals(object?), so this type cannot override it — which "
        + "is what makes it the shape Aggregates_Do_Not_Redeclare_Entity_Equality must catch.")]
internal sealed class SelfEquatableEntity : Entity<ProbeId>, IEquatable<SelfEquatableEntity>
{
    bool IEquatable<SelfEquatableEntity>.Equals(SelfEquatableEntity? other) => other is not null;
}

/// <summary>
/// Re-implements the <b>inherited</b> <c>IEquatable&lt;Entity&lt;ProbeId&gt;&gt;</c>
/// explicitly: it declares no new interface, so an interface-difference check sees nothing.
/// </summary>
[SuppressMessage("Design", "CA1067:Override Object.Equals(object) when implementing IEquatable<T>",
    Justification = "Entity<TId> seals Equals(object?) and already implements this interface; "
        + "re-implementing it explicitly is the shape the rule must catch.")]
internal sealed class ReimplementingEntity : Entity<ProbeId>, IEquatable<Entity<ProbeId>>
{
    bool IEquatable<Entity<ProbeId>>.Equals(Entity<ProbeId>? other) => other is not null;
}

/// <summary>
/// Hides the sealed <c>GetHashCode</c> with a new one. The compiler allows it — measured — so
/// the rule is what catches it: two hash codes for one entity partition a <c>HashSet</c> by
/// which reference the caller holds.
/// </summary>
internal sealed class HidingHashEntity : Entity<ProbeId>
{
    public new int GetHashCode() => Id.Value.GetHashCode();
}

/// <summary>An aggregate root whose identifier is hand-written.</summary>
internal sealed class HandWrittenIdRoot : Entity<ProbeId>, IAggregateRoot<ProbeId>;
