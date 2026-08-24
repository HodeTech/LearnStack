using FluentAssertions;
using LearnStack.SharedKernel.Caching;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Caching;

/// <summary>
/// The cache-key shape, per
/// <see href="../../../../../docs/standards/20-infrastructure-stack.md">Standards 20
/// § Cache</see>: <c>{tenant_id}:{module}:{logical-name}</c>.
/// </summary>
/// <remarks>
/// There is no query filter and no RLS policy in front of a dictionary. The key is
/// the entire isolation boundary, so every rule about it is a tenant-isolation
/// rule wearing a string's clothes.
/// </remarks>
public sealed class CacheKeyTests
{
    private static readonly Guid Tenant = Guid.Parse("018f4d40-0000-7000-8000-00000000000a");
    private static readonly Guid OtherTenant = Guid.Parse("018f4d40-0000-7000-8000-00000000000b");

    [Fact]
    public void A_Tenant_Key_Carries_All_Three_Segments()
    {
        CacheKey.For(Tenant, "tenancy", "settings")
            .Should().Be($"{Tenant}:tenancy:settings");
    }

    [Fact]
    public void A_Platform_Key_Uses_The_Sentinel_Rather_Than_Omitting_The_Segment()
    {
        // "No tenant" and "every tenant" must look different in a key dump, and
        // the rule stays one rule.
        CacheKey.ForPlatform("hub", "host-map")
            .Should().Be("platform:hub:host-map");
    }

    [Fact]
    public void Two_Tenants_Never_Compute_The_Same_Key()
    {
        CacheKey.For(Tenant, "tenancy", "settings")
            .Should().NotBe(CacheKey.For(OtherTenant, "tenancy", "settings"));
    }

    [Theory]
    [InlineData("tenancy:settings", "two segments — no tenant")]
    [InlineData("settings", "one segment")]
    [InlineData(":tenancy:settings", "empty tenant segment")]
    [InlineData("018f:tenancy:", "empty logical name")]
    [InlineData("018f: :settings", "whitespace module")]
    public void A_Key_Without_Three_Real_Segments_Is_Refused(string key, string why)
    {
        // A key that omits the tenant is a key two tenants can both compute, and
        // the second one reads the first one's value.
        var act = () => CacheKey.EnsureValid(key);

        act.Should().Throw<ArgumentException>(why);
    }

    [Fact]
    public void A_Well_Formed_Key_Passes()
    {
        var act = () => CacheKey.EnsureValid(CacheKey.For(Tenant, "tenancy", "settings"));

        act.Should().NotThrow();
    }

    [Fact]
    public void A_Segment_Containing_The_Separator_Is_Refused()
    {
        // Otherwise ("a", "b:c") and ("a:b", "c") produce one key — the ambiguity
        // a delimiter always has when a component can contain it.
        var act = () => CacheKey.For(Tenant, "tenancy:nested", "settings");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_Empty_Component_Is_Refused(string? component)
    {
        var act = () => CacheKey.For(Tenant, "tenancy", component!);

        act.Should().Throw<ArgumentException>();
    }
}
