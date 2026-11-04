using FluentAssertions;
using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.Modules.Tenancy.Application.Settings;
using Xunit;

namespace LearnStack.Tests.Unit.Tenancy;

public sealed class InvitationLifetimePolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    [InlineData(30)]
    public void Supported_days_round_trip_through_the_fixed_tenant_only_registration(int days)
    {
        var registration = TenantSettingRegistry.Default.Find(TenantSettingKeys.InvitationLifetime)!;
        registration.AllowsOrganizationScope.Should().BeFalse();
        registration.Key.Should().Be("identity.invitation.ttl_days");
        var canonical = InvitationLifetimePolicyRegistry.Canonicalize(days);
        canonical.IsSuccess.Should().BeTrue();
        registration.Parse(canonical.Value!).Value!.Days.Should().Be(days);
        TenantSettingRegistry.Default.Find(new TenantSettingKey<BrandingTheme>(registration.Key)).Should().BeNull();
    }

    [Theory]
    [InlineData("{\"days\":0}")]
    [InlineData("{\"days\":31}")]
    [InlineData("{\"days\":1.5}")]
    [InlineData("{\"days\":\"14\"}")]
    [InlineData("{\"days\":14,\"days\":14}")]
    [InlineData("{\"days\":14,\"other\":1}")]
    [InlineData("{\"Days\":14}")]
    [InlineData("14")]
    [InlineData("null")]
    [InlineData("{")]
    public void Invalid_or_ambiguous_settings_refuse_instead_of_silently_defaulting(string value) =>
        InvitationLifetimePolicyRegistry.Read(value).IsFailure.Should().BeTrue();
}
