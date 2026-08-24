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
    private static readonly Guid Org = Guid.Parse("018f4d40-0000-7000-8000-0000000000c1");
    private static readonly Guid OtherOrg = Guid.Parse("018f4d40-0000-7000-8000-0000000000c2");

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
    public void Two_Organizations_Of_One_Tenant_Never_Compute_The_Same_Key()
    {
        // The tenant guard cannot catch this one — an organization-scoped value
        // and a tenant-wide one are indistinguishable as strings — so the
        // composition is what prevents it.
        CacheKey.ForOrganization(Tenant, Org, "education", "roster")
            .Should().NotBe(CacheKey.ForOrganization(Tenant, OtherOrg, "education", "roster"))
            .And.Be($"{Tenant}:{Org}:education:roster");
    }

    [Fact]
    public void An_Organization_Key_Is_Well_Formed()
    {
        var act = () => CacheKey.EnsureValid(
            CacheKey.ForOrganization(Tenant, Org, "education", "roster"));

        act.Should().NotThrow();
    }

    [Fact]
    public void An_Organization_Key_Still_Leads_With_The_Tenant()
    {
        // Not the organization: the tenant is the outer boundary, so it is the
        // segment a key dump must sort by.
        CacheKey.ForOrganization(Tenant, Org, "education", "roster")
            .Should().StartWith($"{Tenant}:");
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

    [Theory]
    [InlineData("hub:entitlement:018f4d40-0000-7000-8000-00000000000a")]
    [InlineData("tenant:settings:cache")]
    [InlineData("education:course:018f4d40-0000-7000-8000-00000000000a")]
    public void Three_Segments_Are_Not_Enough_If_The_First_One_Is_Not_A_Tenant(string key)
    {
        // These are the shapes Standards 20's own cheat sheet used to carry, and
        // the reason counting segments was never the rule: every one of them has
        // three non-empty segments, puts the MODULE first, and is therefore a key
        // two tenants can both compute. The first version of the guard admitted
        // all three while its error message said the tenant segment is mandatory
        // — a guard that passes the shape it exists to reject is worse than no
        // guard, because it makes the rule look enforced.
        var act = () => CacheKey.EnsureValid(key);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_Platform_Sentinel_Is_A_Tenant_Segment()
    {
        var act = () => CacheKey.EnsureValid("platform:hub:host-map");

        act.Should().NotThrow("'every tenant' is spelled, not omitted");
    }

    [Fact]
    public void A_Key_May_Carry_More_Than_Three_Segments()
    {
        // The logical name is the caller's to structure — "settings:theme:dark"
        // is one name with internal structure, not a violation. What is fixed is
        // that the FIRST segment identifies the tenant.
        var act = () => CacheKey.EnsureValid($"{Tenant}:tenancy:settings:theme");

        act.Should().NotThrow();
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_Empty_Component_Of_An_Organization_Key_Is_Refused(string? component)
    {
        var act = () => CacheKey.ForOrganization(Tenant, Org, "education", component!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_Separator_Inside_An_Organization_Key_Segment_Is_Refused()
    {
        var act = () => CacheKey.ForOrganization(Tenant, Org, "education:nested", "roster");

        act.Should().Throw<ArgumentException>();
    }
}
