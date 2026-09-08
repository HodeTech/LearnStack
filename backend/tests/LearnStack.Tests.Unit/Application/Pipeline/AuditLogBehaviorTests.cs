using FluentAssertions;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Audit;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Pipeline;

/// <summary>
/// What the audit behavior does at pipeline step 3, and on every way out of it.
/// </summary>
/// <remarks>
/// The two guarantees the Packet 3 shell established are still here — the rethrow goes
/// through <c>ExceptionDispatchInfo</c> and the wrap order is untouched — and everything
/// else is new: the classification, the intents, and the reconcile that covers the exits
/// the pipeline cannot tell apart from the inside.
/// </remarks>
public sealed class AuditLogBehaviorTests
{
    public sealed record DummyCommand : IRequest<Result<string>>;

    [Fact]
    public async Task An_unregistered_request_is_refused_and_the_handler_never_runs()
    {
        // The fail-closed rule's first half. An operation nobody classified is a
        // deployment defect, and letting it commit while the corpus cannot say whether it
        // should have been audited is exactly what the rule prevents — so the refusal
        // happens BEFORE the handler, not after.
        var handlerRan = false;
        var store = new RecordingAuditStore();
        var behavior = Behavior(new FakeCatalog { Unregistered = true }, store);

        var result = await behavior.Handle(
            new DummyCommand(),
            () => { handlerRan = true; return Task.FromResult(Result.Ok("ok")); },
            default);

        handlerRan.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("audit_unclassified_operation");
        store.Standalone.Should().BeEmpty("nothing was classified, so there is nothing to record");
    }

    [Fact]
    public async Task A_request_registered_silent_runs_and_declares_nothing()
    {
        // Registered-and-silent is a DIFFERENT answer from unregistered: the first is a
        // decision, the second an omission, and only the second is refused.
        var capture = new AuditStateCapture();
        var store = new RecordingAuditStore();
        var behavior = Behavior(new FakeCatalog { Silent = true }, store, capture);

        var result = await behavior.Handle(
            new DummyCommand(), () => Task.FromResult(Result.Ok("ok")), default);

        result.IsSuccess.Should().BeTrue();
        capture.Intents.Should().BeEmpty();
        store.Standalone.Should().BeEmpty();
        store.BestEffort.Should().BeEmpty();
    }

    [Fact]
    public async Task One_request_declares_one_intent_per_audited_operation()
    {
        // Plural, and the reason is ProvisionTenantCommand: two aggregate roots on one
        // transaction, both classified MUST. Under a singular reading the Organization row
        // the Tenancy matrix promises would simply never be written.
        var capture = new AuditStateCapture();
        var catalog = new FakeCatalog(
            AuditPipelineHarness.Entry("tenancy.tenant.create"),
            AuditPipelineHarness.Entry("tenancy.organization.create"));

        var behavior = Behavior(catalog, new RecordingAuditStore(), capture);
        var seen = Array.Empty<string>();

        await behavior.Handle(
            new DummyCommand(),
            () =>
            {
                // Read INSIDE the handler: the intents must exist before the work does, or
                // a handler that throws leaves nothing to record.
                seen = [.. capture.Intents.Select(intent => intent.Operation)];
                capture.MarkCommitted();

                return Task.FromResult(Result.Ok("ok"));
            },
            default);

        seen.Should().Equal("tenancy.tenant.create", "tenancy.organization.create");
    }

    [Fact]
    public async Task An_override_that_silences_an_operation_declares_no_intent()
    {
        // The only thing an override does. It narrows and never elevates
        // (ADR-0033 Amendment 4 § 1).
        var capture = new AuditStateCapture();
        var behavior = Behavior(
            new FakeCatalog(AuditPipelineHarness.Entry(operationClass: OperationClass.Should)),
            new RecordingAuditStore(),
            capture,
            new FakeClassifier(AuditClassification.Off));

        await behavior.Handle(new DummyCommand(), () => Task.FromResult(Result.Ok("ok")), default);

        capture.Intents.Should().BeEmpty();
    }

    [Fact]
    public async Task A_committed_MUST_row_is_not_written_a_second_time()
    {
        // It went with the business write. Re-writing it would produce the duplicate the
        // composite key exists to make legal — for an operation that never was in doubt.
        var capture = new AuditStateCapture();
        var store = new RecordingAuditStore();
        var behavior = Behavior(new FakeCatalog(AuditPipelineHarness.Entry()), store, capture);

        await behavior.Handle(
            new DummyCommand(),
            () => { capture.MarkCommitted(); return Task.FromResult(Result.Ok("ok")); },
            default);

        store.Standalone.Should().BeEmpty();
        store.BestEffort.Should().BeEmpty();
    }

