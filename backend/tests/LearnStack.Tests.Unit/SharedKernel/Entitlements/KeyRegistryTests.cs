using FluentAssertions;
using LearnStack.SharedKernel.Entitlements;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Entitlements;

/// <summary>
/// The three registries, and the properties that make them safe to ship before a consumer.
/// </summary>
/// <remarks>
/// These keys land in the Hub's plan validators, in persisted <c>jsonb</c> and in a wire
/// schema pinned in both repositories, so the SPELLINGS are a one-way door. Nothing here
/// paraphrases them: every assertion names the string, because a test that read the
/// constant back would agree with whatever the constant became.
/// </remarks>
public sealed class KeyRegistryTests
{
    [Fact]
    public void Every_feature_key_carries_a_descriptor_and_every_descriptor_a_declared_key()
    {
        // A key with no descriptor resolves to nothing the resolver can read; a descriptor
        // keyed on a string no field declares is unreachable. Both directions, because
        // each hides the other.
        var declared = typeof(FeatureKeys).GetFields()
            .Where(field => field.FieldType == typeof(FeatureKey))
            .Select(field => (FeatureKey)field.GetValue(null)!)
            .ToList();

        declared.Should().HaveCount(16);
        FeatureKeys.All.Keys.Should().BeEquivalentTo(declared);
        FeatureKeys.All.Should().OnlyContain(entry => entry.Key == entry.Value.Key);
    }

    [Fact]
    public void Every_limit_key_carries_a_descriptor_and_every_descriptor_a_declared_key()
    {
        var declared = typeof(LimitKeys).GetFields()
            .Where(field => field.FieldType == typeof(LimitKey))
            .Select(field => (LimitKey)field.GetValue(null)!)
            .ToList();

        declared.Should().HaveCount(9);
        LimitKeys.All.Keys.Should().BeEquivalentTo(declared);
        LimitKeys.All.Should().OnlyContain(entry => entry.Key == entry.Value.Key);
    }

    [Fact]
    public void The_limit_spellings_are_the_ones_the_Hub_sends()
    {
        // Verbatim, and asserted as literals. The Hub's plan validators reject a plan
        // whose limits are not from this set, so a key spelled LearnStack's way misses on
        // every real projection and falls through to its floor — a paid tenant reading as
        // unentitled, reported as success (ADR-0045 Amendment 1 § 1).
        LimitKeys.All.Keys.Select(key => key.Value).Should().BeEquivalentTo(
            "limits.max_users",
            "limits.max_organizations",
            "limits.classroom_minutes_per_month",
            "limits.recording_storage_gb",
            "limits.media_storage_gb",
            "limits.media_bandwidth_gb_per_month",
            "limits.api_rate_per_minute",
            "limits.max_custom_content_types",
            "limits.max_page_block_definitions");
    }

    [Fact]
    public void The_feature_spellings_are_the_ones_the_Hub_sends_plus_the_two_tenant_flags()
    {
        FeatureKeys.All.Keys.Select(key => key.Value).Should().BeEquivalentTo(
            "classroom.recording",
            "classroom.breakout_rooms",
            "tenancy.custom_domain",
            "tenancy.white_label_branding",
            "customization.unlimited_content_types",
            "identity.sso.saml",
            "identity.sso.oidc",
            "identity.scim",
            "analytics.advanced_reporting",
            "admin.bulk_import",
            "integrations.api_access",
            "integrations.webhooks",
            "audit.export",
            "compliance.data_residency",
            // The only two the Hub does not carry, and the only two that are not
            // plan-projected.
            "learning.lesson_player.v2",
            "ai.pronunciation_feedback");
    }

    [Fact]
    public void No_limit_floor_is_unlimited_or_denied()
    {
        // "Never -1, never 0" — architecture/26 § the degraded read. Unlimited is a gift
        // and zero is an outage: a floor of -1 hands an unentitled tenant an unbounded
        // allowance, and a floor of 0 on limits.api_rate_per_minute denies every API call
        // the moment a projection is late. The Hub's own Starter row carries three zeros,
        // which is exactly why these are not copied from it.
        LimitKeys.All.Values.Should().OnlyContain(
            descriptor => descriptor.Default > 0,
            "a floor that is unlimited or denied is not a floor");
    }

    [Theory]
    [InlineData("limits.max_users", 25)]
    [InlineData("limits.max_organizations", 1)]
    [InlineData("limits.media_storage_gb", 5)]
    [InlineData("limits.media_bandwidth_gb_per_month", 50)]
    [InlineData("limits.max_custom_content_types", 5)]
    [InlineData("limits.max_page_block_definitions", 5)]
    public void The_six_non_zero_Starter_values_are_the_Hubs_verbatim(string key, long expected)
    {
        // These six are transcribed, so drift between the two repositories shows up here
        // rather than as a tenant working at a ceiling nobody chose.
        LimitKeys.All[new LimitKey(key)].Default.Should().Be(expected);
    }

