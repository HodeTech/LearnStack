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
