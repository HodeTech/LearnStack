using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Domain;

public sealed class AuditableEntityTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 05, 21, 10, 00, 00, TimeSpan.Zero);

    private static readonly UserId Actor =
        UserId.From(Guid.Parse("019712ac-aaaa-7000-8000-000000000aaa"));

    [Fact]
    public void MarkCreated_SetsCreatedAtAndCreatedBy()
    {
        var aggregate = new TestAuditableAggregate(TestId.New());

        aggregate.MarkCreated(T0, Actor);

        aggregate.CreatedAt.Should().Be(T0);
        aggregate.CreatedBy.Should().Be(Actor);
        aggregate.UpdatedAt.Should().BeNull();
        aggregate.DeletedAt.Should().BeNull();
        aggregate.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public void MarkCreated_CalledTwice_Throws()
    {
        var aggregate = new TestAuditableAggregate(TestId.New());
        aggregate.MarkCreated(T0, Actor);

        var act = () => aggregate.MarkCreated(T0.AddHours(1), Actor);

        act.Should().Throw<InvalidOperationException>().WithMessage("*already*");
    }

    [Fact]
    public void MarkUpdated_SetsUpdatedAtAndUpdatedBy()
    {
        var aggregate = new TestAuditableAggregate(TestId.New());
        aggregate.MarkCreated(T0, Actor);
        var laterActor = UserId.From(Guid.Parse("019712ac-bbbb-7000-8000-000000000bbb"));
        var t1 = T0.AddHours(1);

        aggregate.MarkUpdated(t1, laterActor);

        aggregate.UpdatedAt.Should().Be(t1);
        aggregate.UpdatedBy.Should().Be(laterActor);
        aggregate.CreatedAt.Should().Be(T0, "create stays untouched");
    }

    [Fact]
    public void SoftDelete_SetsDeletedColumns_AndAlsoBumpsUpdated()
    {
        var aggregate = new TestAuditableAggregate(TestId.New());
        aggregate.MarkCreated(T0, Actor);
        var t1 = T0.AddDays(3);

        aggregate.SoftDelete(t1, Actor);

        aggregate.DeletedAt.Should().Be(t1);
        aggregate.DeletedBy.Should().Be(Actor);
        aggregate.IsDeleted.Should().BeTrue();
        aggregate.UpdatedAt.Should().Be(t1, "SoftDelete bumps UpdatedAt for monotonic last-touched");
        aggregate.UpdatedBy.Should().Be(Actor);
    }

    [Fact]
    public void ISoftDelete_DeletedBy_ProjectsGuidFromUserId()
    {
        var aggregate = new TestAuditableAggregate(TestId.New());
        aggregate.MarkCreated(T0, Actor);
        aggregate.SoftDelete(T0.AddDays(1), Actor);

        ISoftDelete view = aggregate;

        view.DeletedBy.Should().Be(Actor.Value);
    }

    [Fact]
    public void NewlyConstructed_AggregateIsNotDeleted()
    {
        var aggregate = new TestAuditableAggregate(TestId.New());

        aggregate.IsDeleted.Should().BeFalse();
        aggregate.Version.Should().Be(0u);
    }
}
