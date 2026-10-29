using FluentAssertions;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Time;
using Xunit;

namespace LearnStack.Tests.Unit.Modules.Tenancy;

public sealed class OrganizationAdministrationDomainTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangeStatus_Archive_RetainsOrganizationIdentityAndHistoryAndIsTerminal(bool fromSuspended)
    {
        var tenantId = TenantId.From(Guid.Parse("11111111-1111-7111-8111-111111111111"));
        var organizationId = OrganizationId.From(Guid.Parse("22222222-2222-7222-8222-222222222222"));
        var creator = UserId.From(Guid.Parse("aaaaaaaa-1111-7111-8111-111111111111"));
        var editor = UserId.From(Guid.Parse("bbbbbbbb-1111-7111-8111-111111111111"));
        var created = new FixedClock(new DateTimeOffset(2026, 10, 28, 8, 0, 0, TimeSpan.Zero));
        var archived = new FixedClock(created.UtcNow.AddDays(1));
        var organization = Organization.Create(organizationId, tenantId, "branch", "Branch", created, creator);
        if (fromSuspended) organization.ChangeStatus(OrganizationStatus.Suspended, created, creator);
        var version = organization.Version;

        organization.ChangeStatus(OrganizationStatus.Archived, archived, editor);

        (organization.Id, organization.TenantId, organization.Slug, organization.DisplayName)
            .Should().Be((organizationId, tenantId, "branch", "Branch"));
        organization.Status.Should().Be(OrganizationStatus.Archived);
        organization.IsDeleted.Should().BeFalse();
        organization.CreatedAt.Should().Be(created.UtcNow);
        organization.CreatedBy.Should().Be(creator);
        organization.UpdatedAt.Should().Be(archived.UtcNow);
        organization.UpdatedBy.Should().Be(editor);
        organization.Version.Should().Be(version + 1);

        foreach (var target in new[] { OrganizationStatus.Active, OrganizationStatus.Suspended })
        {
            var reactivate = () => organization.ChangeStatus(target, archived, creator);
            reactivate.Should().Throw<InvalidOperationException>();
            organization.Status.Should().Be(OrganizationStatus.Archived);
            organization.Version.Should().Be(version + 1);
            organization.UpdatedBy.Should().Be(editor);
        }
    }

    private sealed record FixedClock(DateTimeOffset UtcNow) : IClock;
}
