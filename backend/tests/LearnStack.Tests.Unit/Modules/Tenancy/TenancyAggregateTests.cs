using FluentAssertions;
using LearnStack.Modules.Tenancy.Domain;
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

        domain.MarkVerificationFailed("no TXT record", Clock, Actor);

        domain.Status.Should().Be(TenantDomainStatus.Failed);
        domain.VerificationAttempts.Should().Be(1);
        domain.LastVerificationError.Should().Be("no TXT record");

        domain.MarkVerified(Clock, Actor);

        domain.Status.Should().Be(TenantDomainStatus.Verified);
        domain.VerificationAttempts.Should().Be(2);
        domain.LastVerificationError.Should().BeNull("a success clears the previous failure");
        domain.Version.Should().Be(2, "each attempt is an audited, versioned mutation");
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
