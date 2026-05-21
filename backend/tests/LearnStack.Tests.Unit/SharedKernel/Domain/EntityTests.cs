using FluentAssertions;
using LearnStack.SharedKernel.Domain;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Domain;

public sealed class EntityTests
{
    [Fact]
    public void Equality_IsIdentityBased()
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
    public void RaiseDomainEvent_AppendsToCollection()
    {
        var aggregate = new TestAggregate(TestId.New());
        IDomainEvent @event = new TestDomainEvent("hello");

        aggregate.Raise(@event);

        aggregate.DomainEvents.Should().ContainSingle().Which.Should().Be(@event);
    }

    [Fact]
    public void ClearDomainEvents_EmptiesTheCollection()
    {
        var aggregate = new TestAggregate(TestId.New());
        aggregate.Raise(new TestDomainEvent("a"));
        aggregate.Raise(new TestDomainEvent("b"));

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
