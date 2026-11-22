using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Audit;

/// <summary>
/// The redaction plan's three closed shapes and the closure's summed report
/// (<see href="../../../../../docs/decisions/0108-privileged-audit-redaction.md">ADR-0108 §§ 3, 4, 8</see>): an
/// erasure's plan keeps its request's subject and reach, a closure's tenant pass reaches one real tenant by every
/// Membership the person holds there, its sentinel pass reaches only the platform sentinel by the subject key, and the
/// aggregate's document is counts only and bounded however many tenants.
/// </summary>
public sealed class AuditRedactionPlanTests
{
    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("0190aaaa-0000-7000-8000-00000000aaaa"));
    private static readonly OrganizationId Branch = OrganizationId.From(Guid.Parse("0190bbbb-0000-7000-8000-00000000bbbb"));
    private static readonly UserId Person = UserId.From(Guid.Parse("0190c0de-0000-7000-8000-00000000c0de"));
    private static readonly Guid MembershipA = Guid.Parse("0190c0de-0000-7000-8000-0000000000a1");
    private static readonly Guid MembershipB = Guid.Parse("0190c0de-0000-7000-8000-0000000000a2");
    private static readonly IdentitySubjectKey Key = IdentitySubjectKey.FromBytes(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());

    [Theory]
    [InlineData(DataRequestOrigin.Self, true)]
    [InlineData(DataRequestOrigin.ClosureFanout, true)]
    [InlineData(DataRequestOrigin.Administrator, false)]
    public void An_erasure_plan_keeps_its_request_s_subject_and_the_reach_of_who_asked(DataRequestOrigin origin, bool wholeTenant)
    {
        var subject = new UserDataSubject(Guid.CreateVersion7(), DataRequestKind.Erasure, origin, Person,
            new DataRequestScopeGroup(Tenant, Branch), [MembershipA]);

        var plan = new AuditRedactionPlan(subject);

        plan.Pass.Should().Be(AuditRedactionPass.Erasure);
        plan.Subject.Should().BeSameAs(subject);
        (plan.TenantId, plan.OrganizationId, plan.WholeTenant, plan.SubjectUserId).Should().Be((Tenant, (OrganizationId?)Branch, wholeTenant, Person));
        plan.MembershipIds.Should().Equal(MembershipA);
        plan.SubjectKey.Should().BeNull("only the sentinel pass selects by the key");
    }

    [Theory]
    [InlineData(DataRequestKind.Export)]
    [InlineData(DataRequestKind.Closure)]
    public void The_request_constructor_takes_an_erasure_s_subject_alone(DataRequestKind kind)
    {
        var subject = new UserDataSubject(Guid.CreateVersion7(), kind, DataRequestOrigin.Self, Person,
            new DataRequestScopeGroup(Tenant, null), [MembershipA]);

        FluentActions.Invoking(() => new AuditRedactionPlan(subject)).Should().Throw<ArgumentException>(
            "an export is never redacted, and a closure's redaction is its Finalize's passes, never one request's");
    }

    [Fact]
    public void A_closure_tenant_pass_reaches_the_whole_tenant_by_every_membership_and_is_no_request()
    {
        var plan = AuditRedactionPlan.ForClosureTenant(Tenant, Person, [MembershipA, MembershipB]);

        plan.Pass.Should().Be(AuditRedactionPass.ClosureTenant);
        (plan.TenantId, plan.OrganizationId, plan.WholeTenant, plan.SubjectUserId).Should().Be((Tenant, (OrganizationId?)null, true, Person),
            "one pass reaches every group of the tenant (ADR-0108 § 4)");
        plan.MembershipIds.Should().Equal(MembershipA, MembershipB);
        plan.SubjectKey.Should().BeNull();
        FluentActions.Invoking(() => plan.Subject).Should().Throw<InvalidOperationException>("a closure's pass is not one request's");
    }

    [Fact]
    public void A_closure_tenant_pass_refuses_the_sentinel_an_unassigned_tenant_a_system_subject_and_a_bad_membership_list()
    {
        var bounded = Enumerable.Range(0, DataRequestLimits.MaximumSnapshotMemberships).Select(_ => Guid.CreateVersion7()).ToArray();
        AuditRedactionPlan.ForClosureTenant(Tenant, Person, bounded).MembershipIds.Should().HaveCount(bounded.Length,
            "the bound an erasure's snapshot carries is admitted");

        var refusals = new (string Case, Func<AuditRedactionPlan> Build)[]
        {
            ("the platform sentinel", () => AuditRedactionPlan.ForClosureTenant(TenantId.PlatformSentinel, Person, [MembershipA])),
            ("the nil tenant", () => AuditRedactionPlan.ForClosureTenant(TenantId.From(Guid.Empty), Person, [MembershipA])),
            ("the system actor", () => AuditRedactionPlan.ForClosureTenant(Tenant, UserId.SystemActor, [MembershipA])),
            ("the nil subject", () => AuditRedactionPlan.ForClosureTenant(Tenant, UserId.From(Guid.Empty), [MembershipA])),
            ("no Membership", () => AuditRedactionPlan.ForClosureTenant(Tenant, Person, [])),
            ("a nil Membership", () => AuditRedactionPlan.ForClosureTenant(Tenant, Person, [MembershipA, Guid.Empty])),
            ("a duplicated Membership", () => AuditRedactionPlan.ForClosureTenant(Tenant, Person, [MembershipA, MembershipA])),
            ("one Membership past the bound", () => AuditRedactionPlan.ForClosureTenant(Tenant, Person, [.. bounded, Guid.CreateVersion7()])),
        };
        foreach (var (name, build) in refusals)
        {
            FluentActions.Invoking(build).Should().Throw<ArgumentException>(name);
        }
    }

    [Fact]
    public void A_closure_sentinel_pass_reaches_only_the_platform_sentinel_by_the_subject_key()
    {
        var plan = AuditRedactionPlan.ForClosureSentinel(Person, Key);

        plan.Pass.Should().Be(AuditRedactionPass.ClosureSentinel);
        (plan.TenantId, plan.OrganizationId, plan.WholeTenant, plan.SubjectUserId).Should().Be(
            (TenantId.PlatformSentinel, (OrganizationId?)null, true, Person));
        plan.MembershipIds.Should().BeEmpty("the sentinel tenant holds no Membership");
        plan.SubjectKey.Should().BeSameAs(Key);
        FluentActions.Invoking(() => plan.Subject).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_closure_sentinel_pass_refuses_a_system_or_nil_subject()
    {
        FluentActions.Invoking(() => AuditRedactionPlan.ForClosureSentinel(UserId.SystemActor, Key)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => AuditRedactionPlan.ForClosureSentinel(UserId.From(Guid.Empty), Key)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => AuditRedactionPlan.ForClosureSentinel(Person, null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void No_plan_and_no_aggregate_prints_what_it_concerns()
    {
        var plans = new[]
        {
            new AuditRedactionPlan(new UserDataSubject(Guid.CreateVersion7(), DataRequestKind.Erasure, DataRequestOrigin.Self, Person,
                new DataRequestScopeGroup(Tenant, null), [MembershipA])),
            AuditRedactionPlan.ForClosureTenant(Tenant, Person, [MembershipA]),
            AuditRedactionPlan.ForClosureSentinel(Person, Key),
        };

        plans.Select(plan => plan.ToString()).Should().AllBe(nameof(AuditRedactionPlan));
        new AuditRedactionAggregate(new Dictionary<TenantId, AuditRedactionReport> { [Tenant] = Report(1) }, Report(1))
            .ToString().Should().Be(nameof(AuditRedactionAggregate));
    }

    [Fact]
    public void The_aggregate_sums_every_pass_and_lists_each_tenant_s_rows_selected_in_tenant_order()
    {
        var first = TenantId.From(Guid.Parse("0190aaaa-0000-7000-8000-000000000001"));
        var second = TenantId.From(Guid.Parse("0190aaaa-0000-7000-8000-000000000002"));
        var aggregate = new AuditRedactionAggregate(
            new Dictionary<TenantId, AuditRedactionReport>
            {
                [second] = new(3, Columns(3, 3, 3, 1, 2, 0), Kinds(actor: 2, entity: 1, metadata: 4)),
                [first] = new(5, Columns(5, 4, 5, 2, 0, 1), Kinds(actor: 3, entity: 2, metadata: 0)),
            },
            new AuditRedactionReport(2, Columns(0, 0, 0, 0, 1, 0), Kinds(actor: 0, entity: 1, metadata: 2)));

        aggregate.ListsTenants.Should().BeTrue();
        aggregate.Tenants.Should().HaveCount(2);
        aggregate.Total.RowsSelected.Should().Be(10);
        Enum.GetValues<AuditRedactedColumn>().Select(column => aggregate.Total.Redacted[column]).Should().Equal(8, 7, 8, 3, 3, 1);
        aggregate.Total.Retained.Select(entry => (entry.Kind, entry.Count)).Should().Equal(
            (RetainedReferenceKind.AuditActorReference, 5L), (RetainedReferenceKind.AuditEntityReference, 4L),
            (RetainedReferenceKind.AuditMetadataReference, 6L));

        // Counts and tenant identifiers only, the tenants in identifier order, and the platform pass beside the total.
        var expected = """
            {"tenant_count":2,
             "rows_selected_by_tenant":{"0190aaaa-0000-7000-8000-000000000001":5,"0190aaaa-0000-7000-8000-000000000002":3},
             "platform":{"rows_selected":2,"redacted":{"ip_address":0,"user_agent":0,"actor_email":0,"before_state":0,"after_state":1,"changes":0},
               "retained":{"audit_actor_reference":0,"audit_entity_reference":1,"audit_metadata_reference":2}},
             "total":{"rows_selected":10,"redacted":{"ip_address":8,"user_agent":7,"actor_email":8,"before_state":3,"after_state":3,"changes":1},
               "retained":{"audit_actor_reference":5,"audit_entity_reference":4,"audit_metadata_reference":6}}}
            """;
        aggregate.ToClosedJson().Should().Be(JsonNode.Parse(expected)!.ToJsonString());
    }

    [Fact]
    public void An_aggregate_with_no_served_tenant_is_its_platform_pass()
    {
        var platform = new AuditRedactionReport(2, Columns(0, 0, 0, 0, 1, 0), Kinds(actor: 0, entity: 1, metadata: 2));

        var aggregate = new AuditRedactionAggregate(new Dictionary<TenantId, AuditRedactionReport>(), platform);

        aggregate.Total.ToClosedJson().Should().Be(platform.ToClosedJson());
        JsonNode.Parse(aggregate.ToClosedJson())!["rows_selected_by_tenant"]!.AsObject().Should().BeEmpty();
    }

    [Fact]
    public void Beyond_two_hundred_tenants_the_document_carries_the_count_and_the_totals_alone()
    {
        var listed = new AuditRedactionAggregate(Tenants(AuditRedactionAggregate.MaximumListedTenants, rows: 1), Report(1));
        var aggregated = new AuditRedactionAggregate(Tenants(AuditRedactionAggregate.MaximumListedTenants + 1, rows: 1), Report(1));

        listed.ListsTenants.Should().BeTrue();
        JsonNode.Parse(listed.ToClosedJson())!["rows_selected_by_tenant"]!.AsObject().Should().HaveCount(200);
        aggregated.ListsTenants.Should().BeFalse();
        var document = JsonNode.Parse(aggregated.ToClosedJson())!.AsObject();
        document.Select(member => member.Key).Should().Equal("tenant_count", "platform", "total");
        document["tenant_count"]!.GetValue<int>().Should().Be(201);
        document["total"]!["rows_selected"]!.GetValue<long>().Should().Be(202, "the totals still count every pass");
    }

    [Fact]
    public void The_document_stays_under_its_bound_at_the_listing_s_limit_with_every_kind_and_the_largest_counts()
    {
        // Each tenant's counts as large as two hundred can be and still sum, every column and all sixteen kinds present.
        var large = long.MaxValue / (AuditRedactionAggregate.MaximumListedTenants + 1);
        var every = Enum.GetValues<RetainedReferenceKind>().Select(kind => new RetainedReferenceCount(kind, large)).ToArray();
        var report = new AuditRedactionReport(large, Columns(large, large, large, large, large, large), every);
        var tenants = Enumerable.Range(0, AuditRedactionAggregate.MaximumListedTenants)
            .ToDictionary(_ => TenantId.From(Guid.CreateVersion7()), _ => report);

        var json = new AuditRedactionAggregate(tenants, report).ToClosedJson();

        Encoding.UTF8.GetByteCount(json).Should().BeLessThanOrEqualTo(AuditRedactionAggregate.MaximumClosedJsonBytes)
            .And.BeLessThan(32 * 1024, "the intent and result rows each hold at most 32 KiB of state");
    }

    [Fact]
    public void The_aggregate_refuses_the_sentinel_a_nil_tenant_a_missing_report_and_a_total_past_a_count()
    {
        var refusals = new (string Case, Func<AuditRedactionAggregate> Build)[]
        {
            ("the platform sentinel as a tenant pass", () => new(
                new Dictionary<TenantId, AuditRedactionReport> { [TenantId.PlatformSentinel] = Report(1) }, Report(1))),
            ("the nil tenant", () => new(new Dictionary<TenantId, AuditRedactionReport> { [TenantId.From(Guid.Empty)] = Report(1) }, Report(1))),
            ("a tenant without its report", () => new(new Dictionary<TenantId, AuditRedactionReport> { [Tenant] = null! }, Report(1))),
        };
        foreach (var (name, build) in refusals)
        {
            FluentActions.Invoking(build).Should().Throw<ArgumentException>(name);
        }

        FluentActions.Invoking(() => new AuditRedactionAggregate(
                new Dictionary<TenantId, AuditRedactionReport> { [Tenant] = Report(long.MaxValue) }, Report(1)))
            .Should().Throw<OverflowException>("a total that does not fit is refused, never wrapped");
    }

    private static Dictionary<TenantId, AuditRedactionReport> Tenants(int count, long rows) =>
        Enumerable.Range(0, count).ToDictionary(_ => TenantId.From(Guid.CreateVersion7()), _ => Report(rows));

    private static AuditRedactionReport Report(long rows) =>
        new(rows, Columns(rows, rows, rows, 0, 0, 0), Kinds(actor: rows, entity: 0, metadata: 0));

    private static Dictionary<AuditRedactedColumn, long> Columns(long ip, long agent, long email, long before, long after, long changes) => new()
    {
        [AuditRedactedColumn.IpAddress] = ip,
        [AuditRedactedColumn.UserAgent] = agent,
        [AuditRedactedColumn.ActorEmail] = email,
        [AuditRedactedColumn.BeforeState] = before,
        [AuditRedactedColumn.AfterState] = after,
        [AuditRedactedColumn.Changes] = changes,
    };

    private static RetainedReferenceCount[] Kinds(long actor, long entity, long metadata) =>
    [
        new(RetainedReferenceKind.AuditActorReference, actor),
        new(RetainedReferenceKind.AuditEntityReference, entity),
        new(RetainedReferenceKind.AuditMetadataReference, metadata),
    ];
}
