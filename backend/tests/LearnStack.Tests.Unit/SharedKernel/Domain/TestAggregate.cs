using LearnStack.SharedKernel.Domain;

namespace LearnStack.Tests.Unit.SharedKernel.Domain;

/// <summary>
/// Test double for <see cref="Entity{TId}"/>. Exposes
/// <see cref="Raise"/> so unit tests can drive the domain-event collector
/// without inventing a fake aggregate per test.
/// </summary>
internal sealed class TestAggregate : Entity<TestId>
{
    public TestAggregate(TestId id) : base(id) { }

    public TestAggregate() { }

    public void Raise(IDomainEvent domainEvent) => RaiseDomainEvent(domainEvent);
}

/// <summary>
/// Second test entity type — same TId, different runtime type. Used to
/// verify Entity&lt;TId&gt;'s cross-runtime-type equality guard.
/// </summary>
internal sealed class TestAggregateSibling : Entity<TestId>
{
    public TestAggregateSibling(TestId id) : base(id) { }
}

/// <summary>
/// Test double for <see cref="AuditableEntity{TId}"/>.
/// </summary>
internal sealed class TestAuditableAggregate : AuditableEntity<TestId>
{
    public TestAuditableAggregate(TestId id) : base(id) { }

    public TestAuditableAggregate() { }
}

internal sealed record TestDomainEvent(string Payload) : DomainEvent;
