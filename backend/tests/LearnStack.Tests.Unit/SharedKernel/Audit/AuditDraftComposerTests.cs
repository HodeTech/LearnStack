using FluentAssertions;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Audit;

/// <summary>
/// The one composer both writers share: what a row is about, and which outcome it records.
/// </summary>
/// <remarks>
/// Driven directly because the rules are pure and the two writers each reach only part of
/// them — the in-transaction write only ever sees an owner that succeeded, and the
/// reconcile only ever sees a transaction that did not carry the row. A table walked here is
/// the only place every combination is on the record at once.
/// </remarks>
public sealed class AuditDraftComposerTests
{
    private static readonly Guid Actor = Guid.Parse("bbbbbbbb-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Declared = DateTimeOffset.UnixEpoch.AddMinutes(1);

    [Fact]
    public void An_in_transaction_row_with_no_recorded_result_is_the_owner_s_own_success()
    {
        // WritePendingAsync runs only on the owning handler's success, and the owner's frame
        // is the only one still open when it does.
        var row = AuditDraftComposer.InTransaction(Intent(), []);

        row.Outcome.Should().Be(AuditOutcome.Success);
        row.ErrorKey.Should().BeNull();
        row.Timestamp.Should().Be(Declared, "the in-transaction row takes DeclaredAt");
    }

    [Fact]
    public void An_in_transaction_row_keeps_a_nested_request_s_refusal_or_failure()
    {
        var refused = Intent() with { Result = AuditIntentResult.Refused(AuditOutcome.Denied, "lockey_forbidden") };
        var threw = Intent() with { Result = AuditIntentResult.Thrown };

        var denied = AuditDraftComposer.InTransaction(refused, []);
        denied.Outcome.Should().Be(AuditOutcome.Denied);
        denied.ErrorKey.Should().Be("lockey_forbidden");

        AuditDraftComposer.InTransaction(threw, []).Outcome.Should().Be(AuditOutcome.Failed);
    }

    [Theory]
    // A refusal the request returned is a fact about the operation: no commit changes it.
    [InlineData("denied", AuditIntentState.RolledBack, AuditOutcome.Denied, "lockey_forbidden")]
    [InlineData("denied", AuditIntentState.Indeterminate, AuditOutcome.Denied, "lockey_forbidden")]
    [InlineData("denied", AuditIntentState.Committed, AuditOutcome.Denied, "lockey_forbidden")]
    [InlineData("denied", AuditIntentState.Pending, AuditOutcome.Denied, "lockey_forbidden")]
    [InlineData("failed", AuditIntentState.RolledBack, AuditOutcome.Failed, "lockey_forbidden")]
    // Anything else takes the transaction's word — in doubt is indeterminate...
    [InlineData("succeeded", AuditIntentState.Indeterminate, AuditOutcome.Indeterminate, null)]
    [InlineData("threw", AuditIntentState.Indeterminate, AuditOutcome.Indeterminate, null)]
    [InlineData("open", AuditIntentState.Indeterminate, AuditOutcome.Indeterminate, null)]
    // ...committed is success only for a request that did not throw...
    [InlineData("succeeded", AuditIntentState.Committed, AuditOutcome.Success, null)]
    [InlineData("threw", AuditIntentState.Committed, AuditOutcome.Failed, null)]
    [InlineData("open", AuditIntentState.Committed, AuditOutcome.Failed, null)]
    // ...and otherwise its work did not persist.
    [InlineData("succeeded", AuditIntentState.RolledBack, AuditOutcome.Failed, null)]
    [InlineData("succeeded", AuditIntentState.Pending, AuditOutcome.Failed, null)]
    [InlineData("threw", AuditIntentState.RolledBack, AuditOutcome.Failed, null)]
    public void A_reconciled_row_combines_its_request_s_result_with_the_transaction_s_state(
        string result, AuditIntentState state, AuditOutcome expected, string? expectedKey)
    {
        var intent = Intent() with
        {
            Result = result switch
            {
                "denied" => AuditIntentResult.Refused(AuditOutcome.Denied, "lockey_forbidden"),
                "failed" => AuditIntentResult.Refused(AuditOutcome.Failed, "lockey_forbidden"),
                "succeeded" => AuditIntentResult.Succeeded,
                "threw" => AuditIntentResult.Thrown,
                _ => null,
            },
        };

        var row = AuditDraftComposer.Reconciled(intent, [], state, Declared.AddMinutes(5));

        row.Outcome.Should().Be(expected);
        row.ErrorKey.Should().Be(expectedKey);
        row.Timestamp.Should().Be(Declared.AddMinutes(5), "a re-write takes the fresh instant it was handed");
    }

    [Fact]
    public void A_refusal_is_denied_or_failed_and_nothing_else()
    {
        FluentActions.Invoking(() => AuditIntentResult.Refused(AuditOutcome.Success, null))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => AuditIntentResult.Refused(AuditOutcome.Indeterminate, null))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Both_writers_take_the_actor_and_correlation_from_the_intent()
    {
        var intent = Intent();

        foreach (var row in new[]
        {
            AuditDraftComposer.InTransaction(intent, []),
            AuditDraftComposer.Reconciled(intent, [], AuditIntentState.RolledBack, Declared),
        })
        {
            row.ActorUserId.Should().Be(UserId.From(Actor));
            row.CorrelationId.Should().Be("00-trace-01");
        }
    }

    [Fact]
    public void A_designated_subject_is_the_row_s_entity_and_every_other_instance_stays_in_changes()
    {
        var intent = Intent(typeof(Probe)) with { SubjectId = "successor" };

        var row = AuditDraftComposer.InTransaction(intent,
        [
            Capture("incumbent", before: """{"Status":"Active"}""", after: """{"Status":"Deprecated"}"""),
            Capture("successor", before: """{"Status":"Draft"}""", after: """{"Status":"Active"}"""),
        ]);

        row.EntityId.Should().Be("successor");
        row.BeforeState.Should().Be("""{"Status":"Draft"}""");
        row.AfterState.Should().Be("""{"Status":"Active"}""");
        row.Changes.Should().Contain("/Probe/incumbent/Status").And.Contain("/Probe/successor/Status",
            "the retirement is on the record under its own pointer");
    }

    [Fact]
    public void A_designated_subject_with_nothing_captured_still_names_the_instance()
    {
        // A request refused after its handler named the subject and before it wrote — a stale
        // incumbent, say — is still a row about that instance.
        var intent = Intent(typeof(Probe)) with { SubjectId = "successor" };

        var row = AuditDraftComposer.Reconciled(
            intent, [Capture("incumbent", "{}", "{}")], AuditIntentState.RolledBack, Declared);

        row.EntityId.Should().Be("successor");
        row.BeforeState.Should().BeNull();
        row.AfterState.Should().BeNull();
        row.Changes.Should().Contain("/Probe/incumbent/");
    }

    [Fact]
    public void Two_undesignated_instances_are_refused_rather_than_guessed_between()
    {
        var act = () => AuditDraftComposer.InTransaction(
            Intent(typeof(Probe)), [Capture("one", "{}", "{}"), Capture("two", "{}", "{}")]);

        act.Should().Throw<AuditWriteFailedException>().WithMessage("*IAuditSubject*");
    }

    [Fact]
    public void One_instance_captured_twice_is_merged_earliest_before_and_latest_after()
    {
        // ProvisionTenantCommand captures Tenant twice — Added, then Modified — and the rule
        // that picks the earliest before and the latest after is what keeps both halves.
        var row = AuditDraftComposer.InTransaction(Intent(typeof(Probe)),
        [
            Capture("t-1", before: null, after: """{"v":1}"""),
            Capture("t-1", before: """{"v":1}""", after: """{"v":2}"""),
        ]);

        row.EntityId.Should().Be("t-1");
        row.BeforeState.Should().BeNull();
        row.AfterState.Should().Be("""{"v":2}""");
    }

    private sealed class Probe;

    private static CapturedEntityChange Capture(string id, string? before, string? after) =>
        new(nameof(Probe), id, before, after,
            [new CapturedFieldChange($"/Probe/{id}/Status", before is null ? null : "\"x\"", after is null ? null : "\"y\"")]);

    private static AuditIntent Intent(Type? entityType = null) =>
        new(
            AuditEntryId.From(Guid.CreateVersion7()),
            TenantId.From(Guid.CreateVersion7()),
            OrganizationId: null,
            ActorUserId: UserId.From(Actor),
            CorrelationId: "00-trace-01",
            ModuleName: "customization",
            Operation: "customization.content_type.publish",
            OperationType: OperationType.Update,
            OperationClass: OperationClass.Must,
            EntityType: entityType,
            DeclaredAt: Declared);
}
