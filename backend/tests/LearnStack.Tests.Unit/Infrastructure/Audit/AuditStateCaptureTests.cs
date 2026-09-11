using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Audit;

/// <summary>
/// The scoped buffer, and the one thing it must never do: infer its own state.
/// </summary>
public sealed class AuditStateCaptureTests
{
    [Fact]
    public void A_fresh_capture_has_declared_nothing()
    {
        var capture = new AuditStateCapture();

        capture.State.Should().Be(AuditIntentState.None);
        capture.Intents.Should().BeEmpty();
        capture.Changes.Should().BeEmpty();
        capture.IndeterminateCause.Should().BeNull();
    }

    [Fact]
    public void Intents_are_plural_and_keep_declaration_order()
    {
        // The correction ADR-0033 Amendment 2 makes: one intent per audited
        // (resource, operation), not one per request. ProvisionTenantCommand writes two
        // aggregate roots on one transaction and the Tenancy matrix classifies both MUST,
        // so under the singular reading the Organization row would never exist.
        var capture = new AuditStateCapture();

        capture.DeclareIntent(Intent("tenancy.tenant.create"));
        capture.DeclareIntent(Intent("tenancy.organization.create"));

        capture.Intents.Select(intent => intent.Operation).Should().Equal(
            "tenancy.tenant.create", "tenancy.organization.create");
        capture.State.Should().Be(AuditIntentState.Pending);
    }

    [Fact]
    public void A_second_declaration_after_the_write_does_not_reopen_the_state()
    {
        // A joiner declares its own intent after the owner has already written. Moving
        // the state back to Pending there would tell the reconcile step nothing had been
        // written, and it would write the owner's rows a second time.
        var capture = new AuditStateCapture();

        capture.DeclareIntent(Intent("tenancy.tenant.create"));
        capture.MarkWrittenInTransaction();
        capture.DeclareIntent(Intent("tenancy.organization.create"));

        capture.State.Should().Be(AuditIntentState.WrittenInTransaction);
        capture.Intents.Should().HaveCount(2, "the joiner's intent is still recorded");
    }

    [Fact]
    public void WrittenInTransaction_is_not_durable_and_Committed_is()
    {
        // The distinction the whole type exists for. A per-request flag cannot observe a
        // rollback — the flag lives in a DI scope and a rollback does not touch it — so
        // only the owning frame's MarkCommitted means the row is there.
        var capture = new AuditStateCapture();
        capture.DeclareIntent(Intent("tenancy.tenant.create"));

        capture.MarkWrittenInTransaction();
        capture.State.Should().Be(AuditIntentState.WrittenInTransaction);

        capture.MarkCommitted();
        capture.State.Should().Be(AuditIntentState.Committed);
    }

    [Fact]
    public void A_rollback_leaves_the_intents_and_the_snapshots_intact()
    {
        // Because that is what the reconcile step re-writes from. A buffer that cleared
        // itself on rollback would lose the record of an attempt, which is the case
        // ADR-0033 says must survive.
        var capture = new AuditStateCapture();
        capture.DeclareIntent(Intent("tenancy.tenant.create"));
        capture.Add(new CapturedEntityChange("Tenant", "t-1", "{}", "{}", []));

        capture.MarkWrittenInTransaction();
        capture.MarkRolledBack();

        capture.State.Should().Be(AuditIntentState.RolledBack);
        capture.Intents.Should().HaveCount(1);
        capture.Changes.Should().HaveCount(1);
    }

    [Fact]
    public void An_indeterminate_commit_keeps_its_cause()
    {
        var cause = new InvalidOperationException("connection reset during COMMIT");
        var capture = new AuditStateCapture();
        capture.DeclareIntent(Intent("tenancy.tenant.create"));

        capture.MarkIndeterminate(cause);

        capture.State.Should().Be(AuditIntentState.Indeterminate);
        capture.IndeterminateCause.Should().BeSameAs(cause);
    }

    [Fact]
    public void Clear_returns_it_to_the_state_a_fresh_request_starts_in()
    {
        // Called once, by the outermost behavior, in its finally. Anything left behind is
        // one request's snapshot arriving in the next request's audit row.
        var capture = new AuditStateCapture();
        capture.DeclareIntent(Intent("tenancy.tenant.create"));
        capture.Add(new CapturedEntityChange("Tenant", "t-1", "{}", "{}", []));
        capture.MarkIndeterminate(new InvalidOperationException("boom"));

        capture.Clear();

        capture.State.Should().Be(AuditIntentState.None);
        capture.Intents.Should().BeEmpty();
        capture.Changes.Should().BeEmpty();
        capture.IndeterminateCause.Should().BeNull();
    }

