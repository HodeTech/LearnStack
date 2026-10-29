using FluentAssertions;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Time;
using Xunit;
using TenantAggregate = LearnStack.Modules.Tenancy.Domain.Tenant;

namespace LearnStack.Tests.Unit.Modules.Tenancy;

public sealed class TenantLocaleAdministrationTests
{
    private static readonly TenantId Tenant =
        TenantId.From(Guid.Parse("11111111-1111-7111-8111-111111111111"));
    private static readonly UserId Creator =
        UserId.From(Guid.Parse("aaaaaaaa-1111-7111-8111-111111111111"));
    private static readonly UserId Editor =
        UserId.From(Guid.Parse("bbbbbbbb-1111-7111-8111-111111111111"));
    private static readonly FixedClock CreatedClock = new(new DateTimeOffset(2026, 10, 28, 8, 0, 0, TimeSpan.Zero));
    private static readonly FixedClock EditedClock = new(CreatedClock.UtcNow.AddDays(1));

    [Fact]
    public void SetLocaleEnabled_FirstEnabledLocale_PromotesExistingRowAndVersionsTenantOnce()
    {
        var tenant = NewTenant();
        tenant.AddLocale("fr", false, CreatedClock, Creator, isEnabled: false, sort: 4);
        tenant.AddLocale("EN-us", false, CreatedClock, Creator, isEnabled: false, sort: 7);
        var target = tenant.Locales.Single(locale => locale.Locale == "en-US");
        var version = tenant.Version;

        tenant.SetLocaleEnabled("en-US", true, EditedClock, Editor).Should().BeTrue();

        tenant.Locales.Should().HaveCount(2);
        tenant.Locales.Single(locale => locale.Locale == "en-US").Should().BeSameAs(target);
        (target.Locale, target.TenantId, target.Sort, target.IsEnabled, target.IsDefault)
            .Should().Be(("en-US", Tenant, (short)7, true, true));
        tenant.Locales.Single(locale => locale.Locale == "fr").IsEnabled.Should().BeFalse();
        tenant.HasValidLocaleConfiguration().Should().BeTrue();
        tenant.Version.Should().Be(version + 1);
        tenant.UpdatedAt.Should().Be(EditedClock.UtcNow);
        tenant.UpdatedBy.Should().Be(Editor);
        tenant.CreatedAt.Should().Be(CreatedClock.UtcNow);
        tenant.CreatedBy.Should().Be(Creator);
    }

    [Fact]
    public void SetLocaleEnabled_AdditionalEnabledLocale_PreservesIncumbentDefault()
    {
        var tenant = NewTenant();
        tenant.AddLocale("en", false, CreatedClock, Creator);
        tenant.AddLocale("fr", false, CreatedClock, Creator, isEnabled: false);
        var version = tenant.Version;

        tenant.SetLocaleEnabled("FR", true, EditedClock, Editor).Should().BeTrue();

        tenant.Locales.Should().OnlyContain(locale => locale.IsEnabled);
        tenant.Locales.Single(locale => locale.IsDefault).Locale.Should().Be("en");
        tenant.Version.Should().Be(version + 1);
        tenant.HasValidLocaleConfiguration().Should().BeTrue();
    }

