using FluentAssertions;
using LearnStack.Modules.Tenancy.Domain;
using TenancyDomain = LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Time;
using Xunit;

namespace LearnStack.Tests.Unit.Modules.Tenancy;

/// <summary>
/// The Tenancy aggregates' factories and the invariants they refuse to let a
/// caller past.
/// </summary>
/// <remarks>
/// The schema carries most of these as constraints, and that is the second layer
/// rather than the first: a caller that reaches the database has already built an
/// object the domain says cannot exist, and gets a
/// <c>PostgresException</c> three layers from the mistake instead of an
/// <c>ArgumentException</c> at it.
/// </remarks>
public sealed class TenancyAggregateTests
{
    private static readonly FixedClock Clock = new(
        new DateTimeOffset(2026, 8, 28, 9, 0, 0, TimeSpan.Zero));

    private static readonly UserId Actor =
        UserId.From(Guid.Parse("00000000-0000-7000-8000-000000000001"));

    private static readonly TenantId Tenant =
        TenantId.From(Guid.Parse("11111111-1111-7111-8111-111111111111"));

    private static readonly TenantDomainId DomainId =
        TenantDomainId.From(Guid.Parse("dddddddd-1111-7111-8111-111111111111"));

    [Fact]
    public void A_subdomain_is_verified_by_construction()
    {
        var domain = TenantDomain.CreateSubdomain(
            DomainId, Tenant, "alpha.example.com", Clock, Actor);

        domain.Kind.Should().Be(TenantDomainKind.Subdomain);
        domain.Status.Should().Be(TenantDomainStatus.Verified);
        domain.VerifiedAt.Should().Be(Clock.UtcNow);
    }

    [Fact]
    public void A_custom_domain_starts_unverified()
    {
        var domain = TenantDomain.RequestCustomDomain(
            DomainId, Tenant, "learn.acme.com", Clock, Actor);

        domain.Kind.Should().Be(TenantDomainKind.Custom);
        domain.Status.Should().Be(TenantDomainStatus.Requested);
        domain.VerifiedAt.Should().BeNull();
    }

    [Fact]
    public void A_subdomain_has_no_verification_lifecycle()
    {
        // The platform controls the zone, so a Subdomain is Verified by
        // construction and every state diagram in the corpus draws it that way.
        // Nothing in the schema says so — ck_tenant_domains_kind and
        // ck_tenant_domains_status are independent single-column CHECKs — so the
        // aggregate is where the invariant lives.
        var domain = TenantDomain.CreateSubdomain(
            DomainId, Tenant, "alpha.example.com", Clock, Actor);

        var verify = () => domain.MarkVerified(Clock, Actor);
        var fail = () => domain.MarkVerificationFailed("dns", Clock, Actor);

        verify.Should().Throw<InvalidOperationException>().WithMessage("*verified by construction*");
        fail.Should().Throw<InvalidOperationException>().WithMessage("*verified by construction*");
        domain.Status.Should().Be(TenantDomainStatus.Verified);
    }

    [Fact]
    public void A_custom_domain_records_its_verification_attempts()
    {
        var domain = TenantDomain.RequestCustomDomain(
            DomainId, Tenant, "learn.acme.com", Clock, Actor);

        domain.MarkVerificationStarted(Clock, Actor);
        domain.Status.Should().Be(TenantDomainStatus.Verifying);

        domain.MarkVerificationFailed("no TXT record", Clock, Actor);

        domain.Status.Should().Be(TenantDomainStatus.Failed);
        domain.VerificationAttempts.Should().Be(1);
        domain.LastVerificationError.Should().Be("no TXT record");

        // Failed may start over, which is the edge the module spec's diagram
        // draws back into Verifying.
        domain.MarkVerificationStarted(Clock, Actor);
        domain.MarkVerified(Clock, Actor);

        domain.Status.Should().Be(TenantDomainStatus.Verified);
        domain.VerificationAttempts.Should().Be(2);
        domain.LastVerificationError.Should().BeNull("a success clears the previous failure");
        domain.Version.Should().Be(4, "each transition is an audited, versioned mutation");
    }

