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

    private static AuditIntent Intent(string operation) =>
        new(
            AuditEntryId.From(Guid.CreateVersion7()),
            TenantId.From(Guid.CreateVersion7()),
            OrganizationId: null,
            ModuleName: "tenancy",
            Operation: operation,
            OperationType: OperationType.Create,
            OperationClass: OperationClass.Must,
            EntityType: null,
            DeclaredAt: DateTimeOffset.UnixEpoch);
}
