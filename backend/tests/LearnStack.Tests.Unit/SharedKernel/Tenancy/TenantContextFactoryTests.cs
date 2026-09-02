using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Tenancy;

/// <summary>
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036</see>'s
/// reconciliation matrix, driven row by row.
/// </summary>
/// <remarks>
/// <para>
/// The matrix is "total over the signal space", and a total function is exactly what
/// a unit suite can hold to account. Every row below is named for its number so a
/// reader can put the two documents side by side; a row that stops matching its
/// entry is a failure here rather than a discovery in Phase 02b.
/// </para>
/// <para>
/// <b>Most of these rows cannot happen yet.</b> Rows 6–12, 14 and 15 all need a
/// validated claim, and there is no <c>UseAuthentication</c> until Phase 02b. They
/// are tested anyway, and this is the one place where testing an unreachable path is
/// right: the factory is pure, so the rows are reachable <i>as a function</i>, and
/// the alternative is shipping the arithmetic of an authority ceiling with no
/// evidence and finding out when authentication lands.
/// </para>
/// </remarks>
public sealed class TenantContextFactoryTests
{
    private static readonly TenantId TenantA = TenantId.From(Guid.Parse("018f4d40-0000-7000-8000-00000000a001"));
    private static readonly TenantId TenantB = TenantId.From(Guid.Parse("018f4d40-0000-7000-8000-00000000b001"));
    private static readonly OrganizationId OrgOne = OrganizationId.From(Guid.Parse("018f4d40-0000-7000-8000-0000000000a1"));
    private static readonly OrganizationId OrgTwo = OrganizationId.From(Guid.Parse("018f4d40-0000-7000-8000-0000000000a2"));
    private static readonly UserId Actor = UserId.From(Guid.Parse("018f4d40-0000-7000-8000-0000000000f1"));

    [Fact]
    public void Row_2_A_tenant_host_with_no_token_resolves_the_tenant_under_the_host_only_ceiling()
    {
        var result = TenantContextFactory.Create(new TenantResolutionAttempt
        {
            HostTenantId = TenantA,
        });

        result.IsSuccess.Should().BeTrue();
        result.Value!.TenantId.Should().Be(TenantA);
        result.Value!.OrganizationId.Should().BeNull();
        result.Value!.Origin.Should().Be(TenantContextOrigin.HostOnly,
            "an anonymous page load reaches [PublicSurface] request types and nothing else");
        result.Value!.UserId.Should().BeNull();
    }

    [Fact]
    public void Row_3_An_org_host_carries_the_mapping_rows_organization_as_the_anonymous_scope()
    {
        // The anonymous organization scope IS the host-mapping row, not the tenant's
        // organization count — a tenant that wants its default branch's content on
        // its public site seeds organization_id into that row. That removes a code
        // branch, and this is the assertion that the branch stayed removed.
        var result = TenantContextFactory.Create(new TenantResolutionAttempt
        {
            HostTenantId = TenantA,
            HostOrganizationId = OrgOne,
        });

        result.IsSuccess.Should().BeTrue();
        result.Value!.OrganizationId.Should().Be(OrgOne);
        result.Value!.Origin.Should().Be(TenantContextOrigin.HostOnly);
    }

    [Fact]
    public void Row_6_A_tenant_host_and_an_agreeing_claim_need_no_membership()
    {
        // Rows 6 and 9 are the two rows that resolve WITHOUT consulting membership,
        // and they are the rows an over-firing predicate breaks. If this ever needs
        // MembershipCovers, every authenticated user is 404'd on their own tenant's
        // own host the moment Phase 02b lands — and nothing in Packet 7 traffic
        // would reveal it, because no claim exists to trigger it.
        var result = TenantContextFactory.Create(Authenticated() with
        {
            HostTenantId = TenantA,
            ClaimTenantId = TenantA,
        });

        result.IsSuccess.Should().BeTrue();
        result.Value!.Origin.Should().Be(TenantContextOrigin.HostAndClaim);
        result.Value!.UserId.Should().Be(Actor);
    }

