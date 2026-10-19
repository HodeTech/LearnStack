using System.Data.Common;
using System.Reflection;
using FluentAssertions;
using LearnStack.Application.Messaging;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using MediatR;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Messaging;

public sealed class DomainEventCollectorTests
{
    [Fact]
    public async Task The_inclusive_256_fact_limit_spans_saves_and_drains_once_in_promotion_order()
    {
        var unit = Unit();
        var producer = new ProducerExecution(unit);
        using var owner = Enroll(producer);
        var publisher = Substitute.For<IPublisher>();
        var published = new List<IDomainEvent>();
        publisher.Publish(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            published.Add(call.Arg<IDomainEvent>());
            return Task.CompletedTask;
        });
        using var collector = new DomainEventCollector(producer, publisher);
        var context = new object();
        var source = new EventSource();
        source.Add(128);
        collector.Stage(context, [source]);
        collector.Promote(context, 1);
        source.Add(128);
        collector.Stage(context, [source]);
        collector.Promote(context, 1);
        // A later save sees the same retained source facts, not 256 new facts.
        collector.Stage(context, [source]);
        collector.Promote(context, 1);

        await collector.DrainAsync(owner);
        await collector.DrainAsync(owner);

        DomainEventCollector.MaxEventsPerTransaction.Should().Be(256);
        published.Should().Equal(source.DomainEvents).And.HaveCount(256);
        owner.HasProducedWork.Should().BeTrue();
        unit.DidNotReceive().MarkRollbackOnly();
    }

    [Fact]
    public void Fact_257_refuses_promotion_with_the_exact_limit_error_and_poisons_the_unit()
    {
        var unit = Unit();
        var producer = new ProducerExecution(unit);
        using var owner = Enroll(producer);
        var publisher = Substitute.For<IPublisher>();
        using var collector = new DomainEventCollector(producer, publisher);
        var context = new object();
        var source = new EventSource();
        source.Add(256);
        collector.Stage(context, [source]);
        collector.Promote(context, 1);
        unit.DidNotReceive().MarkRollbackOnly();
        source.Add(1);
        collector.Stage(context, [source]);

        var promote = () => collector.Promote(context, 1);

        promote.Should().Throw<InvalidOperationException>()
            .WithMessage("A transaction cannot produce more than 256 domain events.");
        unit.Received(1).MarkRollbackOnly();
        owner.HasProducedWork.Should().BeTrue();
        publisher.ReceivedCalls().Should().BeEmpty("promotion admits facts but never dispatches them");
    }

    private static IUnitOfWork Unit()
    {
        var unit = Substitute.For<IUnitOfWork>();
        unit.Transaction.Returns(Substitute.For<DbTransaction>());
        unit.HasActiveTransaction.Returns(true);
        unit.Mode.Returns(TransactionMode.ReadWrite);
        unit.GetTenantContextAnnouncement(unit.Transaction).Returns(new TenantContextAnnouncement(1,
            TenantId.From(Guid.Parse("019712ac-1234-7000-8000-0000000000ab")), null));
        return unit;
    }

    // Enter the real guarded enrollment method without widening the production
    // owner census with a public test seam or forging an invocation token.
    private static ProducerExecution.Invocation Enroll(ProducerExecution producer) =>
        (ProducerExecution.Invocation)typeof(ProducerExecution)
            .GetMethod("BeginInvocation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(producer, [true, null, null, null, null])!;

    private sealed class EventSource : IHasDomainEvents
    {
        private readonly List<IDomainEvent> _events = [];
        public IReadOnlyCollection<IDomainEvent> DomainEvents => _events;
        public void ClearDomainEvents() => _events.Clear();
        internal void Add(int count)
        {
            for (var index = 0; index < count; index++)
                _events.Add(new Fact
                {
                    EventId = Guid.CreateVersion7(),
                    OccurredAt = DateTimeOffset.UnixEpoch,
                });
        }
    }

    private sealed record Fact : DomainEvent;
}
