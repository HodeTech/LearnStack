using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Modules.Tenancy.Application.Account;
using LearnStack.Modules.Tenancy.Application.Audit;
using LearnStack.Modules.Tenancy.Application.Contracts.Account;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.Modules.Tenancy.Application.PublicReads;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Modules.Tenancy;

public sealed class GetAccountSiteQueryTests
{
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly OrganizationId Organization = OrganizationId.From(Guid.CreateVersion7());
    private static readonly UserId Actor = UserId.From(Guid.CreateVersion7());
    private static readonly PublicTenantConfiguration Configuration = new("Configured site", ["tr-TR", "en"], "tr-TR");
    private static readonly BrandingTheme Theme = new("#2345aa", "#ffffff", "#111111", "#555555");

    [Theory]
    [InlineData("unresolved")]
    [InlineData("unresolved-with-values")]
    [InlineData("origin-absent")]
    [InlineData("origin-unknown")]
    [InlineData("host-only-with-actor")]
    [InlineData("ambient")]
    [InlineData("claim-and-membership")]
    [InlineData("actor-absent")]
    [InlineData("actor-uninitialized")]
    [InlineData("actor-nil")]
    [InlineData("actor-system")]
    [InlineData("tenant-uninitialized")]
    [InlineData("tenant-nil")]
    [InlineData("tenant-sentinel")]
    [InlineData("matching-tenant-nil")]
    [InlineData("matching-tenant-sentinel")]
    [InlineData("tenant-mismatch")]
    [InlineData("host-absent")]
    [InlineData("host-tenant-uninitialized")]
    [InlineData("host-tenant-nil")]
    [InlineData("host-tenant-sentinel")]
    [InlineData("host-tenant-mismatch")]
    [InlineData("organization-uninitialized")]
    [InlineData("organization-nil")]
    [InlineData("matching-organization-nil")]
    [InlineData("host-organization-uninitialized")]
    [InlineData("host-organization-nil")]
    [InlineData("host-organization-mismatch")]
    [InlineData("host-organization-resolved-absent")]
    public async Task Context_refusal_is_not_found_before_any_configuration_settings_or_feature_read(string failure)
    {
        var readers = new Readers();

        var result = await readers.Account(RefusedContext(failure)).Handle(new(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeSameAs(TenantContextFactory.Refused);
        result.Error!.Code.Should().Be("not_found");
        readers.AssertUntouched();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Future_row14_membership_success_still_refuses_even_with_a_planted_matching_host_scope(bool plantedScope)
    {
        var attempt = new TenantResolutionAttempt
        {
            ClaimTenantId = Tenant,
            ClaimOrganizationId = Organization,
            HasValidatedPrincipal = true,
            UserId = Actor,
            MembershipCovers = true,
        };
        attempt.RequiresMembershipCheck.Should().BeTrue();
        var resolved = TenantContextFactory.Create(attempt);
        resolved.IsSuccess.Should().BeTrue();
        resolved.Value!.Origin.Should().Be(TenantContextOrigin.ClaimAndMembership);
        resolved.Value.HostScope.Should().BeNull();
        var context = Copy(resolved.Value) with
        {
            HostScope = plantedScope ? Host(Tenant, Organization) : null,
        };
        var readers = new Readers();

        var result = await readers.Account(context).Handle(new(), CancellationToken.None);

        result.Error.Should().BeSameAs(TenantContextFactory.Refused);
        readers.AssertUntouched();
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("organization")]
    [InlineData("tenant-host-narrowed-by-claim")]
    [InlineData("organization-host-confirmed-by-membership")]
    public async Task Host_and_claim_preserves_the_real_actor_and_resolved_scope(string scope)
    {
        var context = Resolved(scope);
        var readers = new Readers();

        var result = await readers.Account(context).Handle(new(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.ActorId.Should().Be(Actor);
        result.Value.TenantId.Should().Be(Tenant);
        result.Value.OrganizationId.Should().Be(context.OrganizationId);
        result.Value.Site.DisplayName.Should().Be(Configuration.DisplayName);
        await readers.Configuration.Received(1).ReadAsync(CancellationToken.None);
        await readers.Settings.Received(1).ReadAsync(TenantSettingKeys.BrandingTheme, CancellationToken.None);
        await readers.Features.Received(1).IsEnabledAsync(FeatureKeys.WhiteLabelBranding, CancellationToken.None);
    }

    [Theory]
    [InlineData("present", true)]
    [InlineData("present", false)]
    [InlineData("absent", true)]
    [InlineData("absent", false)]
    [InlineData("invalid", true)]
    [InlineData("invalid", false)]
    public async Task Account_and_public_reads_share_exact_fields_whole_theme_or_null_and_effective_attribution(
        string theme, bool whiteLabel)
    {
        var readers = new Readers();
        readers.Settings.ReadAsync(TenantSettingKeys.BrandingTheme, Arg.Any<CancellationToken>()).Returns(
            theme == "invalid"
                ? Result<TenantSettingRead<BrandingTheme>>.Fail(new Error(new LocalizedMessage("lockey_dependency_unavailable")))
                : Result.Ok(new TenantSettingRead<BrandingTheme>(theme == "present" ? Theme : null)));
        readers.Features.IsEnabledAsync(FeatureKeys.WhiteLabelBranding, Arg.Any<CancellationToken>()).Returns(whiteLabel);

        var account = await readers.Account(Resolved()).Handle(new(), CancellationToken.None);
        var publicSite = await readers.Public().Handle(new(), CancellationToken.None);

        var expected = new PublicSite(Configuration.DisplayName, Configuration.EnabledLocales, Configuration.DefaultLocale!,
            theme == "present" ? new PublicTheme(Theme.Primary, Theme.Background, Theme.Foreground, Theme.Muted) : null,
            !whiteLabel);
        account.IsSuccess.Should().BeTrue();
        publicSite.IsSuccess.Should().BeTrue();
        account.Value!.Site.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
        publicSite.Value.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
        await readers.Configuration.Received(2).ReadAsync(CancellationToken.None);
        await readers.Settings.Received(2).ReadAsync(TenantSettingKeys.BrandingTheme, CancellationToken.None);
        await readers.Features.Received(2).IsEnabledAsync(FeatureKeys.WhiteLabelBranding, CancellationToken.None);
    }

    [Theory]
    [InlineData("lockey_not_found")]
    [InlineData("lockey_dependency_unavailable")]
    public async Task Configuration_refusals_propagate_without_settings_or_feature_reads(string key)
    {
        var readers = new Readers();
        var error = new Error(new LocalizedMessage(key));
        readers.Configuration.ReadAsync(Arg.Any<CancellationToken>())
            .Returns(Result<PublicTenantConfiguration>.Fail(error));

        var account = await readers.Account(Resolved()).Handle(new(), CancellationToken.None);
        var publicSite = await readers.Public().Handle(new(), CancellationToken.None);

        account.Error.Should().BeSameAs(error);
        publicSite.Error.Should().BeSameAs(error);
        readers.Settings.ReceivedCalls().Should().BeEmpty();
        readers.Features.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task No_enabled_locales_or_no_default_refuses_both_projections_before_settings_and_features(
        bool empty, bool missingDefault)
    {
        var readers = new Readers();
        readers.Configuration.ReadAsync(Arg.Any<CancellationToken>()).Returns(Result.Ok(new PublicTenantConfiguration(
            Configuration.DisplayName, empty ? [] : Configuration.EnabledLocales,
            missingDefault ? null : Configuration.DefaultLocale)));

        var account = await readers.Account(Resolved()).Handle(new(), CancellationToken.None);
        var publicSite = await readers.Public().Handle(new(), CancellationToken.None);

        account.Error!.Code.Should().Be("not_found");
        publicSite.Error!.Code.Should().Be("not_found");
        readers.Settings.ReceivedCalls().Should().BeEmpty();
        readers.Features.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_token_reaches_all_three_readers()
    {
        var readers = new Readers();
        using var cancellation = new CancellationTokenSource();

        var result = await readers.Account(Resolved()).Handle(new(), cancellation.Token);

        result.IsSuccess.Should().BeTrue();
        await readers.Configuration.Received(1).ReadAsync(cancellation.Token);
        await readers.Settings.Received(1).ReadAsync(TenantSettingKeys.BrandingTheme, cancellation.Token);
        await readers.Features.Received(1).IsEnabledAsync(FeatureKeys.WhiteLabelBranding, cancellation.Token);
    }

    [Fact]
    public void No_parameter_contract_is_validated_and_the_authenticated_query_is_explicitly_off_and_unmarked()
    {
        var validator = new GetAccountSiteQueryValidator();
        validator.Validate(new GetAccountSiteQuery()).IsValid.Should().BeTrue();
        var unexpected = validator.Validate(new GetAccountSiteQuery(true));
        unexpected.Errors.Should().ContainSingle().Which.PropertyName.Should().Be("Query");
        unexpected.Errors[0].ErrorCode.Should().Be("lockey_invalid_value");

        var catalog = new AuditCatalog([new TenancyAuditCatalogSource()]);
        catalog.TryGet(typeof(GetAccountSiteQuery), out var registration).Should().BeTrue();
        registration.WritesNoRow.Should().BeTrue();
        registration.Entries.Should().BeEmpty();
        typeof(GetAccountSiteQuery).IsDefined(typeof(PublicSurfaceAttribute), inherit: false).Should().BeFalse();
        typeof(GetAccountSiteQuery).IsDefined(typeof(AllowsUnresolvedTenantContextAttribute), inherit: false).Should().BeFalse();
    }

    private static ITenantContext RefusedContext(string failure)
    {
        var valid = Copy(Resolved());
        return failure switch
        {
            "unresolved" => UnresolvedTenantContext.Instance,
            "unresolved-with-values" => valid with { IsResolved = false },
            "origin-absent" => valid with { Origin = null },
            "origin-unknown" => valid with { Origin = (TenantContextOrigin)999 },
            "host-only-with-actor" => valid with { Origin = TenantContextOrigin.HostOnly },
            "ambient" => valid with { Origin = TenantContextOrigin.Ambient },
            "claim-and-membership" => valid with { Origin = TenantContextOrigin.ClaimAndMembership },
            "actor-absent" => valid with { UserId = null },
            "actor-uninitialized" => valid with { UserId = (new UserId[1])[0] },
            "actor-nil" => valid with { UserId = UserId.From(Guid.Empty) },
            "actor-system" => valid with { UserId = UserId.SystemActor },
            "tenant-uninitialized" => valid with { TenantId = (new TenantId[1])[0] },
            "tenant-nil" => valid with { TenantId = TenantId.From(Guid.Empty) },
            "tenant-sentinel" => valid with { TenantId = TenantId.PlatformSentinel },
            "matching-tenant-nil" => valid with
            {
                TenantId = TenantId.From(Guid.Empty),
                HostScope = Host(TenantId.From(Guid.Empty)),
            },
            "matching-tenant-sentinel" => valid with
            {
                TenantId = TenantId.PlatformSentinel,
                HostScope = Host(TenantId.PlatformSentinel),
            },
            "tenant-mismatch" => valid with { TenantId = TenantId.From(Guid.CreateVersion7()) },
            "host-absent" => valid with { HostScope = null },
            "host-tenant-uninitialized" => valid with { HostScope = Host((new TenantId[1])[0]) },
            "host-tenant-nil" => valid with { HostScope = Host(TenantId.From(Guid.Empty)) },
            "host-tenant-sentinel" => valid with { HostScope = Host(TenantId.PlatformSentinel) },
            "host-tenant-mismatch" => valid with { HostScope = Host(TenantId.From(Guid.CreateVersion7())) },
            "organization-uninitialized" => valid with { OrganizationId = (new OrganizationId[1])[0] },
            "organization-nil" => valid with { OrganizationId = OrganizationId.From(Guid.Empty) },
            "matching-organization-nil" => valid with
            {
                OrganizationId = OrganizationId.From(Guid.Empty),
                HostScope = Host(Tenant, OrganizationId.From(Guid.Empty)),
            },
            "host-organization-uninitialized" => valid with { HostScope = Host(Tenant, (new OrganizationId[1])[0]) },
            "host-organization-nil" => valid with { HostScope = Host(Tenant, OrganizationId.From(Guid.Empty)) },
            "host-organization-mismatch" => valid with
            {
                OrganizationId = Organization,
                HostScope = Host(Tenant, OrganizationId.From(Guid.CreateVersion7())),
            },
            "host-organization-resolved-absent" => valid with { HostScope = Host(Tenant, Organization) },
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
    }

    private static TenantContext Resolved(string scope = "tenant")
    {
        var result = TenantContextFactory.Create(new TenantResolutionAttempt
        {
            HostTenantId = Tenant,
            HostOrganizationId = scope is "organization" or "organization-host-confirmed-by-membership" ? Organization : null,
            ClaimTenantId = Tenant,
            ClaimOrganizationId = scope is "organization" or "tenant-host-narrowed-by-claim" ? Organization : null,
            HasValidatedPrincipal = true,
            UserId = Actor,
            MembershipCovers = true,
            ClaimedOrganizationBelongsToTenant = true,
        });
        result.IsSuccess.Should().BeTrue();
        return result.Value!;
    }

    // Plant malformed provenance without asking the hardened factory to mint it.
    // These cases independently exercise the handler's defensive ceiling.
    private static HostScope Host(TenantId tenant, OrganizationId? organization = null) =>
        (HostScope)Activator.CreateInstance(typeof(HostScope),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null, args: [tenant, organization], culture: null)!;

    private static TestContext Copy(ITenantContext context) => new(
        context.TenantId, context.OrganizationId, context.UserId, context.Origin, context.HostScope);

    private sealed record TestContext(
        TenantId TenantId, OrganizationId? OrganizationId, UserId? UserId,
        TenantContextOrigin? Origin, HostScope? HostScope, bool IsResolved = true) : ITenantContext
    {
        public string? CorrelationId => null;
        public string? ModuleName => "tenancy";
    }

    private sealed class Readers
    {
        public IPublicTenantConfigurationReader Configuration { get; } = Substitute.For<IPublicTenantConfigurationReader>();
        public ITenantSettingsAccessor Settings { get; } = Substitute.For<ITenantSettingsAccessor>();
        public IFeatureFlags Features { get; } = Substitute.For<IFeatureFlags>();

        public Readers()
        {
            Configuration.ReadAsync(Arg.Any<CancellationToken>()).Returns(Result.Ok(GetAccountSiteQueryTests.Configuration));
            Settings.ReadAsync(TenantSettingKeys.BrandingTheme, Arg.Any<CancellationToken>())
                .Returns(Result.Ok(new TenantSettingRead<BrandingTheme>(Theme)));
            Features.IsEnabledAsync(FeatureKeys.WhiteLabelBranding, Arg.Any<CancellationToken>()).Returns(true);
        }

        public GetAccountSiteQueryHandler Account(ITenantContext context) => new(context, Configuration, Settings, Features);
        public GetPublicSiteQueryHandler Public() => new(Configuration, Settings, Features);

        public void AssertUntouched()
        {
            Configuration.ReceivedCalls().Should().BeEmpty();
            Settings.ReceivedCalls().Should().BeEmpty();
            Features.ReceivedCalls().Should().BeEmpty();
        }
    }
}
