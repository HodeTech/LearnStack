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
        CacheKey.ForTenant(Tenant, "tenancy", "settings")
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
        CacheKey.ForTenant(Tenant, "tenancy", "settings")
            .Should().NotBe(CacheKey.ForTenant(OtherTenant, "tenancy", "settings"));
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
    public void An_Unresolved_Tenant_Is_Refused_At_Composition()
    {
        // Guid.Empty is what default(Guid) renders as. Accepting it means two
        // call sites that both failed to resolve their tenant share one bucket —
        // the failure this class exists to prevent, arrived at by a bug rather
        // than by a collision.
        var act = () => CacheKey.ForTenant(Guid.Empty, "tenancy", "settings");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_Unresolved_Organization_Is_Refused_At_Composition()
    {
        var act = () => CacheKey.ForOrganization(Tenant, Guid.Empty, "education", "roster");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_All_Zero_Tenant_Segment_Is_Refused_By_The_Guard_Too()
    {
        // Not only at composition: a hand-rolled key carrying the same segment
        // must not pass either, or the rule holds for one of the two doors.
        var act = () => CacheKey.EnsureValid($"{Guid.Empty}:tenancy:settings");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("018f4d4000007000800000000000000a", "N format")]
    [InlineData("{018f4d40-0000-7000-8000-00000000000a}", "B format")]
    [InlineData("(018f4d40-0000-7000-8000-00000000000a)", "P format")]
    [InlineData("  018f4d40-0000-7000-8000-00000000000a", "leading whitespace")]
    [InlineData("018f4d40-0000-7000-8000-00000000000a  ", "trailing whitespace")]
    [InlineData("018F4D40-0000-7000-8000-00000000000A", "uppercase")]
    public void Only_The_Canonical_Rendering_Of_A_Tenant_Id_Is_Accepted(
        string tenantSegment, string why)
    {
        // Measured: Guid.TryParse accepts N, B, P and X, and tolerates leading
        // and trailing whitespace; TryParseExact with "D" still tolerates the
        // whitespace. None of these collide with a canonical key — the
        // dictionaries compare ordinally, so they land in different slots — and
        // that is exactly the problem: they are a silent miss, and a guard
        // policing the shape our own factories emit should not admit six
        // spellings of one tenant.
        var act = () => CacheKey.EnsureValid($"{tenantSegment}:tenancy:settings");

        act.Should().Throw<ArgumentException>(why);
    }

    [Theory]
    [InlineData("::settings", "empty module, valid tenant")]
    [InlineData(": :settings", "whitespace module, valid tenant")]
    [InlineData(":tenancy:", "empty logical name, valid tenant")]
    public void An_Empty_Segment_Is_Refused_Even_Behind_A_Valid_Tenant(string tail, string why)
    {
        // The corpus's other empty-segment cases all happen to fail on the
        // TENANT segment too, so the empty-segment check was never the deciding
        // factor in any of them — remove it and every one of them still passed.
        // These put a real tenant in front, so only the empty-segment rule can
        // reject them.
        var act = () => CacheKey.EnsureValid($"{Tenant}{tail}");

        act.Should().Throw<ArgumentException>(why);
    }

    [Fact]
    public void A_Two_Segment_Key_Is_Refused_Even_With_The_Platform_Sentinel()
    {
        // Same reasoning one rule over: every other short key in the corpus also
        // fails the tenant check, so the segment-count floor was never proven on
        // its own. "platform:hub" passes the tenant rule and must still fail.
        var act = () => CacheKey.EnsureValid("platform:hub");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_Null_Or_Empty_Key_Is_An_ArgumentException_Not_A_NullReference(string? key)
    {
        // Without the guard, null reaches key.Split and a caller catching the
        // documented exception type sees an unhandled NullReferenceException.
        var act = () => CacheKey.EnsureValid(key!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_Well_Formed_Key_Passes()
    {
        var act = () => CacheKey.EnsureValid(CacheKey.ForTenant(Tenant, "tenancy", "settings"));

        act.Should().NotThrow();
    }

    [Fact]
    public void A_Segment_Containing_The_Separator_Is_Refused()
    {
        // Otherwise ("a", "b:c") and ("a:b", "c") produce one key — the ambiguity
        // a delimiter always has when a component can contain it.
        var act = () => CacheKey.ForTenant(Tenant, "tenancy:nested", "settings");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_Empty_Component_Is_Refused(string? component)
    {
        var act = () => CacheKey.ForTenant(Tenant, "tenancy", component!);

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
