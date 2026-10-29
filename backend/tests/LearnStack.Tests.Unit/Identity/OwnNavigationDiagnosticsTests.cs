using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.SharedKernel.Identifiers;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

public sealed class OwnNavigationDiagnosticsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Own_navigation_diagnostic_formatting_excludes_relationship_values(bool tenancyContract)
    {
        var tenant = TenantId.From(Guid.Parse("01930000-0000-7000-8000-00000000a001"));
        var organization = OrganizationId.From(Guid.Parse("01930000-0000-7000-8000-00000000a002"));
        const string TenantName = "neutral-own-tenant-canary";
        const string OrganizationName = "neutral-own-branch-canary";
        const string EntryHost = "neutral-own-entry.example";
        object value = tenancyContract
            ? new OwnTenantNavigationItem(tenant, organization, TenantName, OrganizationName, EntryHost)
            : new OwnMembershipContextDto(tenant, organization, TenantName, OrganizationName, EntryHost);

        // Ordinary interpolation must not depend on a logger's redaction policy.
        var diagnostic = $"Own navigation: {value}";
        foreach (var personalValue in new[] { tenant.Value.ToString("D"), organization.Value.ToString("D"), TenantName, OrganizationName, EntryHost })
            Assert.DoesNotContain(personalValue, diagnostic, StringComparison.Ordinal);
    }
}
