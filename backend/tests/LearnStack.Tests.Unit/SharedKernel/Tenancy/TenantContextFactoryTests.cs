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
        var attempt = Authenticated() with
        {
            HostTenantId = TenantA,
            HostOrganizationId = OrgOne,
            ClaimTenantId = TenantA,
            ClaimOrganizationId = OrgTwo,
        };

        attempt.ClaimAgreesWithHost.Should().BeFalse(
            "the organization term is what refuses this row — asserting only the "
            + "outcome let the whole term be deleted, because the membership guard "
            + "then caught the row for an unrelated reason");

        // The two predicates the MIDDLEWARE reads to decide whether to spend a
        // membership call and a validator transaction. Create refuses this row on its
        // own standalone ClaimAgreesWithHost check before either is consulted, so
        // forcing their `&& ClaimAgreesWithHost` conjuncts true left the whole suite
        // green — measured. What the conjuncts are actually load-bearing for is the
        // port economy: a request Create will refuse anyway must not first announce an
        // unvouched tenant id to PostgreSQL.
        attempt.RequiresMembershipCheck.Should().BeFalse(
            "a disagreeing claim buys no membership call");
        attempt.RequiresOrganizationScopeCheck.Should().BeFalse(
            "nor the transaction that would announce its tenant id");

        TenantContextFactory.Create(attempt).IsSuccess.Should().BeFalse();

        // And it stays refused once Phase 03 can answer both questions. This is the
        // deliberated decision ADR-0036 records — a disagreeing claim is a mismatch,
        // not a scope change — and without this line the real reader removes the
        // coincidence that was holding it.
        TenantContextFactory.Create(attempt with
        {
            MembershipCovers = true,
            ClaimedOrganizationBelongsToTenant = true,
        }).IsSuccess.Should().BeFalse();
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

        // The middleware branches on the predicate and never calls Create for these
        // rows — but Create must still refuse rather than throw, because its own
        // contract says so and because a caller that ignores the note is exactly who
        // needs the guard. Without it the tenant dereference below raises
        // InvalidOperationException at the request edge.
        TenantContextFactory.Create(attempt).IsSuccess.Should().BeFalse();
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
    public void Row_14_Without_An_Organization_Claim_Still_Needs_Membership()
    {
        // The variant that has no organization anywhere: a platform host and a
        // tenant-wide claim. RequiresOrganizationScopeCheck is false and membership
        // is the only thing standing between the caller and a tenant no host named.
        var attempt = Authenticated() with { ClaimTenantId = TenantB };

        attempt.RequiresMembershipCheck.Should().BeTrue();
        attempt.RequiresOrganizationScopeCheck.Should().BeFalse();
        TenantContextFactory.Create(attempt).IsSuccess.Should().BeFalse();

        var covered = TenantContextFactory.Create(attempt with { MembershipCovers = true });
        covered.IsSuccess.Should().BeTrue();
        covered.Value!.Origin.Should().Be(TenantContextOrigin.ClaimAndMembership);
        covered.Value.OrganizationId.Should().BeNull();
    }

    [Theory]
    [InlineData(true, false, "an organization claim with no tenant claim to scope it")]
    [InlineData(false, true, "a tenant claim with no subject to attribute it to")]
    public void A_Claim_Shape_The_Matrix_Has_No_Row_For_Is_Refused(
        bool organizationWithoutTenant, bool tenantWithoutSubject, string shape)
    {
        // Both were measured answering — and answering generously. The first took the
        // claim's organization under the anonymous HostOnly ceiling, which is row 11's
        // forbidden scope change reached by omitting a field. The second minted
        // ClaimAndMembership, the strongest ceiling there is, with a null user: a
        // membership attributed to no member.
        var attempt = new TenantResolutionAttempt
        {
            HostTenantId = organizationWithoutTenant ? TenantA : null,
            HostOrganizationId = organizationWithoutTenant ? OrgOne : null,
            ClaimOrganizationId = organizationWithoutTenant ? OrgTwo : null,
            ClaimTenantId = tenantWithoutSubject ? TenantA : null,
            HasValidatedPrincipal = true,
            MembershipCovers = true,
            ClaimedOrganizationBelongsToTenant = true,
        };

        attempt.HasIncoherentClaims.Should().BeTrue(shape);
        attempt.RequiresMembershipCheck.Should().BeFalse(
            "an incoherent claim must not reach a port either — the two ! dereferences "
            + "in the resolver's calls are earned by this");
        attempt.RequiresOrganizationScopeCheck.Should().BeFalse();

        // Asserted separately from the predicate: a variant that made the predicates
        // false without refusing would pass a predicate-only assertion and fail open.
        TenantContextFactory.Create(attempt).IsSuccess.Should().BeFalse(shape);
    }

    [Fact]
    public void Membership_Is_Asked_About_The_Organization_That_Will_Be_Granted()
    {
        // Row 10, where the question and the grant drifted: the resolver asked the
        // strictly weaker tenant-level question while the factory granted the host's
        // organization. ADR-0036 row 10 resolves (T, O) iff M covers (T, O).
        var row10 = Authenticated() with
        {
            HostTenantId = TenantA,
            HostOrganizationId = OrgOne,
            ClaimTenantId = TenantA,
        };

        row10.MembershipQuestionOrganizationId.Should().Be(OrgOne);
        TenantContextFactory.Create(row10 with { MembershipCovers = true })
            .Value!.OrganizationId.Should().Be(row10.MembershipQuestionOrganizationId);

        // Rows 7 and 14 were self-consistent only because the host names no
        // organization on either — which is exactly why the row-10 slip was invisible.
        var row7 = Authenticated() with
        {
            HostTenantId = TenantA,
            ClaimTenantId = TenantA,
            ClaimOrganizationId = OrgOne,
        };
        row7.MembershipQuestionOrganizationId.Should().Be(OrgOne);

        var row14 = Authenticated() with { ClaimTenantId = TenantB, ClaimOrganizationId = OrgTwo };
        row14.MembershipQuestionOrganizationId.Should().Be(OrgTwo);
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

    [Fact]
    public async Task The_Registered_Membership_Reader_Denies_Everything()
    {
        // Nothing in the suite instantiated this type, so the only membership
        // behaviour the corpus exhibited was a permissive test double — and flipping
        // the shipped reader to return true left all 1069 tests green. Its own doc
        // says it exists "so that nobody makes the default permissive to unblock a
        // demo"; this is the line that would notice.
        var reader = new DenyAllTenantMembershipReader();

        (await reader.CoversAsync(Actor, TenantA, OrgOne, CancellationToken.None))
            .Should().BeFalse();
        (await reader.CoversAsync(Actor, TenantA, null, CancellationToken.None))
            .Should().BeFalse("the tenant-level question is denied too");
    }

    [Fact]
    public void An_Implementation_That_States_No_Origin_Carries_None()
    {
        // The default is null, and null is fail-closed ONLY under an allow-list. The
        // pipeline's ceiling check must ask "is this origin one of the ones permitted
        // here?" — written as `Origin != HostOnly` it passes for null and hands an
        // unstated context the run of the API. That check now exists;
        // TenantContextBehaviorTests.A_Resolved_Context_That_States_No_Origin_Reaches_Nothing
        // is what holds it to the allow-list form, and this pins the value it reads.
        ITenantContext silent = new OriginlessContext();

        silent.Origin.Should().BeNull();
        // Through the interface, because Origin is a default interface member and a
        // type that does not restate it has no such member of its own. That is the
        // cost of the small diff, and it is worth knowing: a consumer holding the
        // concrete type cannot read the ceiling at all.
        ((ITenantContext)UnresolvedTenantContext.Instance).Origin.Should().BeNull(
            "an unresolved context resolved nothing, so it carries no authority either");
    }

    private sealed class OriginlessContext : ITenantContext
    {
        public bool IsResolved => true;

        public TenantId TenantId => TenantA;

        public OrganizationId? OrganizationId => null;

        public UserId? UserId => null;

        public string? CorrelationId => null;

        public string? ModuleName => null;
    }

    private static TenantResolutionAttempt Authenticated() =>
        new() { HasValidatedPrincipal = true, UserId = Actor };
}