    [Fact]
    public void A_verification_result_needs_a_verification_in_progress()
    {
        // Requested → Verified in one call is a transition no diagram in the
        // corpus draws, and the status CHECK cannot see where a row came from.
        var domain = TenantDomain.RequestCustomDomain(
            DomainId, Tenant, "learn.acme.com", Clock, Actor);

        var verify = () => domain.MarkVerified(Clock, Actor);
        var fail = () => domain.MarkVerificationFailed("no TXT record", Clock, Actor);

        verify.Should().Throw<InvalidOperationException>().WithMessage("*Requested*Verifying*");
        fail.Should().Throw<InvalidOperationException>().WithMessage("*Requested*Verifying*");
        domain.Status.Should().Be(TenantDomainStatus.Requested);
    }

    [Fact]
    public void A_verified_domain_does_not_start_verifying_again()
    {
        var domain = TenantDomain.RequestCustomDomain(
            DomainId, Tenant, "learn.acme.com", Clock, Actor);
        domain.MarkVerificationStarted(Clock, Actor);
        domain.MarkVerified(Clock, Actor);

        var restart = () => domain.MarkVerificationStarted(Clock, Actor);

        restart.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    // The whole rule, not the case half of it. An earlier guard tested only
    // `host.Any(char.IsUpper)` while its message named four properties, so a host
    // carrying a port or an IDN label passed the aggregate and failed at
    // ck_tenant_domains_host_normalized three layers down.
    [InlineData("Alpha.Example.com")]      // not lowercase
    [InlineData("alpha.example.com:443")]  // carries a port
    [InlineData("alpha.example.com.")]     // trailing dot
    [InlineData("täst.example.com")]       // not punycoded
    [InlineData("-bad.example.com")]       // not a host at all
    [InlineData("a..b.example.com")]       // empty label
    public void A_host_that_is_not_already_normalized_is_refused(string host)
    {
        var create = () => TenantDomain.RequestCustomDomain(DomainId, Tenant, host, Clock, Actor);

        create.Should().Throw<ArgumentException>().WithMessage("*normalized*");
    }

    [Theory]
    [InlineData("alpha.example.com")]
    [InlineData("xn--tst-qla.example.com")]
    public void An_already_normalized_host_is_accepted(string host)
    {
        var create = () => TenantDomain.RequestCustomDomain(DomainId, Tenant, host, Clock, Actor);

        create.Should().NotThrow();
    }

    [Fact]
    public void Every_factory_refuses_an_unassigned_identifier()
    {
        // The two direct misuse paths — `default` and `new` — are compile-time
        // errors under Vogen (VOG009 / VOG010), so this guard exists for the third:
        // an id that travelled through a `default(T)`-shaped generic or a
        // deserializer. Tenant.Create already had it; the other three did not, and
        // an inconsistent guard reads as a deliberate exemption.
        var organization = () => Organization.Create(
            Unassigned<OrganizationId>(), Tenant, "main", "Main", Clock, Actor);
        var domain = () => TenantDomain.RequestCustomDomain(
            Unassigned<TenantDomainId>(), Tenant, "learn.acme.com", Clock, Actor);
        var setting = () => TenantSetting.Create(
            Unassigned<TenantSettingId>(), Tenant, null, "tz", "\"Europe/Istanbul\"", Clock, Actor);

        organization.Should().Throw<ArgumentException>().WithMessage("*never assigned*");
        domain.Should().Throw<ArgumentException>().WithMessage("*never assigned*");
        setting.Should().Throw<ArgumentException>().WithMessage("*never assigned*");
    }

    [Fact]
    public void Every_factory_refuses_an_unassigned_tenant()
    {
        var organization = () => Organization.Create(
            OrganizationId.From(Guid.Parse("aaaaaaaa-1111-7111-8111-111111111111")),
            Unassigned<TenantId>(), "main", "Main", Clock, Actor);
        var domain = () => TenantDomain.RequestCustomDomain(
            DomainId, Unassigned<TenantId>(), "learn.acme.com", Clock, Actor);

        organization.Should().Throw<ArgumentException>();
        domain.Should().Throw<ArgumentException>();
    }

    [Theory]
    // The column is jsonb, so PostgreSQL rejects malformed JSON with 22P02 three
    // layers from the call that produced it, naming neither the property nor the
    // aggregate. Parsing at the factory turns that into an ArgumentException at
    // the call site — the same reason TenantDomain runs its host through
    // EffectiveHost.Normalize rather than waiting for the CHECK.
    [InlineData("\"Europe/Istanbul\"", true)]
    [InlineData("{\"a\":1}", true)]
    [InlineData("true", true)]
    [InlineData("Europe/Istanbul", false)]
    [InlineData("{\"a\":}", false)]
    [InlineData("{", false)]
    public void A_setting_value_must_be_well_formed_json(string value, bool accepted)
    {
        var settingId = TenantSettingId.From(Guid.Parse("55555555-1111-7111-8111-111111111111"));

        var create = () => TenantSetting.Create(
            settingId, Tenant, null, "tz", value, Clock, Actor);

        if (accepted)
        {
            create.Should().NotThrow();
        }
        else
        {
            create.Should().Throw<ArgumentException>().WithMessage("*jsonb*");
        }
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("yes", false)]
    public void A_feature_flag_value_must_be_well_formed_json(string value, bool accepted)
    {
        var create = () => TenantFeatureFlag.Create(Tenant, "live-classroom", value, Clock.UtcNow, Actor);

        if (accepted)
        {
            create.Should().NotThrow();
        }
        else
        {
            create.Should().Throw<ArgumentException>().WithMessage("*jsonb*");
        }
    }

    [Theory]
    // The two bounds OrganizationConfiguration maps. The database rejects a longer
    // value with 22001 and no property name.
    [InlineData(63, 200, true)]
    [InlineData(64, 200, false)]
    [InlineData(63, 201, false)]
    public void An_organization_is_bounded_by_the_lengths_its_columns_hold(
        int slugLength, int displayNameLength, bool accepted)
    {
        var create = () => Organization.Create(
            OrganizationId.From(Guid.Parse("aaaaaaaa-1111-7111-8111-111111111111")),
            Tenant,
            new string('s', slugLength),
            new string('d', displayNameLength),
            Clock,
            Actor);

        if (accepted)
        {
            create.Should().NotThrow();
        }
        else
        {
            create.Should().Throw<ArgumentException>();
        }
    }

    [Theory]
    // The module spec's state diagram, as a table. A bare assignment took every
    // pair, including Archived → Active and (TenantStatus)999; ck_tenants_status
    // stops only the third, because a CHECK sees the value and not where the row
    // came from.
    [InlineData(TenantStatus.Active, true)]
    [InlineData(TenantStatus.Suspended, true)]
    [InlineData(TenantStatus.Archived, true)]
    public void A_trial_tenant_moves_where_the_diagram_draws(TenantStatus target, bool allowed)
    {
        var tenant = NewTenant();

        var change = () => tenant.ChangeStatus(target, Clock, Actor);

        if (allowed)
        {
            change.Should().NotThrow();
            tenant.Status.Should().Be(target);
        }
    }

    [Fact]
    public void An_archived_tenant_is_terminal()
    {
        var tenant = NewTenant();
        tenant.ChangeStatus(TenantStatus.Archived, Clock, Actor);

        var revive = () => tenant.ChangeStatus(TenantStatus.Active, Clock, Actor);

        revive.Should().Throw<InvalidOperationException>()
            .WithMessage("*Archived*Active*");
    }

    [Fact]
    public void An_active_tenant_does_not_go_back_to_trial()
    {
        var tenant = NewTenant();
        tenant.ChangeStatus(TenantStatus.Active, Clock, Actor);

        var back = () => tenant.ChangeStatus(TenantStatus.Trial, Clock, Actor);

        back.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_status_the_enum_does_not_define_is_refused()
    {
        var tenant = NewTenant();

        var change = () => tenant.ChangeStatus((TenantStatus)999, Clock, Actor);

        change.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void An_archived_organization_is_terminal()
    {
        var organization = Organization.Create(
            OrganizationId.From(Guid.Parse("22222222-2222-7222-8222-222222222222")),
            Tenant, "branch", "Branch", Clock, Actor);
        organization.ChangeStatus(OrganizationStatus.Archived, Clock, Actor);

        var revive = () => organization.ChangeStatus(OrganizationStatus.Active, Clock, Actor);

        revive.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    // Tenant.Slug's own documentation says "URL-safe handle. Appears in
    // hostnames" and Organization's says 63 "which is a DNS label"; neither
    // factory looked at the characters, and neither column carries a CHECK.
    [InlineData("Acme")]
    [InlineData("acme_school")]
    [InlineData("acme school")]
    [InlineData("acme/school")]
    [InlineData("-acme")]
    [InlineData("acme-")]
    [InlineData("acme--school")]
    public void A_slug_that_is_not_url_safe_is_refused(string slug)
    {
        var tenant = () => TenancyDomain.Tenant.Create(Tenant, slug, "Acme", Clock, Actor);
        var organization = () => Organization.Create(
            OrganizationId.From(Guid.Parse("22222222-2222-7222-8222-222222222222")),
            Tenant, slug, "Acme", Clock, Actor);

        tenant.Should().Throw<ArgumentException>().WithMessage("*URL-safe*");
        organization.Should().Throw<ArgumentException>().WithMessage("*URL-safe*");
    }

    [Fact]
    public void A_nil_uuid_is_not_a_tenant()
    {
        // TenantId.From(Guid.Empty) reports IsInitialized() == true, so the
        // uninitialized guard passed it straight through and a nil-uuid tenant
        // inserted — satisfying its own policy for any session whose
        // app.tenant_id held the same nil. No ADR reserves the value; Packet 9
        // chooses the platform sentinel.
        var create = () => TenancyDomain.Tenant.Create(
            TenantId.From(Guid.Empty), "acme", "Acme", Clock, Actor);

        create.Should().Throw<ArgumentException>();
    }

    [Theory]
    // The one type in the module carrying audit columns without deriving from
    // AuditableEntity, and so the one that skipped its guard. The accepted actor
    // then threw ValueObjectValidationException out of the Vogen EF converter at
    // persist time — three layers from this call.
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_feature_flag_refuses_the_audit_sentinels(bool sentinelClock, bool emptyActor)
    {
        var at = sentinelClock ? default : Clock.UtcNow;
        var by = emptyActor ? UserId.From(Guid.Empty) : Actor;

        var create = () => TenantFeatureFlag.Create(Tenant, "beta", "true", at, by);

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Setting_a_feature_flag_refuses_them_too()
    {
        var flag = TenantFeatureFlag.Create(Tenant, "beta", "true", Clock.UtcNow, Actor);

        var set = () => flag.SetValue("false", default, Actor);

        set.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_verification_error_is_bounded_by_the_length_its_column_holds()
    {
        var domain = TenantDomain.RequestCustomDomain(
            DomainId, Tenant, "learn.acme.com", Clock, Actor);
        domain.MarkVerificationStarted(Clock, Actor);

        var fail = () => domain.MarkVerificationFailed(new string('e', 1001), Clock, Actor);

        fail.Should().Throw<ArgumentException>().WithMessage("*the column holds 1000*");
    }

    [Fact]
    public void A_refused_audit_stamp_leaves_the_aggregate_untouched()
    {
        // MarkUpdated is the only statement in these mutator bodies that can
        // throw. With the assignment first, a call that failed its audit
        // validation still moved the aggregate — an inconsistent object no guard
        // above it can see, and one EF would happily persist if a handler caught
        // the ArgumentException and carried on.
        var tenant = NewTenant();
        var organization = NewOrganization();
        var setting = TenantSetting.Create(
            TenantSettingId.From(Guid.Parse("55555555-2222-7222-8222-222222222222")),
            Tenant, null, "k", "true", Clock, Actor);
        var domain = TenantDomain.RequestCustomDomain(
            DomainId, Tenant, "learn.acme.com", Clock, Actor);

        var unreal = UserId.From(Guid.Empty);

        ((Action)(() => tenant.ChangeStatus(TenantStatus.Active, Clock, unreal)))
            .Should().Throw<ArgumentException>();
        ((Action)(() => organization.ChangeStatus(OrganizationStatus.Suspended, Clock, unreal)))
            .Should().Throw<ArgumentException>();
        ((Action)(() => organization.Rename("Renamed", Clock, unreal)))
            .Should().Throw<ArgumentException>();
        ((Action)(() => setting.SetValue("false", Clock, unreal)))
            .Should().Throw<ArgumentException>();
        ((Action)(() => domain.MarkVerificationStarted(Clock, unreal)))
            .Should().Throw<ArgumentException>();

        tenant.Status.Should().Be(TenantStatus.Trial, "the refused call moved nothing");
        organization.Status.Should().Be(OrganizationStatus.Active);
        organization.DisplayName.Should().Be("Acme");
        setting.Value.Should().Be("true");
        domain.Status.Should().Be(TenantDomainStatus.Requested);
    }

    [Fact]
    public void A_refused_audit_stamp_does_not_advance_the_verification_counter()
    {
        // The attempt counter is the one field here that is not idempotent, so a
        // partially-applied mutator is observable rather than merely untidy.
        var domain = TenantDomain.RequestCustomDomain(
            DomainId, Tenant, "learn.acme.com", Clock, Actor);
        domain.MarkVerificationStarted(Clock, Actor);

        var fail = () => domain.MarkVerificationFailed(
            "no TXT record", Clock, UserId.From(Guid.Empty));

        fail.Should().Throw<ArgumentException>();
        domain.VerificationAttempts.Should().Be(0);
        domain.LastVerificationError.Should().BeNull();
        domain.Status.Should().Be(TenantDomainStatus.Verifying);
    }

    private static Organization NewOrganization() => Organization.Create(
        OrganizationId.From(Guid.Parse("22222222-2222-7222-8222-222222222222")),
        Tenant, "acme", "Acme", Clock, Actor);

    private static TenancyDomain.Tenant NewTenant() =>
        TenancyDomain.Tenant.Create(Tenant, "acme", "Acme", Clock, Actor);

    [Theory]
    // Every mapped text bound, asserted where the value is set. The database
    // reports 22001 with no property name, three layers from the call.
    //
    // The long value is a well-formed tag, not a run of one letter. The earlier
    // version asserted `new string('l', 35)` was ACCEPTED — pinning the absence of
    // the BCP-47 check that 12-localization.md says lives in application code as
    // if it were the rule.
    [InlineData("en-Latn-US-scouse", true)]
    [InlineData("en-Latn-US-scouse-scouse-scouse-scouse", false)]
    public void A_locale_is_bounded_by_the_length_its_column_holds(string locale, bool accepted)
    {
        var create = () => TenantLocale.Create(Tenant, locale, isDefault: true);

        if (accepted)
        {
            create.Should().NotThrow();
        }
        else
        {
            create.Should().Throw<ArgumentException>().WithMessage("*the column holds 35*");
        }
    }

    [Theory]
    [InlineData("tr")]
    [InlineData("tr-TR")]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hans-CN")]
    public void A_well_formed_locale_tag_is_accepted(string locale)
    {
        var create = () => TenantLocale.Create(Tenant, locale, isDefault: true);

        create.Should().NotThrow();
    }

    [Theory]
    // Well-formedness lives here because
    // docs/architecture/12-localization.md says the column bounds the length and
    // nothing else — "validated in application code, not by this column".
    [InlineData("lllllllllllllllllllllllllllllllllll")] // 35 letters, and not a tag
    [InlineData("t")]
    [InlineData("tr_TR")]
    [InlineData("tr-")]
    [InlineData("-tr")]
    [InlineData("tr TR")]
    [InlineData("123")]
    public void A_locale_that_is_not_a_bcp47_tag_is_refused(string locale)
    {
        var create = () => TenantLocale.Create(Tenant, locale, isDefault: true);

        create.Should().Throw<ArgumentException>().WithMessage("*BCP-47*");
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public void A_setting_key_is_bounded_by_the_length_its_column_holds(int length, bool accepted)
    {
        var settingId = TenantSettingId.From(Guid.Parse("55555555-2222-7222-8222-222222222222"));

        var create = () => TenantSetting.Create(
            settingId, Tenant, null, new string('k', length), "true", Clock, Actor);

        if (accepted)
        {
            create.Should().NotThrow();
        }
        else
        {
            create.Should().Throw<ArgumentException>().WithMessage("*the column holds 200*");
        }
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public void A_feature_flag_key_is_bounded_by_the_length_its_column_holds(int length, bool accepted)
    {
        var create = () => TenantFeatureFlag.Create(
            Tenant, new string('k', length), "true", Clock.UtcNow, Actor);

        if (accepted)
        {
            create.Should().NotThrow();
        }
        else
        {
            create.Should().Throw<ArgumentException>().WithMessage("*the column holds 200*");
        }
    }

    /// <summary>
    /// An identifier nobody assigned, obtained the only way that compiles.
    /// </summary>
    /// <remarks>
    /// Writing <c>default(TenantId)</c> at a call site is a compile error under
    /// Vogen (VOG009), which is the point — the guard under test exists for the
    /// path the analyzer cannot see, where the zero value arrives through a
    /// generic <c>default(T)</c> or a deserializer. This helper is that path,
    /// reproduced.
    /// </remarks>
    private static T Unassigned<T>()
        where T : struct => default;
}