    [Fact]
    public async Task A_rolled_back_MUST_row_is_re_written_standalone()
    {
        // The attempt is still the record. The transaction took the row with it, and the
        // reconcile is what keeps the operation from vanishing.
        var capture = new AuditStateCapture();
        var store = new RecordingAuditStore();
        var behavior = Behavior(new FakeCatalog(AuditPipelineHarness.Entry()), store, capture);

        await behavior.Handle(
            new DummyCommand(),
            () => { capture.MarkRolledBack(); return Task.FromResult(Result.Ok("ok")); },
            default);

        store.Standalone.Should().ContainSingle()
            .Which.Outcome.Should().Be(AuditOutcome.Failed);
    }

    [Fact]
    public async Task An_indeterminate_commit_prefers_a_duplicate_to_a_loss()
    {
        // The COMMIT's fate is unknown, so the row goes out anyway with a FRESH timestamp
        // — and the 23505 that may follow is positive evidence the first one landed.
        var capture = new AuditStateCapture();
        var store = new RecordingAuditStore();
        var behavior = Behavior(new FakeCatalog(AuditPipelineHarness.Entry()), store, capture);

        await behavior.Handle(
            new DummyCommand(),
            () =>
            {
                capture.MarkIndeterminate(new InvalidOperationException("commit in doubt"));
                return Task.FromResult(Result.Ok("ok"));
            },
            default);

        store.Standalone.Should().ContainSingle()
            .Which.Outcome.Should().Be(AuditOutcome.Indeterminate);
    }

    [Theory]
    // The three codes HttpStatusMap answers 403 to, so the row's outcome and the
    // response's status cannot disagree. `denied` is the value Audit Coverage justifies
    // the whole class with: a probe is visible only as a series of denials.
    [InlineData("forbidden", AuditOutcome.Denied)]
    [InlineData("resource_scope_violation", AuditOutcome.Denied)]
    [InlineData("feature_disabled", AuditOutcome.Denied)]
    // A malformed request is not a refused one, and counting it as a denial would bury
    // the probes the denied class exists to surface.
    [InlineData("validation_failed", AuditOutcome.Failed)]
    [InlineData("business_rule_violation", AuditOutcome.Failed)]
    public async Task A_refusal_is_recorded_with_the_outcome_its_status_implies(
        string code, AuditOutcome expected)
    {
        var capture = new AuditStateCapture();
        var store = new RecordingAuditStore();
        var behavior = Behavior(new FakeCatalog(AuditPipelineHarness.Entry()), store, capture);

        var refusal = new Error(new LocalizedMessage(LocalizedMessage.RequiredPrefix + code));

        var result = await behavior.Handle(
            new DummyCommand(),
            () => { capture.MarkRolledBack(); return Task.FromResult(Result.Fail<string>(refusal)); },
            default);

        result.Error!.Code.Should().Be(code, "the caller keeps its own refusal");
        var row = store.Standalone.Should().ContainSingle().Subject;
        row.Outcome.Should().Be(expected);
        row.ErrorKey.Should().Be(refusal.Message.Key);
    }

    [Fact]
    public async Task A_failed_audit_write_does_not_change_a_refusal_into_a_503()
    {
        // ADR-0033 Amendment 1 narrows the fail-closed rule: a standalone write failure
        // changes the response only when the operation would otherwise have SUCCEEDED.
        // Turning a refusal into a 503 an anonymous caller can provoke tells them more,
        // not less.
        var capture = new AuditStateCapture();
        var store = new RecordingAuditStore { StandaloneFails = true };
        var behavior = Behavior(new FakeCatalog(AuditPipelineHarness.Entry()), store, capture);

        var refusal = new Error(new LocalizedMessage(LocalizedMessage.RequiredPrefix + "forbidden"));

        var result = await behavior.Handle(
            new DummyCommand(),
            () => { capture.MarkRolledBack(); return Task.FromResult(Result.Fail<string>(refusal)); },
            default);

        result.Error!.Code.Should().Be("forbidden");
    }

