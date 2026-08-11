using FluentAssertions;
using LearnStack.SharedKernel.Domain;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Domain;

public sealed class EntityTests
{
    [Fact]
    public void Equality_IsIdentityBased_ForPersistedIds()
    {
        var id = TestId.New();
        var a = new TestAggregate(id);
        var b = new TestAggregate(id);

        a.Equals(b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Equality_AcrossDifferentIds_IsFalse()
    {
        var a = new TestAggregate(TestId.New());
        var b = new TestAggregate(TestId.New());

        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void Equality_TwoTransientEntities_AreNeverEqual()
    {
        // Both default-id ("not yet persisted") — must not collapse in the
        // change tracker / HashSet-based collection navigations.
        // The hash falls back to reference identity (object.GetHashCode);
        // we only assert the Equals contract because RuntimeHelpers identity
        // hashes are not guaranteed-distinct (collision is rare but legal).
        var a = new TestAggregate();
        var b = new TestAggregate();

        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void Equality_TransientEntityEqualsItself_ByReference()
    {
        var a = new TestAggregate();

        a.Equals(a).Should().BeTrue();
    }

    [Fact]
    public void Equality_DifferentRuntimeType_SameId_IsFalse()
    {
        // Two entity types that share Entity<TestId> and the same Id value
        // must still compare false — they describe different aggregates.
        var id = TestId.New();
        Entity<TestId> a = new TestAggregate(id);
        Entity<TestId> b = new TestAggregateSibling(id);

        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void Entity_ImplementsIEquatable_SoComparisonsDoNotBox()
    {
        // Without IEquatable<Entity<TId>> every comparison goes through
        // Equals(object?) and boxes the struct Id. EqualityComparer<T>.Default
        // picks the typed overload only when the interface is present.
        typeof(TestAggregate).Should().BeAssignableTo<IEquatable<Entity<TestId>>>();
    }

    [Fact]
    public void OperatorEquals_ForPersistedIds_MatchesEquals()
    {
        var id = TestId.New();
        var a = new TestAggregate(id);
        var b = new TestAggregate(id);

        (a == b).Should().BeTrue();
        (a != b).Should().BeFalse();
    }

    [Fact]
    public void OperatorEquals_TwoTransientEntities_AreNeverEqual()
    {
        // The guard that matters most: `==` must not take a shortcut past the
        // transient check. Two unsaved aggregates written as `a == b` collapsing
        // into one is how a change tracker loses a row.
        var a = new TestAggregate();
        var b = new TestAggregate();

        (a == b).Should().BeFalse();
        (a != b).Should().BeTrue();
    }

    [Fact]
    public void OperatorEquals_DifferentRuntimeType_SameId_IsFalse()
    {
        var id = TestId.New();
        Entity<TestId> a = new TestAggregate(id);
        Entity<TestId> b = new TestAggregateSibling(id);

        (a == b).Should().BeFalse();
    }

    [Fact]
    public void OperatorEquals_HandlesNullOnEitherSide()
    {
        var a = new TestAggregate(TestId.New());
        TestAggregate? nothing = null;

        (nothing == null).Should().BeTrue();
        (a == null).Should().BeFalse();
        (null == a).Should().BeFalse();
        (a != null).Should().BeTrue();
    }

    [Fact]
    public void TypedEquals_WithNull_IsFalse()
    {
        var a = new TestAggregate(TestId.New());
        Entity<TestId>? typedNull = null;
        object? untypedNull = null;

        a.Equals(typedNull).Should().BeFalse();
        a.Equals(untypedNull).Should().BeFalse();
    }

    [Fact]
    public void ObjectEquals_WithUnrelatedType_IsFalse()
    {
        var a = new TestAggregate(TestId.New());

        a.Equals("not an entity").Should().BeFalse();
    }

    [Fact]
    public void RaiseDomainEvent_AppendsToCollection()
    {
        var aggregate = new TestAggregate(TestId.New());
        IDomainEvent @event = new TestDomainEvent("hello")
        {
            EventId = Guid.CreateVersion7(),
            OccurredAt = DateTimeOffset.UtcNow,
        };

        aggregate.Raise(@event);

        aggregate.DomainEvents.Should().ContainSingle().Which.Should().Be(@event);
    }

    [Fact]
    public void ClearDomainEvents_EmptiesTheCollection()
    {
        var aggregate = new TestAggregate(TestId.New());
        aggregate.Raise(new TestDomainEvent("a") { EventId = Guid.CreateVersion7(), OccurredAt = DateTimeOffset.UtcNow });
        aggregate.Raise(new TestDomainEvent("b") { EventId = Guid.CreateVersion7(), OccurredAt = DateTimeOffset.UtcNow });

        aggregate.ClearDomainEvents();

        aggregate.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void RaiseDomainEvent_WithNull_Throws()
    {
        var aggregate = new TestAggregate(TestId.New());

        var act = () => aggregate.Raise(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