    [Fact]
    public void SetLocaleEnabled_DisabledNondefault_RetainsMembershipAndPresentationOrder()
    {
        var tenant = NewTenant();
        tenant.AddLocale("en", false, CreatedClock, Creator);
        tenant.AddLocale("fr", false, CreatedClock, Creator, sort: 3);
        var target = tenant.Locales.Single(locale => locale.Locale == "fr");
        var version = tenant.Version;

        tenant.SetLocaleEnabled("fr", false, EditedClock, Editor).Should().BeTrue();

        tenant.Locales.Should().HaveCount(2);
        tenant.Locales.Single(locale => locale.Locale == "fr").Should().BeSameAs(target);
        (target.IsEnabled, target.IsDefault, target.Sort).Should().Be((false, false, (short)3));
        tenant.Locales.Single(locale => locale.IsDefault).Locale.Should().Be("en");
        tenant.Version.Should().Be(version + 1);
        tenant.UpdatedAt.Should().Be(EditedClock.UtcNow);
        tenant.UpdatedBy.Should().Be(Editor);
        tenant.HasValidLocaleConfiguration().Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetLocaleEnabled_UnchangedMembership_LeavesVersionAndAuditStampsUntouched(bool isEnabled)
    {
        var tenant = NewTenant();
        tenant.AddLocale("en-US", false, CreatedClock, Creator, isEnabled: isEnabled);
        var before = Capture(tenant);

        tenant.SetLocaleEnabled("EN-us", isEnabled, EditedClock, Editor).Should().BeFalse();

        Capture(tenant).Should().BeEquivalentTo(before);
    }

    [Fact]
    public void SetLocaleEnabled_DisablingDefault_RefusesBeforeAnyStateChanges()
    {
        var tenant = NewTenant();
        tenant.AddLocale("en-US", false, CreatedClock, Creator);
        tenant.AddLocale("fr", false, CreatedClock, Creator);
        var before = Capture(tenant);

        var disable = () => tenant.SetLocaleEnabled("EN-us", false, EditedClock, Editor);

        disable.Should().Throw<InvalidOperationException>()
            .WithMessage("Select another enabled default before disabling this locale.");
        Capture(tenant).Should().BeEquivalentTo(before);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetLocaleEnabled_MissingLocale_RefusesWithoutIncludingInputOrStamping(bool isEnabled)
    {
        var tenant = NewTenant();
        tenant.AddLocale("en", false, CreatedClock, Creator);
        var before = Capture(tenant);

        var change = () => tenant.SetLocaleEnabled("zz-ZZ", isEnabled, EditedClock, Editor);

        change.Should().Throw<InvalidOperationException>()
            .WithMessage("The tenant does not publish in the requested locale.");
        Capture(tenant).Should().BeEquivalentTo(before);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("en_US")]
    [InlineData("en\n")]
    [InlineData("sensitive-invalid-input@example.test")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefghijkl")]
    public void SetLocaleEnabled_InvalidLocale_RefusesBeforeAnyStateChanges(string? locale)
    {
        var tenant = NewTenant();
        tenant.AddLocale("en", false, CreatedClock, Creator);
        var before = Capture(tenant);

        // Null deliberately exercises the public boundary's programmer-error guard.
        var change = () => tenant.SetLocaleEnabled(locale!, true, EditedClock, Editor);

        change.Should().Throw<ArgumentException>();
        Capture(tenant).Should().BeEquivalentTo(before);
    }

    [Theory]
    [InlineData("no-default")]
    [InlineData("two-defaults")]
    [InlineData("disabled-default")]
    public void SetLocaleEnabled_InvalidExistingConfiguration_RefusesEvenAnOtherwiseUnchangedWrite(string fault)
    {
        var tenant = NewTenant();
        tenant.AddLocale("en", false, CreatedClock, Creator);
        tenant.AddLocale("fr", false, CreatedClock, Creator);
        tenant.AddLocale("de", false, CreatedClock, Creator, isEnabled: false);
        var incumbent = tenant.Locales.Single(locale => locale.Locale == "en");
        if (fault == "no-default") SetLegacyProperty(incumbent, nameof(TenantLocale.IsDefault), false);
        if (fault == "two-defaults")
            SetLegacyProperty(tenant.Locales.Single(locale => locale.Locale == "fr"), nameof(TenantLocale.IsDefault), true);
        if (fault == "disabled-default") SetLegacyProperty(incumbent, nameof(TenantLocale.IsEnabled), false);
        var before = Capture(tenant);

        var change = () => tenant.SetLocaleEnabled("de", false, EditedClock, Editor);

        change.Should().Throw<InvalidOperationException>()
            .WithMessage("The existing locale configuration is invalid; remediate it explicitly.");
        Capture(tenant).Should().BeEquivalentTo(before);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void SetLocaleEnabled_InvalidAuditInput_LeavesChildAndRootUntouched(bool enabling, bool invalidClock)
    {
        var tenant = NewTenant();
        tenant.AddLocale("en", false, CreatedClock, Creator);
        tenant.AddLocale("fr", false, CreatedClock, Creator, isEnabled: !enabling);
        var before = Capture(tenant);
        var clock = invalidClock ? new FixedClock(default) : EditedClock;
        var actor = invalidClock ? Editor : UserId.From(Guid.Empty);

        var change = () => tenant.SetLocaleEnabled("fr", enabling, clock, actor);

        change.Should().Throw<ArgumentException>();
        Capture(tenant).Should().BeEquivalentTo(before);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RemoveLocale_NondefaultMembership_RemovesOnlyTargetAndVersionsRoot(bool isEnabled)
    {
        var tenant = NewTenant();
        tenant.AddLocale("en", false, CreatedClock, Creator);
        tenant.AddLocale("fr-FR", false, CreatedClock, Creator, isEnabled: isEnabled);
        var incumbent = tenant.Locales.Single(locale => locale.IsDefault);
        var version = tenant.Version;

        tenant.RemoveLocale("FR-fr", EditedClock, Editor);

        tenant.Locales.Should().ContainSingle().Which.Should().BeSameAs(incumbent);
        incumbent.IsDefault.Should().BeTrue();
        tenant.Version.Should().Be(version + 1);
        tenant.UpdatedAt.Should().Be(EditedClock.UtcNow);
        tenant.UpdatedBy.Should().Be(Editor);
        tenant.HasValidLocaleConfiguration().Should().BeTrue();
    }

    [Fact]
    public void RemoveLocale_LastDisabledMembership_AllowsEmptyConfiguration()
    {
        var tenant = NewTenant();
        tenant.AddLocale("fr", false, CreatedClock, Creator, isEnabled: false);
        var version = tenant.Version;

        tenant.RemoveLocale("fr", EditedClock, Editor);

        tenant.Locales.Should().BeEmpty();
        tenant.Version.Should().Be(version + 1);
        tenant.HasValidLocaleConfiguration().Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemoveLocale_DefaultMembership_RefusesUntilAnotherEnabledDefaultIsSelected(bool hasAlternative)
    {
        var tenant = NewTenant();
        tenant.AddLocale("en", false, CreatedClock, Creator);
        if (hasAlternative) tenant.AddLocale("fr", false, CreatedClock, Creator);
        var before = Capture(tenant);

        var remove = () => tenant.RemoveLocale("en", EditedClock, Editor);

        remove.Should().Throw<InvalidOperationException>();
        Capture(tenant).Should().BeEquivalentTo(before);
        if (!hasAlternative) return;

        tenant.SetDefaultLocale("fr", EditedClock, Editor);
        tenant.RemoveLocale("en", EditedClock, Editor);
        tenant.Locales.Should().ContainSingle().Which.Locale.Should().Be("fr");
        tenant.HasValidLocaleConfiguration().Should().BeTrue();
    }

    [Fact]
    public void SetLocaleEnabled_AfterDefaultSwitch_DisablesFormerDefaultWithoutChangingSuccessor()
    {
        var tenant = NewTenant();
        tenant.AddLocale("en", false, CreatedClock, Creator);
        tenant.AddLocale("fr", false, CreatedClock, Creator);
        tenant.SetDefaultLocale("fr", EditedClock, Editor);
        var version = tenant.Version;

        tenant.SetLocaleEnabled("en", false, EditedClock, Editor).Should().BeTrue();

        tenant.Locales.Single(locale => locale.IsDefault).Locale.Should().Be("fr");
        tenant.Locales.Single(locale => locale.Locale == "en").IsEnabled.Should().BeFalse();
        tenant.Version.Should().Be(version + 1);
        tenant.HasValidLocaleConfiguration().Should().BeTrue();
    }

    private static TenantAggregate NewTenant() => TenantAggregate.Create(Tenant, "school", "School", CreatedClock, Creator);

    private static object Capture(TenantAggregate tenant) => new
    {
        tenant.Version,
        tenant.CreatedAt,
        tenant.CreatedBy,
        tenant.UpdatedAt,
        tenant.UpdatedBy,
        Locales = tenant.Locales.Select(locale => new
        {
            locale.TenantId,
            locale.Locale,
            locale.IsEnabled,
            locale.IsDefault,
            locale.Sort,
        }).ToArray(),
    };

    private static void SetLegacyProperty(TenantLocale locale, string propertyName, bool value)
    {
        // Materialized legacy corruption cannot be constructed through supported root operations.
        var property = typeof(TenantLocale).GetProperty(propertyName)
            ?? throw new InvalidOperationException("The test's legacy locale property is absent.");
        property.SetValue(locale, value);
    }

    private sealed record FixedClock(DateTimeOffset UtcNow) : IClock;
}