    [Fact]
    public async Task A_handler_exception_is_recorded_and_rethrown_by_reference()
    {
        // The Packet 3 guarantee, still here: ExceptionDispatchInfo rethrows the original
        // instance, so handlers and the L1 boundary see the original stack rather than a
        // wrapper.
        var capture = new AuditStateCapture();
        var store = new RecordingAuditStore();
        var behavior = Behavior(new FakeCatalog(AuditPipelineHarness.Entry()), store, capture);

        var thrown = new InvalidOperationException("boom");

        var act = async () => await behavior.Handle(
            new DummyCommand(),
            () => { capture.MarkRolledBack(); throw thrown; },
            default);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Should().BeSameAs(thrown);

        store.Standalone.Should().ContainSingle()
            .Which.Outcome.Should().Be(AuditOutcome.Failed);
    }

    [Fact]
    public async Task A_cancelled_commit_still_reconciles_and_still_clears()
    {
        // The gap ADR-0033 Amendment 4 § 2 closes. The catch filter excludes
        // OperationCanceledException so the type survives for ADR-0032 — and until the
        // reconcile moved into the finally, that also meant a client disconnecting
        // mid-COMMIT dropped a MUST-class row for an operation that may well have
        // committed, and left the buffer uncleared for the next request in the scope.
        var capture = new AuditStateCapture();
        var store = new RecordingAuditStore();
        var behavior = Behavior(new FakeCatalog(AuditPipelineHarness.Entry()), store, capture);

        var act = async () => await behavior.Handle(
            new DummyCommand(),
            () =>
            {
                capture.MarkIndeterminate(new OperationCanceledException());
                throw new OperationCanceledException();
            },
            default);

        await act.Should().ThrowAsync<OperationCanceledException>();

        store.Standalone.Should().ContainSingle()
            .Which.Outcome.Should().Be(AuditOutcome.Indeterminate);
        capture.Intents.Should().BeEmpty("the outermost frame clears in its finally");
    }

    [Fact]
    public async Task A_SHOULD_class_row_is_written_best_effort_and_never_in_transaction()
    {
        // WritePendingAsync flushes MUST alone, so a SHOULD row has no transaction to ride
        // and the reconcile writes it — with the opposite failure posture, because the
        // accepted loss is written down in the module's matrix.
        var capture = new AuditStateCapture();
        var store = new RecordingAuditStore();
        var behavior = Behavior(
            new FakeCatalog(AuditPipelineHarness.Entry(operationClass: OperationClass.Should)),
            store,
            capture);

        await behavior.Handle(
            new DummyCommand(),
            () => { capture.MarkCommitted(); return Task.FromResult(Result.Ok("ok")); },
            default);

        store.Standalone.Should().BeEmpty();
        store.BestEffort.Should().ContainSingle()
            .Which.Outcome.Should().Be(AuditOutcome.Success);
    }

    [Fact]
    public async Task A_nested_frame_does_not_clear_the_outer_request_buffer()
    {
        // A joiner that cleared would erase the outer request's intents and every snapshot
        // before the owner had committed (ADR-0033 Amendment 2 § 2).
        var capture = new AuditStateCapture();
        var store = new RecordingAuditStore();
        var outer = Behavior(new FakeCatalog(AuditPipelineHarness.Entry()), store, capture);
        var inner = Behavior(
            new FakeCatalog(AuditPipelineHarness.Entry("tenancy.organization.create")), store, capture);

        var insideOuterAfterInner = 0;

        await outer.Handle(
            new DummyCommand(),
            async () =>
            {
                await inner.Handle(
                    new DummyCommand(),
                    () => { capture.MarkCommitted(); return Task.FromResult(Result.Ok("inner")); },
                    default);

                insideOuterAfterInner = capture.Intents.Count;

                return Result.Ok("outer");
            },
            default);

        insideOuterAfterInner.Should().Be(2,
            "the inner frame is a joiner and clears nothing");
        capture.Intents.Should().BeEmpty("the outer frame clears when it finishes");
    }

    private static AuditLogBehavior<DummyCommand, Result<string>> Behavior(
        IAuditCatalog catalog,
        RecordingAuditStore store,
        AuditStateCapture? capture = null,
        IAuditConfigService? classifier = null) =>
        new(
            catalog,
            classifier ?? new FakeClassifier(),
            capture ?? new AuditStateCapture(),
            store,
            new HarnessTenantContext(),
            new HarnessClock(DateTimeOffset.UnixEpoch),
            new HarnessGuidFactory(),
            NullLogger<AuditLogBehavior<DummyCommand, Result<string>>>.Instance);
}