    [Fact]
    public void It_refuses_a_null_intent_change_or_cause()
    {
        var capture = new AuditStateCapture();

        FluentActions.Invoking(() => capture.DeclareIntent(null!))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => capture.Add(null!))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => capture.MarkIndeterminate(null!))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Each_frame_records_its_own_result_on_the_intents_it_declared()
    {
        // The outcome of a request and the fate of its transaction are different facts.
        // An inner request refused under an outer one that succeeds keeps the refusal; the
        // outer's intent does not inherit it, and neither is overwritten by the other
        // (ADR-0044 Amendment 6 § 3).
        var capture = new AuditStateCapture();
        var refused = AuditIntentResult.Refused(AuditOutcome.Denied, "lockey_forbidden");

        capture.OpenFrame();
        capture.DeclareIntent(Intent("tenancy.tenant.create"));

        capture.OpenFrame();
        capture.DeclareIntent(Intent("tenancy.organization.create"));
        capture.CloseFrame(refused);

        capture.Intents[0].Result.Should().BeNull("the outer frame is still open");
        capture.Intents[1].Result.Should().BeSameAs(refused);

        capture.CloseFrame(AuditIntentResult.Succeeded);

        capture.Intents[0].Result.Should().BeSameAs(AuditIntentResult.Succeeded);
        capture.Intents[1].Result.Should().BeSameAs(refused, "closing the outer frame leaves the inner's alone");
    }

    [Fact]
    public void A_capture_belongs_to_the_frame_whose_handler_flushed_it()
    {
        var capture = new AuditStateCapture();

        capture.OpenFrame();
        capture.DeclareIntent(Intent("tenancy.tenant.create"));
        capture.Add(Change("outer"));

        capture.OpenFrame();
        capture.DeclareIntent(Intent("tenancy.organization.create"));
        capture.Add(Change("inner"));
        capture.CloseFrame(AuditIntentResult.Succeeded);

        capture.Add(Change("outer-again"));

        capture.ChangesOf(capture.Intents[0]).Select(change => change.EntityId)
            .Should().Equal("outer", "outer-again");
        capture.ChangesOf(capture.Intents[1]).Select(change => change.EntityId)
            .Should().Equal("inner");
        capture.Changes.Should().HaveCount(3, "Changes is still every capture in the request");
    }

    [Fact]
    public void With_no_frame_open_everything_belongs_to_one_request_frame()
    {
        // What a caller that never opens a frame sees — the store's own cases, a tool that
        // writes outside the pipeline — is exactly the single-request picture it had before.
        var capture = new AuditStateCapture();
        capture.DeclareIntent(Intent("tenancy.tenant.create"));
        capture.Add(Change("t-1"));

        capture.ChangesOf(capture.Intents.Single()).Should().ContainSingle();
    }

    [Fact]
    public void A_copy_of_an_intent_from_before_its_frame_closed_is_refused()
    {
        // Closing a frame replaces its intents with copies carrying the result. A caller
        // composing from a copy it kept would compose from a stale declaration.
        var capture = new AuditStateCapture();
        var declared = Intent("tenancy.tenant.create");

        capture.OpenFrame();
        capture.DeclareIntent(declared);
        capture.CloseFrame(AuditIntentResult.Succeeded);

        FluentActions.Invoking(() => capture.ChangesOf(declared))
            .Should().Throw<InvalidOperationException>().WithMessage("*Read it from Intents*");
        capture.ChangesOf(capture.Intents.Single()).Should().BeEmpty();
    }

    [Fact]
    public void A_designation_binds_the_innermost_frame_s_intents_of_its_type_and_nothing_else()
    {
        var capture = new AuditStateCapture();

        capture.OpenFrame();
        capture.DeclareIntent(Intent("customization.content_type.publish", typeof(Probe)));

        capture.OpenFrame();
        capture.DeclareIntent(Intent("customization.content_type.publish", typeof(Probe)));
        capture.DeclareIntent(Intent("tenancy.tenant.create", typeof(string)));
        capture.Designate(typeof(Probe), "inner-subject");

        capture.Intents[0].SubjectId.Should().BeNull("a nested request's designation never reaches its parent");
        capture.Intents[1].SubjectId.Should().Be("inner-subject");
        capture.Intents[2].SubjectId.Should().BeNull("an intent about another type is not bound");

        capture.CloseFrame(AuditIntentResult.Succeeded);
        capture.Designate(typeof(Probe), "outer-subject");

        capture.Intents[0].SubjectId.Should().Be("outer-subject");
        capture.Intents[1].SubjectId.Should().Be("inner-subject");
    }

    [Fact]
    public void Designating_a_second_instance_is_a_programming_error_and_the_same_one_is_not()
    {
        var capture = new AuditStateCapture();
        capture.OpenFrame();
        capture.DeclareIntent(Intent("customization.content_type.publish", typeof(Probe)));

        capture.Designate(typeof(Probe), "one");
        capture.Designate(typeof(Probe), "one");

        FluentActions.Invoking(() => capture.Designate(typeof(Probe), "two"))
            .Should().Throw<InvalidOperationException>().WithMessage("*already designated*");
        capture.Intents.Single().SubjectId.Should().Be("one");
    }

    [Fact]
    public void Closing_a_frame_that_was_never_opened_is_refused_and_Clear_closes_them_all()
    {
        var capture = new AuditStateCapture();

        FluentActions.Invoking(() => capture.CloseFrame(AuditIntentResult.Succeeded))
            .Should().Throw<InvalidOperationException>();

        capture.OpenFrame();
        capture.OpenFrame();
        capture.Clear();

        FluentActions.Invoking(() => capture.CloseFrame(AuditIntentResult.Succeeded))
            .Should().Throw<InvalidOperationException>("Clear leaves no frame open for the next request");
    }

    private sealed class Probe;

    private static CapturedEntityChange Change(string id) => new(nameof(Probe), id, null, "{}", []);

    private static AuditIntent Intent(string operation, Type? entityType = null) =>
        new(
            AuditEntryId.From(Guid.CreateVersion7()),
            TenantId.From(Guid.CreateVersion7()),
            OrganizationId: null,
            ActorUserId: null,
            CorrelationId: null,
            ModuleName: "tenancy",
            Operation: operation,
            OperationType: OperationType.Create,
            OperationClass: OperationClass.Must,
            EntityType: entityType,
            DeclaredAt: DateTimeOffset.UnixEpoch);
}