    [Theory]
    [InlineData("limits.classroom_minutes_per_month", 60)]
    [InlineData("limits.recording_storage_gb", 1)]
    [InlineData("limits.api_rate_per_minute", 60)]
    public void The_three_Starter_zeros_are_replaced_with_a_working_floor(string key, long expected)
    {
        // LearnStack's own numbers, not the Hub's, and the delivery record says so. The
        // Hub ships 0 for all three; a floor of 0 is the outage "never 0" forbids.
        LimitKeys.All[new LimitKey(key)].Default.Should().Be(expected);
    }

    [Fact]
    public void Only_recording_is_gated_by_a_killswitch_and_it_names_it()
    {
        // The correspondence is DECLARED, never derived from the string. Inferring
        // `killswitch.classroom.recording` from `classroom.recording` by prefix would make
        // a renamed key silently ungated: the rename compiles, the overlay looks for a
        // switch nobody declared, and the expensive path stays on through the incident the
        // switch exists for (ADR-0045 Amendment 1 § 5).
        var gated = FeatureKeys.All.Values
            .Where(descriptor => descriptor.Killswitch is not null)
            .ToList();

        gated.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                Key = FeatureKeys.ClassroomRecording,
                Killswitch = (KillswitchKey?)KillswitchKeys.RecordingEnabled,
            });
    }

    [Fact]
    public void Every_named_killswitch_is_one_the_registry_declares()
    {
        // A descriptor naming a switch the registry does not carry is a feature gated by
        // nothing, which reads exactly like a feature gated by something.
        FeatureKeys.All.Values
            .Where(descriptor => descriptor.Killswitch is not null)
            .Select(descriptor => descriptor.Killswitch!.Value)
            .Should().OnlyContain(key => KillswitchKeys.All.ContainsKey(key));
    }

    [Fact]
    public void Every_killswitch_defaults_to_enabled()
    {
        // A killswitch's default is the state nobody has flipped it out of, and it is also
        // what an unreadable overlay answers. A default of false would mean a cache outage
        // disables every gated path platform-wide.
        KillswitchKeys.All.Values.Should().OnlyContain(enabled => enabled);
        KillswitchKeys.All.Should().HaveCount(3);
    }

    [Fact]
    public void No_plan_feature_defaults_to_granted()
    {
        // An unlisted capability is one the plan does not grant. A default of true would
        // hand every capability to every tenant until a projection arrived to take it
        // away — and to every tenant permanently if one never did.
        FeatureKeys.All.Values.Should().OnlyContain(descriptor => !descriptor.Default);
    }

    [Fact]
    public void Every_security_surface_fails_closed()
    {
        // ADR-0034 requires each feature key class to declare a posture explicitly, and
        // architecture/26 puts these on fail-closed: an unknown answer must not open an
        // export or an API surface. Asserted by key rather than by counting, because the
        // membership of this set is the security decision.
        var mustFailClosed = new[]
        {
            FeatureKeys.SsoSaml, FeatureKeys.SsoOidc, FeatureKeys.Scim,
            FeatureKeys.AuditExport, FeatureKeys.ApiAccess, FeatureKeys.Webhooks,
            FeatureKeys.BulkImport, FeatureKeys.DataResidencySelection,
        };

        foreach (var key in mustFailClosed)
        {
            FeatureKeys.All[key].Degraded.Should().Be(DegradedPosture.FailClosed,
                $"{key} is a security or compliance surface");
        }
    }

    [Fact]
    public void A_tenant_flag_is_never_plan_projected_and_a_plan_feature_never_a_tenant_flag()
    {
        // The two halves are resolved from different tables. A plan-projected key served
        // from tenant_feature_flags is a tenant that granted itself an entitlement, which
        // is what PlanProjected_Keys_NotInTenantFlags exists to catch one layer down.
        FeatureKeys.All[FeatureKeys.LessonPlayerV2].Source
            .Should().Be(FeatureSource.TenantFlag);
        FeatureKeys.All[FeatureKeys.AiPronunciationFeedback].Source
            .Should().Be(FeatureSource.TenantFlag);

        FeatureKeys.All.Values.Count(descriptor => descriptor.Source == FeatureSource.TenantFlag)
            .Should().Be(2, "every other key is the Hub's to project");
    }
}
