using FluentAssertions;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Identifiers;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Entitlements;

/// <summary>
/// The working default, and the one way it can be wrong without failing.
/// </summary>
public sealed class NullEntitlementProviderTests
{
    private static readonly TenantId Tenant =
        TenantId.From(Guid.Parse("018f4d40-0000-7000-8000-0000000000aa"));

    [Fact]
    public async Task Every_limit_is_unlimited_and_none_is_merely_absent()
    {
        // THE defect this type can ship. Empty dictionaries compile, and the feature half
        // reads identically: an absent feature key resolves to its catalog default, false,
        // and "no plan gating" looks the same. The limit half REVERSES — an absent limit
        // key resolves to its catalog FLOOR, which is positive by construction — so a
        // provider promising "no ceiling" would hand every caller the Starter allowance
        // and nothing would notice.
        var projection = await new NullEntitlementProvider().GetAsync(Tenant);

        projection.Limits.Should().HaveCount(LimitKeys.All.Count);
        projection.Limits.Keys.Should().BeEquivalentTo(
            LimitKeys.All.Keys.Select(key => key.Value));
        projection.Limits.Values.Should().OnlyContain(value => value == LimitKeys.Unlimited);
    }

    [Fact]
    public async Task Every_feature_is_granted_and_none_is_merely_absent()
    {
        var projection = await new NullEntitlementProvider().GetAsync(Tenant);

        projection.Features.Should().HaveCount(FeatureKeys.All.Count);
        projection.Features.Keys.Should().BeEquivalentTo(
            FeatureKeys.All.Keys.Select(key => key.Value));
        projection.Features.Values.Should().OnlyContain(granted => granted);
    }

    [Fact]
    public async Task The_projection_carries_the_tenant_it_was_asked_about()
    {
        var other = TenantId.From(Guid.Parse("018f4d40-0000-7000-8000-0000000000bb"));

        (await new NullEntitlementProvider().GetAsync(Tenant)).TenantId.Should().Be(Tenant);
        (await new NullEntitlementProvider().GetAsync(other)).TenantId.Should().Be(other);
    }

    [Fact]
    public async Task A_plan_that_does_not_exist_cannot_lapse()
    {
        // Null rather than a far-future instant, for the reason the column is nullable: a
        // sentinel date silently becomes an expiry somebody eventually has to explain.
        var projection = await new NullEntitlementProvider().GetAsync(Tenant);

        projection.ExpiresAt.Should().BeNull();
        projection.GraceUntil.Should().BeNull();
        projection.PlanCode.Should().Be("null-provider",
            "the value platform_entitlement_cache's source CHECK admits");
    }

    [Fact]
    public async Task Its_generation_is_below_the_first_the_Hub_sends()
    {
        // The Hub's first projection for a tenant carries generation 1, and the guard
        // admits the equal case. A default of 1 here would put the Hub's first push level
        // with a projection nothing persisted.
        (await new NullEntitlementProvider().GetAsync(Tenant)).Generation.Should().Be(0);
    }

    [Fact]
    public async Task A_push_is_reported_as_ignored_because_nothing_stored_it()
    {
        // Applied would claim durability nothing performed — the same defect class as a
        // unit-of-work joiner reporting Committed for a row nothing committed. The caller
        // is the Hub-facing endpoint and is better told its push went nowhere.
        var projection = await new NullEntitlementProvider().GetAsync(Tenant);

        (await new NullEntitlementProvider().RefreshAsync(projection))
            .Should().Be(EntitlementRefreshOutcome.IgnoredAsStale);
    }

    [Fact]
    public async Task No_projection_can_change_what_the_next_tenant_is_handed()
    {
        // Every map the Null provider hands out is shared by every tenant's projection, so
        // each must refuse a write even through a cast. ComplianceCaps.None was a Dictionary
        // behind IReadOnlyDictionary, and a cap added through IDictionary showed up in the
        // next tenant's projection (measured by the review of Packet 9). The feature and
        // limit maps were frozen already; the case covers all three so the next map added
        // is not the one that slips.
        var provider = new NullEntitlementProvider();
        var first = await provider.GetAsync(TenantId.From(Guid.CreateVersion7()));

        var writes = new Action[]
        {
            () => ((IDictionary<string, ComplianceCap>)first.Compliance.Caps)
                .Add("data_residency", new ComplianceCap(true, true, "eu")),
            () => ((IDictionary<string, bool>)first.Features).Add("injected", true),
            () => ((IDictionary<string, long>)first.Limits).Add("limits.injected", 1),
        };

        foreach (var write in writes)
        {
            write.Should().Throw<NotSupportedException>();
        }

        var next = await provider.GetAsync(TenantId.From(Guid.CreateVersion7()));
        next.Compliance.Caps.Should().BeEmpty();
    }
}
