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

    // ---- the concurrency token (ADR-0039) --------------------------------

    [Fact]
    public void MarkCreated_LeavesTheVersionAtZero()
    {
        // The column's DEFAULT 0 and the CLR default have to agree, or an insert
        // needs a special case that nothing would remember to write.
        var aggregate = new TestAuditableAggregate(TestId.New());

        aggregate.MarkCreated(T0, Actor);

        aggregate.Version.Should().Be(0);
    }

    [Fact]
    public void MarkUpdated_AdvancesTheRowVersion()
    {
        var aggregate = new TestAuditableAggregate(TestId.New());
        aggregate.MarkCreated(T0, Actor);

        aggregate.MarkUpdated(T0.AddHours(1), Actor);
        aggregate.MarkUpdated(T0.AddHours(2), Actor);

        aggregate.Version.Should().Be(2, "every audited mutation is a versioned mutation");
    }

    [Fact]
    public void SoftDelete_Advances_The_Row_Version()
    {
        // The case that fails when the increment lives in MarkUpdated alone.
        // SoftDelete stamps UpdatedAt/UpdatedBy itself, so a delete would leave
        // the token where it was — and a client holding the pre-delete ETag would
        // still satisfy If-Match on the row it had just deleted. Route both paths
        // through one primitive and this cannot happen; delete the routing and
        // this test is what notices.
        var aggregate = new TestAuditableAggregate(TestId.New());
        aggregate.MarkCreated(T0, Actor);
        aggregate.MarkUpdated(T0.AddHours(1), Actor);
        var beforeDelete = aggregate.Version;

        aggregate.SoftDelete(T0.AddDays(3), Actor);

        aggregate.Version.Should().BeGreaterThan(beforeDelete);
    }

    [Fact]
    public void TheVersionIsWideEnoughForTheColumnItMapsTo()
    {
        // row_version is bigint, so the CLR side is long. It was uint — the
        // Npgsql convention for an xmin token, which ADR-0039 rejected — and a
        // uint property against a bigint column round-trips wrong at the top of
        // the range rather than failing loudly.
        typeof(IOptimisticConcurrency)
            .GetProperty(nameof(IOptimisticConcurrency.Version))!
            .PropertyType.Should().Be<long>();
    }

    [Fact]
    public void ISoftDelete_DeletedBy_IsStronglyTypedUserId()
    {
        var aggregate = new TestAuditableAggregate(TestId.New());
        aggregate.MarkCreated(T0, Actor);
        aggregate.SoftDelete(T0.AddDays(1), Actor);

        // The cast is the point of this test - the marker contract is what
        // we are asserting. CA1859 would prefer the concrete type for
        // performance.
#pragma warning disable CA1859
        ISoftDelete view = aggregate;
#pragma warning restore CA1859

        view.DeletedBy.Should().Be(Actor);
    }

    // Vogen prohibits constructing `default(UserId)` at compile time (VOG009),
    // so the "empty actor" case is exercised via UserId.From(Guid.Empty) -
    // the guard reads `by.Value == Guid.Empty`, which both forms hit.
    private static readonly UserId EmptyActor = UserId.From(Guid.Empty);

    [Fact]
    public void MarkCreated_WithDefaultTimestamp_Throws()
    {
        var aggregate = new TestAuditableAggregate(TestId.New());

        var act = () => aggregate.MarkCreated(default, Actor);

        act.Should().Throw<ArgumentException>().WithMessage("*default*");
    }

    [Fact]
    public void MarkCreated_WithEmptyActor_Throws()
    {
        var aggregate = new TestAuditableAggregate(TestId.New());

        var act = () => aggregate.MarkCreated(T0, EmptyActor);

        act.Should().Throw<ArgumentException>().WithMessage("*UserId*");
    }

    [Fact]
    public void MarkUpdated_WithInvalidInputs_Throws()
    {
        var aggregate = new TestAuditableAggregate(TestId.New());
        aggregate.MarkCreated(T0, Actor);

        var defaultAt = () => aggregate.MarkUpdated(default, Actor);
        var emptyBy = () => aggregate.MarkUpdated(T0.AddHours(1), EmptyActor);

        defaultAt.Should().Throw<ArgumentException>();
        emptyBy.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SoftDelete_WithInvalidInputs_Throws()
    {
        var aggregate = new TestAuditableAggregate(TestId.New());
        aggregate.MarkCreated(T0, Actor);

        var defaultAt = () => aggregate.SoftDelete(default, Actor);
        var emptyBy = () => aggregate.SoftDelete(T0.AddDays(1), EmptyActor);

        defaultAt.Should().Throw<ArgumentException>();
        emptyBy.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarkCreated_WithUninitializedActor_ThrowsArgumentException()
    {
        // Vogen's analyzer rejects `default(UserId)` outright (VOG009), so this
        // state cannot be written on purpose — it is only ever arrived at by
        // omission, which is why it went unnoticed. A command record whose ActorId
        // nothing assigns is the realistic shape. The guard must answer with its own
        // ArgumentException naming the parameter, not with Vogen's
        // ValueObjectValidationException, which is what reading .Value before
        // IsInitialized() produced.
        var aggregate = new TestAuditableAggregate(TestId.New());
        var command = new CommandWithUnassignedActor("Any title");

        aggregate.Invoking(a => a.MarkCreated(DateTimeOffset.UtcNow, command.ActorId))
            .Should().Throw<ArgumentException>()
            .WithParameterName("by");
    }

    [Fact]
    public void NewlyConstructed_AggregateIsNotDeleted()
    {
        var aggregate = new TestAuditableAggregate(TestId.New());

        aggregate.IsDeleted.Should().BeFalse();
        aggregate.Version.Should().Be(0u);
    }

    // A command whose ActorId is never assigned — the only way an uninitialized
    // UserId reaches a guard, since Vogen forbids writing default(UserId).
    private sealed record CommandWithUnassignedActor(string Title)
    {
        public UserId ActorId { get; init; }
    }
}
