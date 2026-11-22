using FluentAssertions;
using LearnStack.Infrastructure.DataProtection;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.DataProtection;

/// <summary>
/// ADR-0107 § 6: which columns <c>attribution_reference</c> reads, derived from the model; the database proof of the
/// row unit is <c>DataExportParticipantTests</c>.
/// </summary>
public sealed class AttributionReferenceCounterTests
{
    [Fact]
    public void Every_UserId_column_of_a_tenant_owned_entity_is_attribution_unless_named_a_party()
    {
        using var identity = Identity();
        var plan = AttributionReferenceCounter.Plan(identity.Model, new HashSet<Type> { typeof(DataSubjectRequest) },
            [(typeof(Membership), nameof(Membership.UserId)), (typeof(Invitation), nameof(Invitation.InviterUserId)),
                (typeof(Invitation), nameof(Invitation.AcceptedBy))]).ToDictionary(entry => entry.Entity, entry => entry.Properties);

        plan[typeof(Membership)].Should().Equal("CreatedBy", "DeletedBy", "UpdatedBy");
        plan[typeof(Invitation)].Should().Equal("CreatedBy", "DeletedBy", "UpdatedBy");
        plan[typeof(DataExportTicket)].Should().Equal(["CreatedBy"], "a non-AuditableEntity CreatedBy is attribution too");
        plan[typeof(FederationConnection)].Should().Contain(["PendingActor", "TestActor"], "a party not named is counted");
        plan.Should().NotContainKey(typeof(DataSubjectRequest), "an excluded type is reported under its own kind");
        plan.Keys.Should().OnlyContain(type => typeof(LearnStack.SharedKernel.Persistence.ITenantOwned).IsAssignableFrom(type),
            "global rows are not a tenant group's");

        using var tenancy = new TenancyDbContext(new DbContextOptionsBuilder<TenancyDbContext>().UseNpgsql(ModelOnly).Options,
            StaticTenantContextAccessor.Unresolved);
        var settings = AttributionReferenceCounter.Plan(tenancy.Model, new HashSet<Type> { typeof(MembershipFieldConsent) }, null)
            .ToDictionary(entry => entry.Entity, entry => entry.Properties);
        settings[typeof(TenantFeatureFlag)].Should().Equal(["UpdatedBy"], "an updater outside the AuditableEntity set is attribution");
        settings.Should().NotContainKey(typeof(MembershipFieldConsent));
    }

    [Theory]
    [InlineData("Membership", "Missing")]
    [InlineData("Membership", "TenantId")]
    [InlineData("DataSubjectRequest", "SubjectUserIdTypo")]
    public void A_party_that_is_not_a_mapped_UserId_column_is_a_programming_error(string entity, string property)
    {
        using var identity = Identity();
        var type = entity == "Membership" ? typeof(Membership) : typeof(DataSubjectRequest);

        FluentActions.Invoking(() => AttributionReferenceCounter.Plan(identity.Model, null, [(type, property)]))
            .Should().Throw<InvalidOperationException>("an exclusion cannot silently go stale");
    }

    private const string ModelOnly = "Host=model-only;Database=model-only;Username=model-only";

    private static IdentityDbContext Identity() => new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(ModelOnly).Options,
        StaticTenantContextAccessor.Unresolved, EmptyIdentityScope.Instance);
}
