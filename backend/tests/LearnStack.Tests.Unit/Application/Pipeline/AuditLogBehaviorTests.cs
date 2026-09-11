using FluentAssertions;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Audit;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.Logging;
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
    public async Task A_silent_request_still_owns_the_frame_a_nested_audited_request_joins()
    {
        // Silent is not frameless. The outer request's TransactionBehavior owns the unit of
        // work whatever its audit registration says, so it is the outer frame that drains the
        // scope's intents on its way out — and an inner audited request that believed itself
        // outermost reconciled BEFORE the owner committed, wrote a `failed` row for an
        // operation about to commit, and cleared the intent the owner was to write. Measured:
        // skipping the frame for a silent request left every other unit case green.
        var capture = new AuditStateCapture();
        var store = new RecordingAuditStore();
        var outer = Behavior(new FakeCatalog { Silent = true }, store, capture);
        var inner = Behavior(new FakeCatalog(AuditPipelineHarness.Entry()), store, capture);

        await outer.Handle(
            new DummyCommand(),
            async () =>
            {
                await inner.Handle(
                    new DummyCommand(), () => Task.FromResult(Result.Ok("inner")), default);

                capture.Intents.Should().ContainSingle(
                    "the inner request joined the silent one's frame and left its intent to the owner");
                store.Standalone.Should().BeEmpty("a joiner reconciles nothing");

                capture.MarkCommitted();

                return Result.Ok("outer");
            },
            default);

        store.Standalone.Should().BeEmpty("the inner MUST row committed with the owner's transaction");
        capture.Intents.Should().BeEmpty("and the silent owner cleared on its way out");
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
        var store = new RecordingAuditStore();
        var behavior = Behavior(
            new FakeCatalog(AuditPipelineHarness.Entry(operationClass: OperationClass.Should)),
            store,
            capture,
            new FakeClassifier(AuditClassification.Off));

        var declaredInside = -1;

        await behavior.Handle(
            new DummyCommand(),
            () =>
            {
                // Read INSIDE the handler. The finally clears the buffer for the outermost
                // frame whatever happened, so an assertion afterwards is true for ANY
                // successful call and says nothing about the Off skip.
                declaredInside = capture.Intents.Count;

                capture.MarkCommitted();

                return Task.FromResult(Result.Ok("ok"));
            },
            default);

        declaredInside.Should().Be(0, "an Off classification declares no intent");
        store.Standalone.Should().BeEmpty();
        store.BestEffort.Should().BeEmpty("a silenced operation leaves no row anywhere");
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
    // the probes the denied class exists to surface. The validation_failed that reaches
    // this step is a handler's — ADR-0043's payload gates return it; step 1's never does,
    // because validation runs outside the audit step.
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

        // The request's token is ALREADY CANCELLED, which is the whole shape of the case
        // and what the earlier version of it got wrong: it handed Handle a live token, so
        // it asserted the reconcile's shape rather than its behaviour and stayed green
        // while every cancelled request lost its row.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = async () => await behavior.Handle(
            new DummyCommand(),
            () =>
            {
                capture.MarkIndeterminate(new OperationCanceledException());
                throw new OperationCanceledException();
            },
            cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        store.Abandoned.Should().Be(0,
            "the reconcile must not be abandoned by the token that cancelled the request");
        store.Standalone.Should().ContainSingle()
            .Which.Outcome.Should().Be(AuditOutcome.Indeterminate);
        capture.Intents.Should().BeEmpty("the outermost frame clears in its finally");
    }

    [Fact]
    public async Task A_cancelled_handler_still_records_the_attempt()
    {
        // The commoner half, and it fails the same way. A client that aborts mid-handler
        // leaves the state RolledBack, and ADR-0033 § Decision promises the row is
        // re-written standalone with outcome `failed` — which a reconcile abandoned by the
        // request's own token never writes.
        var capture = new AuditStateCapture();
        var store = new RecordingAuditStore();
        var behavior = Behavior(new FakeCatalog(AuditPipelineHarness.Entry()), store, capture);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = async () => await behavior.Handle(
            new DummyCommand(),
            () =>
            {
                capture.MarkRolledBack();
                throw new OperationCanceledException();
            },
            cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        store.Abandoned.Should().Be(0);
        store.Standalone.Should().ContainSingle()
            .Which.Outcome.Should().Be(AuditOutcome.Failed);
    }

    [Fact]
    public async Task A_cancelled_classification_still_reconciles_what_was_declared_and_clears()
    {
        // The lifecycle Audit Subsystem § 5 documents, pinned: the declaration runs INSIDE
        // the block the finally guards. ClassifyAsync can be cancelled — on a cache miss it
        // opens a transaction of its own — and a declaration above the try leaves by a door
        // nothing guards: the first operation's intent declared and never written, its frame
        // never closed, the buffer never cleared. Measured: moving the declaration above the
        // try left every other case in this class green.
        var capture = new AuditStateCapture();
        var store = new RecordingAuditStore();
        var behavior = Behavior(
            new FakeCatalog(
                AuditPipelineHarness.Entry("tenancy.tenant.create"),
                AuditPipelineHarness.Entry("tenancy.organization.create")),
            store,
            capture,
            new CancelledOnSecondClassifier());

        var handlerRan = false;

        var act = async () => await behavior.Handle(
            new DummyCommand(),
            () => { handlerRan = true; return Task.FromResult(Result.Ok("ok")); },
            default);

        await act.Should().ThrowAsync<OperationCanceledException>();

        handlerRan.Should().BeFalse();

        var row = store.Standalone.Should().ContainSingle(
            "the intent declared before the cancellation is an attempt, and the attempt is the record")
            .Which;

        row.Operation.Should().Be("tenancy.tenant.create");
        row.Outcome.Should().Be(AuditOutcome.Failed, "nothing committed and nothing succeeded");
        capture.Intents.Should().BeEmpty("the outermost frame clears on this way out too");
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
    public async Task A_cancelled_request_still_writes_its_SHOULD_row_best_effort()
    {
        // The CancellationToken.None fix has TWO call sites and only the MUST one was
        // covered: both cancellation cases above drive WriteStandaloneAsync, so passing
        // the request's own token to WriteBestEffortAsync instead left them all green.
        //
        // Best effort is not no effort. The accepted loss is a DATABASE failure, written
        // down in the module's coverage matrix; a row dropped because the caller hung up
        // is not that loss, and it is the one class of row the reconcile exists to rescue.
        var capture = new AuditStateCapture();
        var store = new RecordingAuditStore();
        var behavior = Behavior(
            new FakeCatalog(AuditPipelineHarness.Entry(operationClass: OperationClass.Should)),
            store,
            capture);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = async () => await behavior.Handle(
            new DummyCommand(),
            () =>
            {
                capture.MarkRolledBack();
                throw new OperationCanceledException();
            },
            cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        store.Abandoned.Should().Be(0,
            "the reconcile hands the write a token of its own, not the one that is already cancelled");
        store.BestEffort.Should().ContainSingle()
            .Which.Outcome.Should().Be(AuditOutcome.Failed);
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

    [Fact]
    public async Task A_reconciled_row_carries_the_snapshot_the_request_captured()
    {
        // The two composers had diverged: only the in-transaction row carried a snapshot,
        // so every denied, failed and indeterminate row said nothing about what was
        // attempted — the class of row ADR-0033 calls the common case it protects, and the
        // one an investigator reads first.
        var capture = new AuditStateCapture();
        var store = new RecordingAuditStore();
        var behavior = Behavior(
            new FakeCatalog(AuditPipelineHarness.Entry(entityType: typeof(Subject))), store, capture);

        await behavior.Handle(
            new DummyCommand(),
            () =>
            {
                // INSIDE the handler, which is the only place a flush happens: a capture
                // belongs to the request whose handler wrote it.
                capture.Add(SubjectInsert("t-7", "acme"));
                capture.MarkRolledBack();

                return Task.FromResult(Result.Ok("ok"));
            },
            default);

        var row = store.Standalone.Should().ContainSingle().Subject;

        row.EntityType.Should().Be(nameof(Subject));
        row.EntityId.Should().Be("t-7");
        row.BeforeState.Should().BeNull("the capture is an insert, and null is what says so");
        row.AfterState.Should().Contain("acme");
        row.Changes.Should().Contain("/Subject/t-7/slug");
    }

    [Fact]
    public async Task A_reconciled_row_takes_a_fresh_instant_and_not_the_intents()
    {
        // Two rows under one AuditEntryId are legal only because their timestamps differ.
        // Reusing DeclaredAt here would make the commit-in-doubt re-write raise 23505 —
        // which the store reads as positive evidence the first row is durable, so the
        // failure would report the opposite of what happened.
        var capture = new AuditStateCapture();
        var store = new RecordingAuditStore();
        var behavior = Behavior(new FakeCatalog(AuditPipelineHarness.Entry()), store, capture);

        await behavior.Handle(
            new DummyCommand(),
            () =>
            {
                capture.MarkIndeterminate(new InvalidOperationException("in doubt"));
                return Task.FromResult(Result.Ok("ok"));
            },
            default);

        var row = store.Standalone.Should().ContainSingle().Subject;

        // The harness clock advances one tick per read, so DeclaredAt is the FIRST reading
        // and the reconcile's is a later one. A frozen clock made the two spellings
        // indistinguishable and every assertion here vacuous.
        row.Timestamp.Should().BeAfter(
            DateTimeOffset.UnixEpoch.AddMilliseconds(1),
            "the reconcile reads the clock again rather than replaying the declaration");
    }

    [Fact]
    public async Task An_unresolved_request_that_is_not_provisioning_writes_no_row()
    {
        // The fourth of ADR-0044 § 2's cases, and the only one that writes nothing: there
        // is no tenant whose admin could read the row, and ADR-0036 forbids inventing one.
        // The request still runs — it is classified, so it is not the unregistered
        // rejection — it simply leaves no record, which is the honest answer when there is
        // no tenant to attribute it to.
        var capture = new AuditStateCapture();
        var store = new RecordingAuditStore();
        var handlerRan = false;

        var behavior = new AuditLogBehavior<DummyCommand, Result<string>>(
            new FakeCatalog(AuditPipelineHarness.Entry()),
            new FakeClassifier(),
            capture,
            store,
            new HarnessTenantContext(resolved: false),
            new HarnessClock(DateTimeOffset.UnixEpoch),
            new HarnessGuidFactory(),
            NullLogger<AuditLogBehavior<DummyCommand, Result<string>>>.Instance);

        var result = await behavior.Handle(
            new DummyCommand(),
            () =>
            {
                handlerRan = true;
                capture.MarkRolledBack();

                return Task.FromResult(Result.Ok("ok"));
            },
            default);

        handlerRan.Should().BeTrue("a classified request is not refused for lacking a tenant");
        result.IsSuccess.Should().BeTrue();
        store.Standalone.Should().BeEmpty();
        store.BestEffort.Should().BeEmpty();
    }

    [Theory]
    [InlineData("success")]
    [InlineData("refusal")]
    [InlineData("exception")]
    public async Task AuditStateCapture_ClearedPerRequest(string ending)
    {
        // The buffer is SCOPED — its lifetime is the request, not the transaction — so
        // anything it still holds when the request ends is state the next request
        // inherits: another tenant's snapshots, and intents that would be reconciled a
        // second time under an outcome that is not theirs.
        //
        // Three endings, listed rather than represented by one, because each leaves the
        // behaviour by a different door and only the `finally` is common to all of them.
        var capture = new AuditStateCapture();
        var store = new RecordingAuditStore();
        var behavior = Behavior(new FakeCatalog(AuditPipelineHarness.Entry()), store, capture);

        var act = async () => await behavior.Handle(
            new DummyCommand(),
            () =>
            {
                capture.MarkCommitted();

                return ending switch
                {
                    "refusal" => Task.FromResult(Result.Fail<string>(new Error(
                        new LocalizedMessage(
                            LocalizedMessage.RequiredPrefix + "business_rule_violation")))),
                    "exception" => throw new InvalidOperationException("handler failed"),
                    _ => Task.FromResult(Result.Ok("ok")),
                };
            },
            default);

        if (ending == "exception")
        {
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        else
        {
            await act.Should().NotThrowAsync();
        }

        capture.Intents.Should().BeEmpty($"a {ending} leaves nothing for the next request");
        capture.Changes.Should().BeEmpty();
        capture.State.Should().Be(AuditIntentState.None);
    }

    [Fact]
    public async Task A_joiner_never_clears_the_buffer_it_did_not_open()
    {
        // The half that matters under nesting: a joiner that cleared would erase the outer
        // request's intents and every snapshot BEFORE the owner committed. With the case
        // above — which proves the outermost frame DOES clear — the pair pins "exactly
        // once, by the outermost" rather than merely "at least once".
        var capture = new AuditStateCapture();
        var store = new RecordingAuditStore();
        var behavior = Behavior(new FakeCatalog(AuditPipelineHarness.Entry()), store, capture);

        await behavior.Handle(
            new DummyCommand(),
            async () =>
            {
                await behavior.Handle(
                    new DummyCommand(),
                    () => Task.FromResult(Result.Ok("inner")),
                    default);

                capture.Intents.Should().NotBeEmpty(
                    "the joiner returned and must have left the outer buffer alone");

                capture.MarkCommitted();

                return Result.Ok("outer");
            },
            default);

        capture.Intents.Should().BeEmpty("and the OWNER cleared on its way out");
    }

    [Fact]
    public async Task An_absorbed_inner_refusal_keeps_its_own_outcome_on_the_business_transaction()
    {
        // ADR-0040 § Nesting sanctions it: an inner request is refused, the outer handler
        // absorbs the refusal and succeeds, and the transaction commits. The outcome lived in
        // a local of each frame and only the owner's reached the write, so the owner wrote
        // every pending MUST row as success — the inner refusal was permanently recorded as
        // having succeeded, with no error key (ADR-0044 Amendment 6 § 3).
        var capture = new AuditStateCapture();
        var store = new RecordingAuditStore();
        var outer = Behavior(new FakeCatalog(AuditPipelineHarness.Entry("tenancy.tenant.create")), store, capture);
        var inner = Behavior(
            new FakeCatalog(AuditPipelineHarness.Entry("tenancy.organization.create")), store, capture);

        var forbidden = new Error(new LocalizedMessage(LocalizedMessage.RequiredPrefix + "forbidden"));
        AuditEntryDraft[] inTransaction = [];

        await outer.Handle(
            new DummyCommand(),
            async () =>
            {
                var refused = await inner.Handle(
                    new DummyCommand(),
                    () => Task.FromResult(Result.Fail<string>(forbidden)),
                    default);

                refused.IsFailure.Should().BeTrue("the precondition: the inner request was refused");

                // What the owning frame's WritePendingAsync composes, at the moment it runs:
                // the outer handler has returned success and its own frame is still open.
                inTransaction = [.. capture.Intents.Select(intent =>
                    AuditDraftComposer.InTransaction(intent, capture.ChangesOf(intent)))];

                capture.MarkCommitted();

                return Result.Ok("outer absorbed it");
            },
            default);

        inTransaction.Select(row => (row.Operation, row.Outcome, row.ErrorKey)).Should().Equal(
            ("tenancy.tenant.create", AuditOutcome.Success, (string?)null),
            ("tenancy.organization.create", AuditOutcome.Denied, forbidden.Message.Key));

        store.Standalone.Should().BeEmpty("both MUST rows committed with the transaction");
    }

    [Fact]
    public async Task A_nested_request_s_row_describes_its_own_writes_and_not_its_parent_s()
    {
        // A capture belongs to the frame whose handler wrote it. Without that, an outer and
        // an inner request auditing the same aggregate type would each carry the other's
        // instance in `changes` — and before the designation existed, would each refuse on
        // "two instances" and fail a request that did nothing wrong.
        var capture = new AuditStateCapture();
        var store = new RecordingAuditStore();
        var outer = Behavior(
            new FakeCatalog(AuditPipelineHarness.Entry("tenancy.tenant.create", entityType: typeof(Subject))),
            store, capture);
        var inner = Behavior(
            new FakeCatalog(AuditPipelineHarness.Entry("tenancy.organization.create", entityType: typeof(Subject))),
            store, capture);

        await outer.Handle(
            new DummyCommand(),
            async () =>
            {
                capture.Add(SubjectInsert("outer-1", "outer"));

                await inner.Handle(
                    new DummyCommand(),
                    () =>
                    {
                        capture.Add(SubjectInsert("inner-1", "inner"));
                        return Task.FromResult(Result.Ok("inner"));
                    },
                    default);

                capture.MarkRolledBack();

                return Result.Ok("outer");
            },
            default);

        store.Standalone.Select(row => (row.Operation, row.EntityId)).Should().BeEquivalentTo(
        [
            ("tenancy.tenant.create", "outer-1"),
            ("tenancy.organization.create", "inner-1"),
        ]);
        store.Standalone.Single(row => row.EntityId == "outer-1").Changes.Should().NotContain("inner-1");
    }

    [Fact]
    public async Task The_actor_and_correlation_travel_on_the_intent_to_every_writer()
    {
        // The in-transaction write had no way to reach them and wrote both columns NULL on
        // every successful row; the reconcile passed them. Read once, at declaration, they
        // cannot differ between the two writers (ADR-0044 Amendment 6 § 2).
        var capture = new AuditStateCapture();
        var store = new RecordingAuditStore();
        var behavior = Behavior(new FakeCatalog(AuditPipelineHarness.Entry()), store, capture);
        AuditEntryDraft? inTransaction = null;

        await behavior.Handle(
            new DummyCommand(),
            () =>
            {
                inTransaction = AuditDraftComposer.InTransaction(
                    capture.Intents.Single(), capture.ChangesOf(capture.Intents.Single()));
                capture.MarkRolledBack();

                return Task.FromResult(Result.Ok("ok"));
            },
            default);

        var reconciled = store.Standalone.Should().ContainSingle().Subject;

        foreach (var row in new[] { inTransaction!, reconciled })
        {
            row.ActorUserId!.Value.Value.Should().Be(AuditPipelineHarness.Actor);
            row.CorrelationId.Should().Be("00-harness-span-01");
        }
    }

    [Fact]
    public async Task A_store_that_fails_before_writing_keeps_the_refusal_and_still_clears()
    {
        // The reviewer's measurement, reproduced: a data source that cannot be built throws
        // InvalidOperationException from the Lazy factory, the store rethrows it unchanged,
        // and the reconcile's catch — AuditWriteFailedException and cancellation only — let
        // it escape the finally. The 403 became a 500, the capture was never cleared, and
        // the flow still believed a frame was open.
        var capture = new AuditStateCapture();
        var logger = new CapturingLogger();
        var store = new RecordingAuditStore
        {
            StandaloneThrows = new InvalidOperationException("the data source could not be built"),
        };
        var behavior = Behavior(
            new FakeCatalog(
                AuditPipelineHarness.Entry("tenancy.tenant.create"),
                AuditPipelineHarness.Entry("tenancy.organization.create", OperationClass.Should)),
            store, capture, logger: logger);

        var forbidden = new Error(new LocalizedMessage(LocalizedMessage.RequiredPrefix + "forbidden"));

        var result = await behavior.Handle(
            new DummyCommand(),
            () => { capture.MarkRolledBack(); return Task.FromResult(Result.Fail<string>(forbidden)); },
            default);

        result.Error!.Code.Should().Be("forbidden", "the caller keeps its own refusal");
        store.BestEffort.Should().ContainSingle(
            "one row's failure does not cost the next intent its attempt");
        capture.Intents.Should().BeEmpty("the outermost frame clears whatever the reconcile did");

        // One alert per failure. The store reports a failed write itself (IAuditStore), so the
        // reconcile adds a Warning and not a second Critical (the fifth review of Packet 9).
        logger.Levels.Should().NotContain(LogLevel.Critical);
        logger.Levels.Should().ContainSingle(level => level == LogLevel.Warning);

        await AssertTheNextRequestIsOutermostAsync(store);
    }

    [Fact]
    public async Task A_composer_refusal_keeps_the_refusal_and_still_clears()
    {
        // The composer's own refusal — two instances of the declared type, none designated —
        // was thrown OUTSIDE the reconcile's try, and took the same way out.
        var capture = new AuditStateCapture();
        var logger = new CapturingLogger();
        var store = new RecordingAuditStore();
        var behavior = Behavior(
            new FakeCatalog(AuditPipelineHarness.Entry(entityType: typeof(Subject))), store, capture,
            logger: logger);

        var forbidden = new Error(new LocalizedMessage(LocalizedMessage.RequiredPrefix + "forbidden"));

        var result = await behavior.Handle(
            new DummyCommand(),
            () =>
            {
                capture.Add(SubjectInsert("first", "a"));
                capture.Add(SubjectInsert("second", "b"));
                capture.MarkRolledBack();

                return Task.FromResult(Result.Fail<string>(forbidden));
            },
            default);

        result.Error!.Code.Should().Be("forbidden");
        store.Standalone.Should().BeEmpty("the row could not be composed, and nothing was guessed");
        capture.Intents.Should().BeEmpty();

        // A programmer error nothing else reports, so this is its alert.
        logger.Levels.Should().ContainSingle(level => level == LogLevel.Critical);

        await AssertTheNextRequestIsOutermostAsync(store);
    }

    [Fact]
    public async Task A_designated_subject_is_the_row_s_entity_and_the_other_instance_stays_in_changes()
    {
        // The publication's shape: one request writes two instances of one aggregate, the
        // handler designates the one the operation is about, and the retired one travels in
        // `changes` under its own pointer (ADR-0044 Amendment 6 § 1).
        var capture = new AuditStateCapture();
        var store = new RecordingAuditStore();
        var behavior = Behavior(
            new FakeCatalog(AuditPipelineHarness.Entry(entityType: typeof(Subject))), store, capture);

        await behavior.Handle(
            new DummyCommand(),
            () =>
            {
                capture.Designate(typeof(Subject), "successor");
                capture.Add(SubjectInsert("incumbent", "retired"));
                capture.Add(SubjectInsert("successor", "live"));
                capture.MarkRolledBack();

                return Task.FromResult(Result.Ok("ok"));
            },
            default);

        var row = store.Standalone.Should().ContainSingle().Subject;
        row.EntityId.Should().Be("successor");
        row.AfterState.Should().Contain("live").And.NotContain("retired");
        row.Changes.Should().Contain("/Subject/incumbent/slug").And.Contain("/Subject/successor/slug");
    }

    /// <summary>
    /// A fresh request on the same flow is the outermost frame again — which it is not when
    /// a previous request left <c>AuditFrame</c> open.
    /// </summary>
    private static async Task AssertTheNextRequestIsOutermostAsync(RecordingAuditStore store)
    {
        var next = new AuditStateCapture();
        var behavior = Behavior(new FakeCatalog(AuditPipelineHarness.Entry()), store, next);

        await behavior.Handle(
            new DummyCommand(),
            () => { next.MarkRolledBack(); return Task.FromResult(Result.Ok("ok")); },
            default);

        next.Intents.Should().BeEmpty(
            "only an OUTERMOST frame clears, so a frame left open by the previous request "
            + "would leave this one's intents behind");
    }

    private static CapturedEntityChange SubjectInsert(string id, string slug) =>
        new(
            nameof(Subject), id, null, $$"""{"slug":"{{slug}}"}""",
            [new CapturedFieldChange($"/Subject/{id}/slug", null, $"\"{slug}\"")]);

    /// <summary>A stand-in for the aggregate an intent names.</summary>
    private sealed class Subject;

    /// <summary>Classifies the first entry at its declared tier and is cancelled on the second.</summary>
    private sealed class CancelledOnSecondClassifier : IAuditConfigService
    {
        private int _calls;

        public Task<AuditClassification> ClassifyAsync(
            TenantId? tenantId, AuditCatalogEntry entry, CancellationToken cancellationToken = default) =>
            ++_calls == 1
                ? Task.FromResult(AuditClassification.Must)
                : Task.FromCanceled<AuditClassification>(new CancellationToken(canceled: true));
    }

    private static AuditLogBehavior<DummyCommand, Result<string>> Behavior(
        IAuditCatalog catalog,
        RecordingAuditStore store,
        AuditStateCapture? capture = null,
        IAuditConfigService? classifier = null,
        ILogger<AuditLogBehavior<DummyCommand, Result<string>>>? logger = null) =>
        new(
            catalog,
            classifier ?? new FakeClassifier(),
            capture ?? new AuditStateCapture(),
            store,
            new HarnessTenantContext(),
            new HarnessClock(DateTimeOffset.UnixEpoch),
            new HarnessGuidFactory(),
            logger ?? NullLogger<AuditLogBehavior<DummyCommand, Result<string>>>.Instance);

    /// <summary>Every log line the behavior writes, by level.</summary>
    private sealed class CapturingLogger : ILogger<AuditLogBehavior<DummyCommand, Result<string>>>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }
}
