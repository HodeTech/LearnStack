using FluentAssertions;
using LearnStack.Modules.Tenancy.Domain.Events;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Time;
using Xunit;
using TenantAggregate = LearnStack.Modules.Tenancy.Domain.Tenant;

namespace LearnStack.Tests.Unit.Modules.Tenancy;

public sealed class TenantCreatedEventTests
{
    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("0199a000-0000-7000-8000-000000000001"));
    private static readonly OrganizationId Organization = OrganizationId.From(Guid.Parse("0199a000-0000-7000-8000-000000000002"));
    private static readonly Guid FactId = Guid.Parse("0199a000-0000-7000-8000-000000000003");
    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Creation_is_a_fact_only_after_the_first_default_organization_assignment()
    {
        var tenant = Create();
        tenant.DomainEvents.Should().BeEmpty();
        tenant.DefaultOrganizationId.Should().BeNull();

        tenant.AssignDefaultOrganization(Organization, Clock, new FixedGuidFactory(FactId), UserId.SystemActor);

        tenant.DefaultOrganizationId.Should().Be(Organization);
        tenant.Version.Should().Be(1);
        var fact = tenant.DomainEvents.Should().ContainSingle().Subject
            .Should().BeOfType<TenantCreatedDomainEvent>().Subject;
        fact.EventId.Should().Be(FactId);
        fact.OccurredAt.Should().Be(Clock.UtcNow);
        fact.TenantId.Should().Be(Tenant);

        // An exhausted factory proves reassignment does not mint another creation id.
        var replacement = OrganizationId.From(Guid.Parse("0199a000-0000-7000-8000-000000000004"));
        tenant.AssignDefaultOrganization(replacement, Clock, new FixedGuidFactory(), UserId.SystemActor);
        tenant.DefaultOrganizationId.Should().Be(replacement);
        tenant.Version.Should().Be(2);
        tenant.DomainEvents.Should().ContainSingle().Which.Should().BeSameAs(fact);
    }

    [Theory]
    [InlineData("organization")]
    [InlineData("actor")]
    [InlineData("clock")]
    [InlineData("guids")]
    [InlineData("exhausted")]
    public void Refused_assignments_raise_no_fact_and_leave_the_tenant_unchanged(string refusal)
    {
        var tenant = Create();
        var assign = () => tenant.AssignDefaultOrganization(
            refusal == "organization" ? OrganizationId.From(Guid.Empty) : Organization,
            refusal == "clock" ? null! : Clock,
            refusal == "guids" ? null! : new FixedGuidFactory(refusal == "exhausted" ? [] : [FactId]),
            refusal == "actor" ? UserId.From(Guid.Empty) : UserId.SystemActor);

        if (refusal == "exhausted") assign.Should().Throw<InvalidOperationException>();
        else assign.Should().Throw<ArgumentException>();
        tenant.DomainEvents.Should().BeEmpty();
        tenant.DefaultOrganizationId.Should().BeNull();
        tenant.Version.Should().Be(0);
        tenant.UpdatedAt.Should().BeNull();
        tenant.UpdatedBy.Should().BeNull();
    }

    private static TenantAggregate Create() => TenantAggregate.Create(Tenant, "event-probe", "Event probe", Clock, UserId.SystemActor);
}