    [Fact]
    public void Row_7_A_claim_reaching_past_the_host_needs_both_answers()
    {
        var attempt = Authenticated() with
        {
            HostTenantId = TenantA,
            ClaimTenantId = TenantA,
            ClaimOrganizationId = OrgOne,
        };

        attempt.RequiresMembershipCheck.Should().BeTrue();
        attempt.RequiresOrganizationScopeCheck.Should().BeTrue(
            "row 7 is the one row of the matrix carrying an ∈ term");

        TenantContextFactory.Create(attempt with { MembershipCovers = true })
            .IsSuccess.Should().BeFalse("the ∈ answer is missing, and missing is a refusal");

        TenantContextFactory.Create(attempt with { ClaimedOrganizationBelongsToTenant = true })
            .IsSuccess.Should().BeFalse("membership is missing");

        var resolved = TenantContextFactory.Create(attempt with
        {
            MembershipCovers = true,
            ClaimedOrganizationBelongsToTenant = true,
        });

        resolved.IsSuccess.Should().BeTrue();
        resolved.Value!.OrganizationId.Should().Be(OrgOne, "the claim narrows within the tenant");
        resolved.Value!.Origin.Should().Be(TenantContextOrigin.HostAndClaim,
            "membership CONFIRMED a claim here; it did not carry the tenant");
    }

