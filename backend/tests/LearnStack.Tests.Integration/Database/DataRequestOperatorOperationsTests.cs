using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.Modules.Identity.Infrastructure.DataRequests;
using LearnStack.Modules.Tenancy.Application.Audit;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Secrets;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The operator's data-request owner against a real database, the real platform scope and the request guard's platform
/// arm (ADR-0107 § 8, ADR-0108 § 8): the inspection's four lists, its cursors and plans, and the erasure completion's one
/// transaction, its refusals, its repeat and its failures. The Audit executor is a recording double here except in the one
/// test that runs the real one through the scope; its own SQL is proven by its own suite. Fixture rows are written by the superuser with the guards paused, in a disposable database;
/// every statement under test runs as <c>learnstack_platform</c> through the scope, and every read-back of a column the
/// platform role is not granted runs as <c>learnstack_app</c> announcing the row's group.
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class DataRequestOperatorOperationsTests(SchemaFixture schema)
{
    private static readonly Guid Tenant = SchemaFixture.TenantA;
    private static readonly Guid Branch = SchemaFixture.OrgA1;

    [Fact]
    public async Task Each_list_shows_exactly_its_rows_oldest_due_first_and_metadata_only()
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        var awaitingFanout = await proof.RequestAsync("erasure", "closure_fanout", "awaiting_privileged_step", "now() - interval '1 day'");
        var awaitingSelf = await proof.RequestAsync("erasure", "self", "awaiting_privileged_step", "now() - interval '3 days'", Branch);
        var overdueErasure = await proof.RequestAsync("erasure", "administrator", "scheduled", "now() - interval '30 hours'");
        var overdueClosure = await proof.RequestAsync("closure", "self", "scheduled", "now() - interval '2 days'");
        await proof.RequestAsync("erasure", "self", "scheduled", "now() - interval '1 hour'");
        var stuckFanout = await proof.RequestAsync("erasure", "closure_fanout", "fulfilling", "now() - interval '3 hours'");
        var stuckExport = await proof.RequestAsync("export", "self", "fulfilling", "now() - interval '5 hours'", Branch);
        await proof.RequestAsync("erasure", "closure_fanout", "fulfilling", "now() - interval '90 minutes'");
        await proof.RequestAsync("erasure", "self", "completed", "now() - interval '4 days'");
        var expiredAvailable = await proof.ArtifactAsync("available", "now() - interval '8 days'");
        var expiredMarked = await proof.ArtifactAsync("expired", "now() - interval '9 days'", Branch);
        await proof.ArtifactAsync("available", "now() - interval '1 day'");
        await proof.ArtifactAsync("purged", "now() - interval '10 days'");
        await proof.ArtifactAsync("revoked", "now() - interval '2 days'");
        var owner = proof.Owner();

        var awaiting = await owner.InspectAsync(Proof.Admission(), Query(DataRequestInspectionState.AwaitingPrivilegedStep));
        var overdue = await owner.InspectAsync(Proof.Admission(), Query(DataRequestInspectionState.OverdueScheduled));
        var stuck = await owner.InspectAsync(Proof.Admission(), Query(DataRequestInspectionState.StuckFulfilling));
        var expired = await owner.InspectAsync(Proof.Admission(), Query(DataRequestInspectionState.ExpiredArtifact));

        awaiting!.Items.Select(item => item.RequestId).Should().Equal([awaitingSelf.Id, awaitingFanout.Id],
            "the self erasure fell due two days ago when its window ended, the fan-out a day ago when it was created");
        overdue!.Items.Select(item => item.RequestId).Should().Equal([overdueClosure.Id, overdueErasure.Id],
            "a window that ended is overdue whatever the kind, and one still open is not listed");
        stuck!.Items.Select(item => item.RequestId).Should().Equal([stuckExport.Id, stuckFanout.Id],
            "only what is still fulfilling two hours after it fell due is stuck");
        expired!.Items.Select(item => item.Artifact!.ArtifactId).Should().Equal(new[] { expiredMarked.Artifact!.Value, expiredAvailable.Artifact!.Value },
            "an unpurged artifact past its expiry is listed, a readable, purged or revoked one is not");

        var self = awaiting.Items[0];
        (self.TenantId, self.OrganizationId, self.Kind, self.Origin, self.Status).Should().Be(
            (Tenant, Branch, "erasure", "self", "awaiting_privileged_step"));
        self.ExecuteAt.Should().Be(self.CreatedAt + DataRequestLimits.CancellationWindow);
        self.Artifact.Should().BeNull();
        var artifact = expired.Items[0];
        (artifact.RequestId, artifact.Kind, artifact.Status, artifact.OrganizationId).Should().Be(
            (expiredMarked.Id, "export", "completed", Branch), "the artifact list names the export it belongs to and its group");
        (artifact.Artifact!.Status, artifact.Artifact.ExpiresAt < DateTimeOffset.UtcNow).Should().Be(("expired", true));

        var shown = JsonSerializer.Serialize(new object[] { awaiting, overdue, stuck, expired });
        foreach (var row in new[] { awaitingFanout, awaitingSelf, overdueErasure, overdueClosure, stuckFanout, stuckExport, expiredAvailable })
        {
            shown.Should().NotContain(row.Subject.ToString("D"), "no list names the subject").And
                .NotContain(row.Membership.ToString("D"), "or the frozen snapshot");
        }
    }

    [Fact]
    public async Task Pages_cover_a_list_without_a_repeat_or_a_gap_and_a_cursor_belongs_to_the_list_that_issued_it()
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        var ids = new List<Guid>();
        for (var index = 0; index < 5; index++)
        {
            // Equal due instants: the order and the cursor rest on the request's own identifier.
            ids.Add((await proof.RequestAsync("erasure", "closure_fanout", "awaiting_privileged_step", "'2026-11-01 12:00:00.000001+00'")).Id);
        }

        var owner = proof.Owner();
        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await owner.InspectAsync(Proof.Admission(), Query(DataRequestInspectionState.AwaitingPrivilegedStep, cursor, 2));
            seen.AddRange(page!.Items.Select(item => item.RequestId));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 10);

        pages.Should().Be(3);
        seen.Should().Equal(ids.Order(), "every row once, by its own key when the instants are equal");

        var issued = (await owner.InspectAsync(Proof.Admission(), Query(DataRequestInspectionState.AwaitingPrivilegedStep, null, 1)))!.NextCursor;
        issued.Should().NotBeNull();
        var refused = Proof.Admission();
        (await owner.InspectAsync(refused, Query(DataRequestInspectionState.OverdueScheduled, issued, 10))).Should().BeNull(
            "a cursor carries its list, so it cannot move another one");
        (await owner.InspectAsync(Proof.Admission(), Query(DataRequestInspectionState.ExpiredArtifact, issued, 10))).Should().BeNull();
        (await owner.InspectAsync(Proof.Admission(), Query(DataRequestInspectionState.AwaitingPrivilegedStep, "not-a-cursor", 10))).Should().BeNull();
        (await proof.AuditAsync(refused.Operator.CorrelationId!)).Should().BeEmpty("a refused cursor enters no scope");
        (await owner.InspectAsync(Proof.Admission(), Query(DataRequestInspectionState.AwaitingPrivilegedStep, issued, 10)))!.Items
            .Should().HaveCount(4, "the cursor still moves the list that issued it");
    }

    [Fact]
    public async Task Each_page_writes_one_entry_row_naming_the_inspection_with_its_ticket_and_no_result_row()
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        var awaiting = await proof.RequestAsync("erasure", "self", "awaiting_privileged_step", "now() - interval '3 days'");
        var admission = Proof.Admission("INC-310");

        var page = await proof.Owner().InspectAsync(admission, Query(DataRequestInspectionState.AwaitingPrivilegedStep));

        page!.Items.Should().ContainSingle().Which.RequestId.Should().Be(awaiting.Id);
        var entry = (await proof.AuditAsync(admission.Operator.CorrelationId!)).Should()
            .ContainSingle("the scope reads and changes nothing, so it writes no result row").Which;
        (entry.Operation, entry.Reason, entry.Tenant).Should().Be(
            (PlatformAdminScope.EnterOperation, "data_request_inspect", TenantId.PlatformSentinel.Value));
        entry.Metadata.Should().Contain("INC-310", "a sentinel-tenant row is unreadable to every tenant, so it carries the ticket");
        (await proof.ReadAsync(awaiting)).Version.Should().Be(2, "the inspection changed nothing");
    }

    [Fact]
    public async Task Every_list_is_an_ordered_scan_of_its_partial_index_with_no_sort()
    {
        // The statement is the text the inspection runs. Sequential and bitmap scans are disabled so that the only plan
        // left is an ordered scan of an index: the index absent from the plan, or a Sort above it, means the statement's
        // predicate or order no longer matches it, and a page would scan and sort every request or artifact.
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        await proof.SqlAsync($"""
            SET session_replication_role = replica;
            CREATE TEMP TABLE bulk AS SELECT g, uuidv7() AS request, uuidv7() AS artifact, uuidv7() AS subject
                FROM generate_series(1, 6000) g;
            INSERT INTO data_subject_requests (id, tenant_id, kind, origin, subject_user_id, requester_user_id, status,
                membership_snapshot, execute_at, completed_at, created_at, created_by, row_version)
            SELECT request, '{Tenant}', 'erasure', 'self', subject, subject,
                (ARRAY['scheduled','fulfilling','awaiting_privileged_step','completed','cancelled'])[1 + g % 5],
                jsonb_build_array(uuidv7()::text), now() - make_interval(hours => g) + interval '24 hours',
                CASE WHEN g % 5 = 3 THEN now() END, now() - make_interval(hours => g), subject, 2
            FROM bulk WHERE g <= 3000;
            INSERT INTO data_subject_requests (id, tenant_id, kind, origin, subject_user_id, requester_user_id, status,
                membership_snapshot, completed_at, created_at, created_by, row_version)
            SELECT request, '{Tenant}', 'export', 'self', subject, subject, 'completed', jsonb_build_array(uuidv7()::text),
                now() - make_interval(days => 8) + interval '1 hour', now() - make_interval(days => 8), subject, 2
            FROM bulk WHERE g > 3000;
            INSERT INTO data_export_artifacts (id, tenant_id, request_id, subject_user_id, status, byte_count, chunk_count,
                key_version, expires_at, purged_at, created_at, created_by)
            SELECT artifact, '{Tenant}', request, subject, CASE WHEN g % 30 = 0 THEN 'expired' ELSE 'purged' END, 100, 1, 'v1',
                now() - make_interval(days => 1, mins => g), CASE WHEN g % 30 = 0 THEN NULL ELSE now() - make_interval(days => 1, mins => g) END,
                now() - make_interval(days => 8, mins => g), '{UserId.SystemActor.Value}'
            FROM bulk WHERE g > 3000;
            ANALYZE data_subject_requests;
            ANALYZE data_export_artifacts;
            """);
        await using var platform = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.PlatformConnectionString);

        async Task<string> Plan(DataRequestInspectionState state, bool afterCursor)
        {
            await using var command = new NpgsqlCommand(
                "SET enable_seqscan = off; SET enable_bitmapscan = off; EXPLAIN (FORMAT JSON) "
                + DataRequestOperatorOperations.InspectionSql(state, afterCursor), platform);
            command.Parameters.AddWithValue("size", 21);
            if (state == DataRequestInspectionState.StuckFulfilling) command.Parameters.AddWithValue("stuck", DataRequestLimits.StuckFulfillingAfter);
            if (afterCursor)
            {
                command.Parameters.AddWithValue("after", new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
                command.Parameters.AddWithValue("key", Guid.CreateVersion7());
            }

            return (await command.ExecuteScalarAsync())!.ToString()!;
        }

        foreach (var state in Enum.GetValues<DataRequestInspectionState>())
        {
            foreach (var afterCursor in new[] { false, true })
            {
                var plan = await Plan(state, afterCursor);
                var index = state == DataRequestInspectionState.ExpiredArtifact
                    ? "ix_data_export_artifacts_unpurged_expiry" : "ix_data_subject_requests_operator_due";
                plan.Should().Contain(index, $"{state}, after a cursor: {afterCursor}").And.NotContain("\"Node Type\": \"Sort\"")
                    .And.NotContain("Incremental Sort").And.NotContain("Seq Scan");
            }
        }

        await using var definitions = new NpgsqlCommand(
            "SELECT string_agg(indexdef, ' | ' ORDER BY indexname) FROM pg_indexes WHERE indexname IN "
            + "('ix_data_subject_requests_operator_due', 'ix_data_export_artifacts_unpurged_expiry')", platform);
        var indexes = (string)(await definitions.ExecuteScalarAsync())!;
        indexes.Should().Contain("(expires_at, id) WHERE (status = ANY (ARRAY['available'::text, 'expired'::text]))")
            .And.Contain("(status, COALESCE(execute_at, created_at), id) WHERE (status = ANY (ARRAY['scheduled'::text, "
                + "'fulfilling'::text, 'awaiting_privileged_step'::text]))");
    }

    [Fact]
    public async Task A_completion_redacts_completes_and_records_in_one_transaction_from_the_row_s_own_subject()
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        var request = await proof.RequestAsync("erasure", "administrator", "awaiting_privileged_step", "now() - interval '3 days'", Branch,
            """{"consent_evidence":2,"request_receipt":1,"audit_actor_reference":9}""");
        var report = new AuditRedactionReport(
            12,
            new Dictionary<AuditRedactedColumn, long> { [AuditRedactedColumn.IpAddress] = 5, [AuditRedactedColumn.AfterState] = 3 },
            [
                new(RetainedReferenceKind.AuditActorReference, 4), new(RetainedReferenceKind.AuditMetadataReference, 2),
                new(RetainedReferenceKind.UnscopedAuditRows, 1),
            ]);
        var redactor = new RecordingRedactor(report);
        var admission = Proof.Admission("INC-311", PlatformOperations.ErasureComplete);

        var result = await proof.Owner(redactor).CompleteErasureAsync(admission, request.Id);

        result.Outcome.Should().Be(ErasureCompletionOutcome.Completed);
        result.TenantId.Should().Be(Tenant);
        result.Report.Should().BeSameAs(report);
        var expected = new Dictionary<string, long>
        {
            ["consent_evidence"] = 2,
            ["request_receipt"] = 1,
            ["audit_actor_reference"] = 4,
            ["audit_metadata_reference"] = 2,
            ["unscoped_audit_rows"] = 1,
        };
        result.Retained.Should().BeEquivalentTo(expected, "the tenant side's kinds stay and the audit log's are set from the report");

        var plan = redactor.Plans.Should().ContainSingle().Which;
        (plan.Subject.RequestId, plan.Subject.Kind, plan.Subject.Origin, plan.Subject.SubjectUserId.Value).Should().Be(
            (request.Id, DataRequestKind.Erasure, DataRequestOrigin.Administrator, request.Subject));
        (plan.TenantId.Value, plan.OrganizationId?.Value, plan.WholeTenant).Should().Be((Tenant, (Guid?)Branch, false),
            "an administrator's erasure redacts its group's rows only");
        plan.Subject.MembershipIds.Should().Equal([request.Membership], "the plan is the row's frozen snapshot");

        var stored = await proof.ReadAsync(request);
        stored.Status.Should().Be("completed");
        stored.Version.Should().Be(3, "the completion steps the version once");
        stored.CompletedAt.Should().Be(stored.UpdatedAt, "the guard stamps both from one database clock");
        stored.CompletedAt.Should().Be(result.CompletedAt);
        stored.CompletedAt!.Value.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        stored.UpdatedBy.Should().Be(UserId.SystemActor.Value, "an operator is not a user");
        JsonSerializer.Deserialize<Dictionary<string, long>>(stored.Result).Should().BeEquivalentTo(expected);

        var rows = await proof.AuditAsync(admission.Operator.CorrelationId!);
        rows.Should().HaveCount(2, "the entry row and one result row");
        var entry = rows.Single(row => row.Operation == PlatformAdminScope.EnterOperation);
        (entry.Tenant, entry.Reason).Should().Be((TenantId.PlatformSentinel.Value, "erasure_complete"));
        entry.Metadata.Should().Contain("INC-311");
        var recorded = rows.Single(row => row.Operation == "audit.redaction.apply");
        (recorded.Tenant, recorded.Module, recorded.Reason, recorded.Actor, recorded.EntityType, recorded.EntityId).Should().Be(
            (Tenant, "audit", "erasure_complete", (Guid?)null, "DataSubjectRequest", request.Id.ToString("D")));
        recorded.Metadata.Should().NotContain("INC-311", "the tenant can read its own row, so it never carries the ticket");
        JsonDocument.Parse(recorded.Before).RootElement.GetProperty("status").GetString().Should().Be("awaiting_privileged_step");
        using var after = JsonDocument.Parse(recorded.After);
        after.RootElement.GetProperty("status").GetString().Should().Be("completed");
        after.RootElement.GetProperty("rows_selected").GetInt64().Should().Be(12);
        after.RootElement.GetProperty("redacted").GetProperty("ip_address").GetInt64().Should().Be(5);
        after.RootElement.GetProperty("retained").GetProperty("audit_actor_reference").GetInt64().Should().Be(4);
        recorded.After.Should().NotContain(request.Subject.ToString("D")).And.NotContain(request.Membership.ToString("D"));
    }

    [Fact]
    public async Task A_repeat_answers_the_recorded_result_and_writes_no_second_row()
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        var request = await proof.RequestAsync("erasure", "self", "awaiting_privileged_step", "now() - interval '1 day'",
            Branch, """{"request_receipt":1}""");
        var redactor = new RecordingRedactor(new AuditRedactionReport(
            1, new Dictionary<AuditRedactedColumn, long>(), [new(RetainedReferenceKind.AuditEntityReference, 3)]));
        var owner = proof.Owner(redactor);
        var first = await owner.CompleteErasureAsync(Proof.Admission(operation: PlatformOperations.ErasureComplete), request.Id);
        var repeat = Proof.Admission(operation: PlatformOperations.ErasureComplete);

        var second = await owner.CompleteErasureAsync(repeat, request.Id);

        second.Outcome.Should().Be(ErasureCompletionOutcome.AlreadyCompleted);
        (second.TenantId, second.Status, second.CompletedAt).Should().Be((Tenant, "completed", first.CompletedAt));
        second.Retained.Should().BeEquivalentTo(first.Retained, "the recorded result, read back from the request");
        second.Report.Should().BeNull("a repeat redacts nothing; the counts are in the first act's row");
        var plan = redactor.Plans.Should().ContainSingle("the repeat never reached the executor").Which;
        (plan.Subject.Origin, plan.OrganizationId?.Value, plan.WholeTenant).Should().Be((DataRequestOrigin.Self, (Guid?)Branch, true),
            "a person's own erasure, filed in a branch, still redacts the person's rows in the whole tenant");
        (await proof.AuditAsync(repeat.Operator.CorrelationId!)).Should().ContainSingle()
            .Which.Operation.Should().Be(PlatformAdminScope.EnterOperation, "the repeat is on the record by its entry row alone");
        (await proof.ResultRowsAsync(request.Id)).Should().Be(1, "one completion, one result row");
        (await proof.ReadAsync(request)).Version.Should().Be(3);
    }

    [Fact]
    public async Task A_closure_s_fan_out_erasure_is_refused_in_its_every_status_and_nothing_changes()
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        var redactor = new RecordingRedactor();
        var owner = proof.Owner(redactor);
        foreach (var status in new[] { "awaiting_privileged_step", "completed" })
        {
            var request = await proof.RequestAsync("erasure", "closure_fanout", status, "now() - interval '3 days'");
            var before = await proof.ReadAsync(request);

            var result = await owner.CompleteErasureAsync(Proof.Admission(operation: PlatformOperations.ErasureComplete), request.Id);

            result.Outcome.Should().Be(ErasureCompletionOutcome.ClosureFanout, "only that closure's finalization completes it: " + status);
            (await proof.ReadAsync(request)).Should().Be(before, "a refused completion changes nothing");
            (await proof.ResultRowsAsync(request.Id)).Should().Be(0, "no result row describes a refused act");
        }

        redactor.Plans.Should().BeEmpty("no refused request reached the executor");
    }

    [Fact]
    public async Task A_completion_refuses_a_missing_request_another_kind_and_every_status_but_awaiting_and_changes_nothing()
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        var redactor = new RecordingRedactor();
        var owner = proof.Owner(redactor);
        var export = await proof.RequestAsync("export", "self", "completed", "now() - interval '2 days'");
        var closure = await proof.RequestAsync("closure", "self", "fulfilling", "now() - interval '2 days'");
        var refusals = new List<(Proof.Row Row, ErasureCompletionOutcome Outcome, string Status)>
        {
            (export, ErasureCompletionOutcome.NotErasure, "completed"),
            (closure, ErasureCompletionOutcome.NotErasure, "fulfilling"),
        };
        foreach (var status in new[] { "scheduled", "fulfilling", "blocked", "failed", "cancelled" })
        {
            refusals.Add((await proof.RequestAsync("erasure", "self", status, "now() - interval '3 days'"),
                ErasureCompletionOutcome.NotAwaiting, status));
        }

        var missing = Proof.Admission(operation: PlatformOperations.ErasureComplete);
        (await owner.CompleteErasureAsync(missing, Guid.CreateVersion7())).Outcome.Should().Be(ErasureCompletionOutcome.NotFound);
        (await proof.AuditAsync(missing.Operator.CorrelationId!)).Should().ContainSingle()
            .Which.Operation.Should().Be(PlatformAdminScope.EnterOperation);
        foreach (var (row, outcome, status) in refusals)
        {
            var before = await proof.ReadAsync(row);
            var admission = Proof.Admission(operation: PlatformOperations.ErasureComplete);

            var result = await owner.CompleteErasureAsync(admission, row.Id);

            (result.Outcome, result.Status, result.TenantId).Should().Be((outcome, status, (Guid?)Tenant), status);
            (await proof.ReadAsync(row)).Should().Be(before, "a refused completion changes nothing: " + status);
            (await proof.AuditAsync(admission.Operator.CorrelationId!)).Should().ContainSingle(status)
                .Which.Operation.Should().Be(PlatformAdminScope.EnterOperation, "only the entry row records the attempt");
        }

        redactor.Plans.Should().BeEmpty("no refused request reached the executor");
    }

    [Fact]
    public async Task A_failed_redaction_or_result_row_leaves_the_request_awaiting_and_only_the_entry_on_the_record()
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        var request = await proof.RequestAsync("erasure", "self", "awaiting_privileged_step", "now() - interval '3 days'");
        var before = await proof.ReadAsync(request);

        async Task<Exception> Fails(DataRequestOperatorOperations owner)
        {
            var admission = Proof.Admission(operation: PlatformOperations.ErasureComplete);
            var act = async () => await owner.CompleteErasureAsync(admission, request.Id);
            var failure = (await act.Should().ThrowAsync<Exception>()).Which;
            (await proof.ReadAsync(request)).Should().Be(before, "nothing of the act committed");
            (await proof.AuditAsync(admission.Operator.CorrelationId!)).Should().ContainSingle()
                .Which.Operation.Should().Be(PlatformAdminScope.EnterOperation);
            return failure;
        }

        (await Fails(proof.Owner(new RecordingRedactor(failure: new InvalidOperationException("forced defect")))))
            .Should().BeOfType<InvalidOperationException>("a defect is not retryable and is not dressed as one");
        var transient = await Fails(proof.Owner(new RecordingRedactor(failure: new TimeoutException("forced timeout"))));
        transient.Should().BeOfType<OperatorRecoveryUnavailableException>()
            .Which.CommitOutcomeUnknown.Should().BeFalse("nothing was sent to commit, and the redaction is idempotent");
        (await Fails(proof.Owner(new RecordingRedactor(), Proof.FailingResultStore())))
            .Should().BeOfType<AuditWriteFailedException>("a MUST row that cannot be written refuses the act");
        (await proof.ResultRowsAsync(request.Id)).Should().Be(0);

        (await proof.Owner(new RecordingRedactor()).CompleteErasureAsync(Proof.Admission(operation: PlatformOperations.ErasureComplete), request.Id))
            .Outcome.Should().Be(ErasureCompletionOutcome.Completed, "each failure left the request as it was, so a repeat completes it");
    }

    [Fact]
    public async Task A_commit_whose_answer_is_lost_is_the_unknown_outcome_and_a_repeat_settles_it()
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        var request = await proof.RequestAsync("erasure", "self", "awaiting_privileged_step", "now() - interval '3 days'");
        var lost = new DataRequestOperatorOperations(new LostCommitScope(proof.Scope(new RecordingRedactor())));

        var act = async () => await lost.CompleteErasureAsync(Proof.Admission(operation: PlatformOperations.ErasureComplete), request.Id);

        var failure = (await act.Should().ThrowAsync<OperatorRecoveryUnavailableException>()).Which;
        failure.CommitOutcomeUnknown.Should().BeTrue("the commit was sent, so the act may have landed");
        failure.InnerException.Should().BeOfType<TimeoutException>("the cause is kept for the route's log");
        (await proof.Owner(new RecordingRedactor()).CompleteErasureAsync(Proof.Admission(operation: PlatformOperations.ErasureComplete), request.Id))
            .Outcome.Should().Be(ErasureCompletionOutcome.Completed, "this commit never reached the database, so the repeat completes it");
    }

    [Fact]
    public async Task Two_concurrent_completions_serialize_on_the_row_and_the_second_finds_the_first()
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        var request = await proof.RequestAsync("erasure", "self", "awaiting_privileged_step", "now() - interval '3 days'");
        var redactor = new RecordingRedactor(hold: true);
        var owner = proof.Owner(redactor);

        var first = owner.CompleteErasureAsync(Proof.Admission(operation: PlatformOperations.ErasureComplete), request.Id);
        ErasureCompletionResult[] outcomes;
        try
        {
            await redactor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var second = owner.CompleteErasureAsync(Proof.Admission(operation: PlatformOperations.ErasureComplete), request.Id);
            await proof.WaitUntilALockIsAwaitedAsync(second, "the second completion waits on the first's row lock");
            redactor.Release.SetResult();
            outcomes = await Task.WhenAll(first, second);
        }
        finally
        {
            // A failed wait must not leave the first act holding its transaction open past the test.
            redactor.Release.TrySetResult();
        }


        outcomes.Select(result => result.Outcome).Should().Equal(
            ErasureCompletionOutcome.Completed, ErasureCompletionOutcome.AlreadyCompleted);
        redactor.Plans.Should().ContainSingle("the second read the committed completion and redacted nothing");
        (await proof.ResultRowsAsync(request.Id)).Should().Be(1);
    }

    [Fact]
    public async Task The_real_executor_redacts_through_the_scope_and_its_report_commits_with_the_request()
    {
        await using var proof = await Proof.CreateAsync(schema.Postgres);
        var request = await proof.RequestAsync("erasure", "self", "awaiting_privileged_step", "now() - interval '3 days'");
        Guid actor = Guid.CreateVersion7(), entity = Guid.CreateVersion7(), payload = Guid.CreateVersion7(),
            unrelated = Guid.CreateVersion7(), foreign = Guid.CreateVersion7(), course = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        await proof.SqlAsync($$"""
            INSERT INTO audit_log (id, "timestamp", tenant_id, organization_id, actor_user_id, actor_email, module, operation,
                operation_type, operation_class, entity_type, entity_id, outcome, after_state, correlation_id, ip_address, user_agent)
            VALUES
            ('{{actor}}', clock_timestamp(), '{{Tenant}}', NULL, '{{request.Subject}}', 'p@example.test', 'education', 'e2e.row',
                'Update', 'Must', 'Course', '{{course}}', 'success', NULL, '00-{{actor:N}}-01', '198.51.100.7', 'UA/1.0'),
            ('{{entity}}', clock_timestamp(), '{{Tenant}}', NULL, '{{other}}', 'a@example.test', 'identity', 'e2e.row',
                'Update', 'Must', 'Membership', '{{request.Membership}}', 'success', NULL, '00-{{entity:N}}-01', '198.51.100.7', 'UA/1.0'),
            ('{{payload}}', clock_timestamp(), '{{Tenant}}', '{{Branch}}', '{{other}}', 'a@example.test', 'education', 'e2e.row',
                'Update', 'Must', 'Course', '{{course}}', 'success',
                '{"Id":"{{course}}","CreatedBy":"{{other}}","UpdatedBy":"{{request.Subject}}"}', '00-{{payload:N}}-01', '198.51.100.7', 'UA/1.0'),
            ('{{unrelated}}', clock_timestamp(), '{{Tenant}}', NULL, '{{other}}', 'a@example.test', 'education', 'e2e.row',
                'Update', 'Must', 'Course', '{{course}}', 'success',
                '{"Id":"{{course}}","CreatedBy":"{{other}}","UpdatedBy":"{{other}}"}', '00-{{unrelated:N}}-01', '198.51.100.7', 'UA/1.0'),
            ('{{foreign}}', clock_timestamp(), '{{SchemaFixture.TenantB}}', NULL, '{{request.Subject}}', 'p@example.test', 'education',
                'e2e.row', 'Update', 'Must', 'Course', '{{course}}', 'success',
                '{"Id":"{{course}}","UpdatedBy":"{{request.Subject}}"}', '00-{{foreign:N}}-01', '198.51.100.7', 'UA/1.0')
            """);
        var services = new ServiceCollection();
        services.AddIdentityContextualFoundation();
        services.AddDataExportCustody(new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();
        var executor = new AuditRedactionExecutor(provider.GetServices<IUserReferenceLocator>());
        var admission = Proof.Admission("INC-312", PlatformOperations.ErasureComplete);

        var result = await proof.Owner(executor).CompleteErasureAsync(admission, request.Id);

        result.Outcome.Should().Be(ErasureCompletionOutcome.Completed);
        var report = result.Report.Should().NotBeNull().And.Subject.As<AuditRedactionReport>();
        report.RowsSelected.Should().Be(3, "the actor row, the Membership row and the payload row of a person's own request");
        (report.Redacted[AuditRedactedColumn.IpAddress], report.Redacted[AuditRedactedColumn.UserAgent],
            report.Redacted[AuditRedactedColumn.ActorEmail], report.Redacted[AuditRedactedColumn.AfterState])
            .Should().Be((3L, 3L, 3L, 1L));
        result.Retained.Should().Contain(new KeyValuePair<string, long>("audit_actor_reference", 1))
            .And.Contain(new KeyValuePair<string, long>("audit_entity_reference", 1));

        foreach (var selected in new[] { actor, entity, payload })
        {
            (await proof.AuditColumnsAsync(selected)).Should().Match<AuditColumns>(
                row => row.Address == null && row.Agent == null && row.Email == null, "{0} is selected", selected);
        }

        var redacted = JsonDocument.Parse((await proof.AuditColumnsAsync(payload)).After!).RootElement;
        redacted.GetProperty("UpdatedBy").GetString().Should().Be(SensitiveTokenCatalog.RedactedValue);
        redacted.GetProperty("CreatedBy").GetString().Should().Be(other.ToString("D"), "another person's value at the same path stays");
        foreach (var kept in new[] { unrelated, foreign })
        {
            (await proof.AuditColumnsAsync(kept)).Should().Match<AuditColumns>(
                row => row.Address == "198.51.100.7" && row.Agent == "UA/1.0" && row.Email != null
                    && row.After!.Contains(kept == foreign ? request.Subject.ToString("D") : other.ToString("D")),
                "{0} names no one of this request's tenant", kept);
        }

        (await proof.ReadAsync(request)).Status.Should().Be("completed");
        (await proof.ResultRowsAsync(request.Id)).Should().Be(1, "the redaction's result row commits with the request");
        var repeat = await proof.Owner(executor).CompleteErasureAsync(
            Proof.Admission(operation: PlatformOperations.ErasureComplete), request.Id);
        repeat.Outcome.Should().Be(ErasureCompletionOutcome.AlreadyCompleted);
        (await proof.ResultRowsAsync(request.Id)).Should().Be(1, "a repeat writes no second result row and redacts nothing");
    }

    private static DataRequestInspectionQuery Query(DataRequestInspectionState state, string? cursor = null, int limit = 10) =>
        new(state, cursor, limit);

    /// <summary>The Audit executor's stand-in: it records each plan, may hold the first call, and returns its report or fails.</summary>
    private sealed class RecordingRedactor(AuditRedactionReport? report = null, Exception? failure = null, bool hold = false) : IAuditRedactor
    {
        public List<AuditRedactionPlan> Plans { get; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AuditRedactionReport> RedactAsync(
            AuditRedactionPlan plan, PlatformAdmission admission, System.Data.Common.DbConnection connection,
            System.Data.Common.DbTransaction transaction, CancellationToken cancellationToken = default)
        {
            admission.Operation.Should().BeSameAs(PlatformOperations.ErasureComplete);
            Plans.Add(plan);
            if (hold && Plans.Count == 1)
            {
                Entered.SetResult();
                await Release.Task;
            }

            return failure is null
                ? report ?? new AuditRedactionReport(0, new Dictionary<AuditRedactedColumn, long>(), [])
                : throw failure;
        }
    }

    /// <summary>The real scope with a handle whose commit is never sent and whose answer is a timeout, as a lost reply.</summary>
    private sealed class LostCommitScope(IPlatformAdminScope inner) : IPlatformAdminScope
    {
        public async Task<IPlatformAdminScopeHandle> EnterAsync(
            PlatformAdmission admission, CancellationToken cancellationToken = default,
            [System.Runtime.CompilerServices.CallerMemberName] string? callerMember = null,
            [System.Runtime.CompilerServices.CallerFilePath] string? callerFile = null,
            [System.Runtime.CompilerServices.CallerLineNumber] int callerLine = 0) =>
            new Handle(await inner.EnterAsync(admission, cancellationToken, callerMember, callerFile, callerLine));

        private sealed class Handle(IPlatformAdminScopeHandle real) : IPlatformAdminScopeHandle
        {
            public Task<int> ExecuteAsync(string sql, IReadOnlyList<PlatformSqlParameter>? parameters = null, CancellationToken cancellationToken = default) =>
                real.ExecuteAsync(sql, parameters, cancellationToken);

            public Task<IReadOnlyList<T>> QueryAsync<T>(
                string sql, IReadOnlyList<PlatformSqlParameter>? parameters, Func<System.Data.Common.DbDataReader, T> map,
                CancellationToken cancellationToken = default) => real.QueryAsync(sql, parameters, map, cancellationToken);

            public Task WriteIntentAsync(PlatformResult intent, CancellationToken cancellationToken = default) =>
                real.WriteIntentAsync(intent, cancellationToken);

            public Task<AuditRedactionReport> RedactAuditAsync(AuditRedactionPlan plan, CancellationToken cancellationToken = default) =>
                real.RedactAuditAsync(plan, cancellationToken);

            public Task WriteResultAsync(PlatformResult result, CancellationToken cancellationToken = default) =>
                real.WriteResultAsync(result, cancellationToken);

            public Task CommitAsync(CancellationToken cancellationToken = default) =>
                Task.FromException(new TimeoutException("forced lost commit"));

            public ValueTask DisposeAsync() => real.DisposeAsync();
        }
    }

    private sealed record AuditRow(
        Guid Tenant, string Module, string Operation, string? Reason, Guid? Actor, string? EntityType, string? EntityId,
        string Before, string After, string Metadata);

    private sealed record AuditColumns(string? Address, string? Agent, string? Email, string? After);

    private sealed record StoredRequest(
        string Status, long Version, DateTimeOffset? CompletedAt, DateTimeOffset? UpdatedAt, Guid? UpdatedBy, string Result);

    private sealed class Proof : IAsyncDisposable
    {
        private readonly PostgresFixture _postgres;
        private readonly NpgsqlDataSource _platform;

        private Proof(PostgresFixture postgres, DisposableSchemaDatabase database)
        {
            _postgres = postgres;
            Database = database;
            _platform = LearnStack.Api.Composition.PersistenceCompositionExtensions.BuildPlatformDataSource(database.PlatformConnectionString);
        }

        internal sealed record Row(Guid Id, Guid Subject, Guid Membership, Guid? Organization, Guid? Artifact = null);

        internal DisposableSchemaDatabase Database { get; }

        internal static async Task<Proof> CreateAsync(PostgresFixture postgres) =>
            new(postgres, await DisposableSchemaDatabase.CreateAsync(postgres));

        internal static PlatformAdmission Admission(string? ticket = null, PlatformOperation? operation = null) =>
            TestPlatformAdmissions.Admission(
                operation ?? PlatformOperations.DataRequestInspect, ticket, correlationId: "00-data-request-" + Guid.NewGuid().ToString("N"));

        /// <summary>The scope as production builds it, with the real audit store and the given executor.</summary>
        internal PlatformAdminScope Scope(IAuditRedactor? redactor = null, IServiceScopeFactory? audit = null) => new(
            new PrivilegedDataSource(() => _platform), new AuditCatalog([new TenancyAuditCatalogSource()]),
            audit ?? AuditServices(redactor), new SystemClock(), new SystemGuidFactory(), NullLogger<PlatformAdminScope>.Instance);

        internal DataRequestOperatorOperations Owner(IAuditRedactor? redactor = null, IServiceScopeFactory? audit = null) =>
            new(Scope(redactor, audit is null ? null : WithRedactor(audit, redactor)));

        /// <summary>A store that writes the scope's entry row and fails every result row, as an outage between them would.</summary>
        internal static IServiceScopeFactory FailingResultStore() => new ServiceCollection()
            .AddScoped<IAuditStore>(_ => new ResultFailingStore(AuditServices(null).CreateScope().ServiceProvider.GetRequiredService<IAuditStore>()))
            .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        /// <summary>One request, written with the guards paused; the identifiers are fresh and its times follow its kind.</summary>
        internal async Task<Row> RequestAsync(
            string kind, string origin, string status, string createdAt, Guid? organization = null, string result = "{}")
        {
            var row = new Row(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), organization);
            var requester = origin switch
            {
                "self" => $"'{row.Subject}'",
                "administrator" => $"'{Guid.CreateVersion7()}'",
                _ => "NULL",
            };
            var window = kind != "export" && origin != "closure_fanout";
            // A request that left its initial state was last stamped by its job, never after the present.
            var initial = status is "requested" or "scheduled";
            const string Stamped = "LEAST(at + interval '25 hours', now())";
            await SqlAsync($"""
                SET session_replication_role = replica;
                INSERT INTO data_subject_requests (id, tenant_id, organization_id, kind, origin, subject_user_id, requester_user_id,
                    status, membership_snapshot, execute_at, result, completed_at, failure_reason, last_refusal, created_at,
                    created_by, updated_at, updated_by, row_version)
                SELECT '{row.Id}', '{Tenant}', {Literal(organization)}, '{kind}', '{origin}', '{row.Subject}', {requester},
                    '{status}', '["{row.Membership}"]'::jsonb, {(window ? "at + interval '24 hours'" : "NULL")}, '{result}'::jsonb,
                    {(status == "completed" ? Stamped : "NULL")},
                    {(status == "failed" ? "'execution_failed'" : "NULL")}, {(status == "blocked" ? "'blocked_hold'" : "NULL")},
                    at, COALESCE({requester}, '{UserId.SystemActor.Value}')::uuid,
                    {(initial ? "NULL" : Stamped)}, {(initial ? "NULL" : $"'{UserId.SystemActor.Value}'")}, {(initial ? 0 : 2)}
                FROM (SELECT ({createdAt})::timestamptz AS at) AS created
                """);
            return row;
        }

        /// <summary>A completed export and its artifact in the given status, created at the given time.</summary>
        internal async Task<Row> ArtifactAsync(string status, string createdAt, Guid? organization = null)
        {
            var request = await RequestAsync("export", "self", "completed", $"({createdAt}) - interval '1 hour'", organization);
            var artifact = Guid.CreateVersion7();
            var purged = status is "revoked" or "purged";
            await SqlAsync($"""
                SET session_replication_role = replica;
                INSERT INTO data_export_artifacts (id, tenant_id, organization_id, request_id, subject_user_id, status, byte_count,
                    chunk_count, key_version, expires_at, purged_at, created_at, created_by)
                SELECT '{artifact}', '{Tenant}', {Literal(organization)}, '{request.Id}', '{request.Subject}', '{status}', 100, 1, 'v1',
                    at + interval '7 days', {(purged ? "at + interval '1 day'" : "NULL")}, at, '{UserId.SystemActor.Value}'
                FROM (SELECT ({createdAt})::timestamptz AS at) AS created
                """);
            return request with { Artifact = artifact };
        }

        /// <summary>The columns the platform role is not granted, read as the application role announcing the row's group.</summary>
        internal async Task<StoredRequest> ReadAsync(Row row)
        {
            await using var app = await DataExportTestKit.OpenAppAsync(Database.AppConnectionString);
            await using var transaction = await app.BeginTransactionAsync();
            await IdentityMembershipFixture.ScopeAsync(app, transaction, Tenant, row.Organization);
            await using var command = new NpgsqlCommand(
                "SELECT status, row_version, completed_at, updated_at, updated_by, result::text FROM data_subject_requests WHERE id = @id",
                app, transaction);
            command.Parameters.AddWithValue("id", row.Id);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue("the row is visible to its own group");
            return new StoredRequest(
                reader.GetString(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3), reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.GetString(5));
        }

        internal async Task<List<AuditRow>> AuditAsync(string correlation)
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.PlatformConnectionString);
            await using var command = new NpgsqlCommand("""
                SELECT tenant_id, module, operation, reason, actor_user_id, entity_type, entity_id, before_state::text,
                    after_state::text, metadata::text
                FROM audit_log WHERE correlation_id = @c ORDER BY timestamp, id
                """, connection);
            command.Parameters.AddWithValue("c", correlation);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<AuditRow>();
            while (await reader.ReadAsync())
            {
                rows.Add(new AuditRow(
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                    reader.IsDBNull(8) ? string.Empty : reader.GetString(8), reader.IsDBNull(9) ? string.Empty : reader.GetString(9)));
            }

            return rows;
        }

        internal async Task<AuditColumns> AuditColumnsAsync(Guid id)
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.PlatformConnectionString);
            await using var command = new NpgsqlCommand(
                "SELECT host(ip_address), user_agent, actor_email, after_state::text FROM audit_log WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue("the audit row exists");
            return new AuditColumns(
                reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3));
        }

        internal async Task<long> ResultRowsAsync(Guid request)
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.PlatformConnectionString);
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM audit_log WHERE operation = 'audit.redaction.apply' AND entity_id = @id", connection);
            command.Parameters.AddWithValue("id", request.ToString("D"));
            return (long)(await command.ExecuteScalarAsync())!;
        }

        /// <summary>
        /// Waits until a backend connected to this database waits on a lock, from <c>pg_locks</c>, instead of sleeping. A
        /// row-lock waiter waits on the holder's transaction identifier, a lock with no database, so the backend is matched
        /// through its session's database instead.
        /// </summary>
        internal async Task WaitUntilALockIsAwaitedAsync(Task contender, string because)
        {
            await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.PlatformConnectionString);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (true)
            {
                contender.IsCompleted.Should().BeFalse(because);
                await using var command = new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_locks l JOIN pg_catalog.pg_stat_activity a ON a.pid = l.pid "
                    + "WHERE a.datname = current_database() AND NOT l.granted)", observer);
                if ((bool)(await command.ExecuteScalarAsync())!) return;
                DateTime.UtcNow.Should().BeBefore(deadline, because);
                await Task.Delay(TimeSpan.FromMilliseconds(20));
            }
        }

        internal Task SqlAsync(string sql) => _postgres.ExecuteAsSuperuserAsync(sql, RebindProofSupport.DatabaseOf(Database.MigrationConnectionString));

        public async ValueTask DisposeAsync()
        {
            await _platform.DisposeAsync();
            await Database.DisposeAsync();
        }

        private static string Literal(Guid? value) => value is { } id ? $"'{id}'" : "NULL";

        private static IServiceScopeFactory WithRedactor(IServiceScopeFactory audit, IAuditRedactor? redactor) =>
            redactor is null ? audit : new RedactorScopeFactory(audit, redactor);

        private static IServiceScopeFactory AuditServices(IAuditRedactor? redactor) =>
            (redactor is null ? new ServiceCollection() : new ServiceCollection().AddSingleton(redactor))
                .AddMetrics()
                .AddLogging()
                .AddScoped<IAuditStateCapture, AuditStateCapture>()
                .AddSingleton<IAuditHealth, AuditHealth>()
                .AddSingleton<IAuditCatalog>(new AuditCatalog([new TenancyAuditCatalogSource()]))
                .AddSingleton<ITenantContextAccessor>(StaticTenantContextAccessor.Unresolved)
                .AddScoped<LearnStack.SharedKernel.Identity.IGlobalIdentityEnrollment, GlobalIdentityEnrollment>()
                .AddSingleton(new Lazy<NpgsqlDataSource>(
                    () => throw new InvalidOperationException("The scope's rows are written on its own connection.")))
                .AddScoped<IAuditStore, PostgresAuditStore>()
                .BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>A store's service scope that also holds the executor, so a failing store and a redactor combine.</summary>
    private sealed class RedactorScopeFactory(IServiceScopeFactory inner, IAuditRedactor redactor) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(inner.CreateScope(), redactor);

        private sealed class Scope(IServiceScope inner, IAuditRedactor redactor) : IServiceScope, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type type) => type == typeof(IAuditRedactor) ? redactor : inner.ServiceProvider.GetService(type);

            public void Dispose() => inner.Dispose();
        }
    }

    private sealed class ResultFailingStore(IAuditStore store) : IAuditStore
    {
        public Task WritePlatformScopeAsync(AuditEntryDraft entry, System.Data.Common.DbConnection connection,
            System.Data.Common.DbTransaction transaction, CancellationToken cancellationToken = default) =>
            store.WritePlatformScopeAsync(entry, connection, transaction, cancellationToken);

        public Task WritePlatformOperatorResultAsync(AuditEntryDraft entry, PlatformAdmission admission,
            System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction,
            CancellationToken cancellationToken = default) => throw new AuditWriteFailedException("forced result failure");

        public Task WriteGlobalIdentityPendingAsync(LearnStack.SharedKernel.Persistence.IUnitOfWork unit,
            GlobalIdentityCreationReference creation, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ReconcileGlobalIdentityAsync(AuditIntent intent, IReadOnlyList<CapturedEntityChange> changes,
            AuditIntentState state, DateTimeOffset timestamp, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task WritePendingAsync(LearnStack.SharedKernel.Persistence.IUnitOfWork unitOfWork,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task WriteStandaloneAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task WriteBestEffortAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task WriteJobTerminalAsync(AuditEntryDraft entry, JobTerminalTransition transition,
            System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task WriteEventTerminalAsync(AuditEntryDraft entry, EventTerminalTransition transition,
            System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
