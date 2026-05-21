using FluentAssertions;
using LearnStack.SharedKernel.Domain;
using MediatR;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Domain;

public sealed class DomainEventTests
{
    [Fact]
    public void IDomainEvent_IsAMediatRNotification()
    {
        typeof(INotification).IsAssignableFrom(typeof(IDomainEvent))
            .Should().BeTrue("domain events dispatch in-process through MediatR");
    }

    [Fact]
    public void DefaultEventId_IsUuidV7()
    {
        var @event = new TestDomainEvent("x");

        @event.EventId.Version.Should().Be(7);
    }

    [Fact]
    public void DefaultOccurredAt_IsRecent()
    {
        var before = DateTimeOffset.UtcNow;

        var @event = new TestDomainEvent("x");

        @event.OccurredAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTimeOffset.UtcNow);
    }

    [Fact]
    public void RecordInit_AllowsTimeOverride()
    {
        var fixedAt = new DateTimeOffset(2026, 01, 01, 0, 0, 0, TimeSpan.Zero);

        var @event = new TestDomainEvent("x") { OccurredAt = fixedAt };

        @event.OccurredAt.Should().Be(fixedAt);
    }
}
