using FluentAssertions;
using LearnStack.SharedKernel.Domain;
using MediatR;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Domain;

public sealed class DomainEventTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 01, 01, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IDomainEvent_IsAMediatRNotification()
    {
        typeof(INotification).IsAssignableFrom(typeof(IDomainEvent))
            .Should().BeTrue("domain events dispatch in-process through MediatR");
    }

    [Fact]
    public void Stamped_Event_CarriesTheSuppliedEventIdAndOccurredAt()
    {
        var eventId = Guid.CreateVersion7();

        var @event = new TestDomainEvent("payload")
        {
            EventId = eventId,
            OccurredAt = T0,
        };

        @event.EventId.Should().Be(eventId);
        @event.OccurredAt.Should().Be(T0);
    }
}