    [Fact]
    public void Row_8_A_token_for_another_tenant_on_this_tenants_host_is_refused()
    {
        // The architecture/13 cross-check. Stated in the corpus as a fault detector
        // rather than an authorization control: a client holding a valid token for
        // T' can still address a platform host and take row 14, which grants only
        // their own tenant. The control is that no signal outside the intersection
        // can select a tenant.
        var result = TenantContextFactory.Create(Authenticated() with
        {
            HostTenantId = TenantA,
            ClaimTenantId = TenantB,
        });

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Row_9_An_org_host_and_a_claim_naming_the_same_pair_agree()
    {
        var attempt = Authenticated() with
        {
            HostTenantId = TenantA,
            HostOrganizationId = OrgOne,
            ClaimTenantId = TenantA,
            ClaimOrganizationId = OrgOne,
        };

        attempt.RequiresMembershipCheck.Should().BeFalse("the host already vouches for this pair");

        var result = TenantContextFactory.Create(attempt);

        result.IsSuccess.Should().BeTrue();
        result.Value!.OrganizationId.Should().Be(OrgOne);
        result.Value!.Origin.Should().Be(TenantContextOrigin.HostAndClaim);
    }

    [Fact]
    public void Row_10_An_org_host_with_a_tenant_wide_claim_needs_membership()
    {
        var attempt = Authenticated() with
        {
            HostTenantId = TenantA,
            HostOrganizationId = OrgOne,
            ClaimTenantId = TenantA,
        };

        attempt.RequiresMembershipCheck.Should().BeTrue();
        attempt.RequiresOrganizationScopeCheck.Should().BeFalse(
            "the claim names no organization, so there is nothing whose belonging to ask about");

        TenantContextFactory.Create(attempt).IsSuccess.Should().BeFalse(
            "unasked is refused — DenyAllTenantMembershipReader is what makes this the live answer");

        var covered = TenantContextFactory.Create(attempt with { MembershipCovers = true });
        covered.IsSuccess.Should().BeTrue();
        covered.Value!.OrganizationId.Should().Be(OrgOne, "the host's organization stands");
    }

    [Fact]
    public void Row_11_A_claim_naming_a_different_organization_of_the_same_tenant_is_a_mismatch()
    {
        // Not a scope change. An earlier draft let the host's organization win over a
        // disagreeing claim, citing shareable branch links — a citation that turned
        // out to describe `?org=<slug>`, a search parameter this ADR trusts for
        // nothing. Making it a refusal also removes a durable write from a happy
        // path: nothing re-issues the token, so the disagreement would hold for the
        // whole session and every subresource fetch would re-emit the event.
        var result = TenantContextFactory.Create(Authenticated() with
        {
            HostTenantId = TenantA,
            HostOrganizationId = OrgOne,
            ClaimTenantId = TenantA,
            ClaimOrganizationId = OrgTwo,
        });

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Row_12_A_tenant_disagreement_wins_before_the_organization_term_is_reached()
    {
        var result = TenantContextFactory.Create(Authenticated() with
        {
            HostTenantId = TenantA,
            HostOrganizationId = OrgOne,
            ClaimTenantId = TenantB,
            ClaimOrganizationId = OrgOne,
        });

        result.IsSuccess.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, "row 13 — a platform host with no token")]
    [InlineData(true, "row 15 — a valid token carrying no tenant claim")]
    public void Rows_13_And_15_Name_No_Tenant_And_That_Is_Not_A_Failure(
        bool authenticated, string row)
    {
        // NamesNoTenant is what the middleware branches on; Create is not called for
        // these rows at all. Asserting the predicate rather than the refusal is the
        // point: "no tenant" and "refused" are different outcomes, and only the
        // second is an error.
        var attempt = new TenantResolutionAttempt { HasValidatedPrincipal = authenticated };

        attempt.NamesNoTenant.Should().BeTrue(row);
    }

    [Fact]
    public void Row_14_A_platform_host_with_a_claim_is_carried_by_membership_alone()
    {
        var attempt = Authenticated() with { ClaimTenantId = TenantB, ClaimOrganizationId = OrgTwo };

        attempt.NamesNoTenant.Should().BeFalse("the claim names one even though no host does");
        attempt.RequiresMembershipCheck.Should().BeTrue();
        attempt.RequiresOrganizationScopeCheck.Should().BeFalse(
            "row 14 names M alone: a membership record is per (tenant, organization), so an "
            + "active membership covering the pair already establishes the belonging");

        TenantContextFactory.Create(attempt).IsSuccess.Should().BeFalse(
            "the Studio tenant switcher 404s for everyone until Phase 03 ships Membership");

        var covered = TenantContextFactory.Create(attempt with { MembershipCovers = true });

        covered.IsSuccess.Should().BeTrue();
        covered.Value!.TenantId.Should().Be(TenantB);
        covered.Value!.Origin.Should().Be(TenantContextOrigin.ClaimAndMembership,
            "no host named this tenant, so membership is what carried it — the one row that is");
    }

    [Fact]
    public void Every_refusal_carries_the_same_error()
    {
        // A caller able to tell row 8 (a tenant that exists, claimed by a token for
        // another) from row 10 (an organization no membership covers) would have an
        // oracle over which tenants and organizations exist. The wire is already
        // bodyless; this pins the layer above it.
        var refusals = new[]
        {
            TenantContextFactory.Create(Authenticated() with
            {
                HostTenantId = TenantA, ClaimTenantId = TenantB,
            }),
            TenantContextFactory.Create(Authenticated() with
            {
                HostTenantId = TenantA, HostOrganizationId = OrgOne, ClaimTenantId = TenantA,
            }),
            TenantContextFactory.Create(Authenticated() with
            {
                HostTenantId = TenantA, HostOrganizationId = OrgOne,
                ClaimTenantId = TenantA, ClaimOrganizationId = OrgTwo,
            }),
        };

        refusals.Should().OnlyContain(result => !result.IsSuccess);
        refusals.Should().OnlyContain(result => result.Error == TenantContextFactory.Refused);
        TenantContextFactory.Refused.Message.Key.Should().Be("lockey_not_found",
            "not tenant_mismatch — the wire must match the anonymous case, and "
            + "tenant_mismatch is the authenticated code");
    }

    [Fact]
    public void An_unanswered_question_is_a_refusal_and_never_a_pass()
    {
        // `is not true` and not `== false`. The difference is a caller that forgot to
        // ask: under `== false` a null answer would sail through and widen the
        // context to whatever the claim asked for.
        var attempt = Authenticated() with
        {
            HostTenantId = TenantA,
            ClaimTenantId = TenantA,
            ClaimOrganizationId = OrgOne,
        };

        attempt.MembershipCovers.Should().BeNull();
        TenantContextFactory.Create(attempt).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void The_factory_refuses_a_null_attempt_rather_than_inventing_one()
    {
        var act = () => TenantContextFactory.Create(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private static TenantResolutionAttempt Authenticated() =>
        new() { HasValidatedPrincipal = true, UserId = Actor };
}
