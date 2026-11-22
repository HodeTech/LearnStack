using System.Reflection;
using FluentAssertions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

/// <summary>ADR-0106 § 8's status machine, the immutable snapshot and the closed documents of a request.</summary>
public sealed class DataSubjectRequestDomainTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly OrganizationId Organization = OrganizationId.From(Guid.CreateVersion7());
    private static readonly UserId Subject = UserId.From(Guid.CreateVersion7());
    private static readonly UserId Administrator = UserId.From(Guid.CreateVersion7());
    private static readonly MembershipId Current = MembershipId.From(Guid.CreateVersion7());
    private static readonly MembershipId Tombstone = MembershipId.From(Guid.CreateVersion7());

    public enum Action { StartFulfilling, AwaitPrivilegedStep, Complete, Fail, Block, Cancel }

    /// <summary>ADR-0106 § 8, row by row: the only arrows each kind has.</summary>
    private static readonly Dictionary<(DataRequestKind, DataRequestStatus, Action), DataRequestStatus> Arrows = new()
    {
        [(DataRequestKind.Export, DataRequestStatus.Requested, Action.StartFulfilling)] = DataRequestStatus.Fulfilling,
        [(DataRequestKind.Export, DataRequestStatus.Requested, Action.Cancel)] = DataRequestStatus.Cancelled,
        [(DataRequestKind.Export, DataRequestStatus.Fulfilling, Action.Complete)] = DataRequestStatus.Completed,
        [(DataRequestKind.Export, DataRequestStatus.Fulfilling, Action.Fail)] = DataRequestStatus.Failed,
        [(DataRequestKind.Erasure, DataRequestStatus.Scheduled, Action.StartFulfilling)] = DataRequestStatus.Fulfilling,
        [(DataRequestKind.Erasure, DataRequestStatus.Scheduled, Action.Cancel)] = DataRequestStatus.Cancelled,
        [(DataRequestKind.Erasure, DataRequestStatus.Fulfilling, Action.AwaitPrivilegedStep)] = DataRequestStatus.AwaitingPrivilegedStep,
        [(DataRequestKind.Erasure, DataRequestStatus.Fulfilling, Action.Block)] = DataRequestStatus.Blocked,
        [(DataRequestKind.Erasure, DataRequestStatus.Fulfilling, Action.Fail)] = DataRequestStatus.Failed,
        [(DataRequestKind.Erasure, DataRequestStatus.AwaitingPrivilegedStep, Action.Complete)] = DataRequestStatus.Completed,
        [(DataRequestKind.Closure, DataRequestStatus.Scheduled, Action.StartFulfilling)] = DataRequestStatus.Fulfilling,
        [(DataRequestKind.Closure, DataRequestStatus.Scheduled, Action.Cancel)] = DataRequestStatus.Cancelled,
        [(DataRequestKind.Closure, DataRequestStatus.Fulfilling, Action.Complete)] = DataRequestStatus.Completed,
    };

    private static readonly Dictionary<DataRequestKind, DataRequestStatus[]> Reachable = new()
    {
        [DataRequestKind.Export] = [DataRequestStatus.Requested, DataRequestStatus.Fulfilling, DataRequestStatus.Completed,
            DataRequestStatus.Failed, DataRequestStatus.Cancelled],
        [DataRequestKind.Erasure] = [DataRequestStatus.Scheduled, DataRequestStatus.Fulfilling, DataRequestStatus.AwaitingPrivilegedStep,
            DataRequestStatus.Completed, DataRequestStatus.Blocked, DataRequestStatus.Failed, DataRequestStatus.Cancelled],
        [DataRequestKind.Closure] = [DataRequestStatus.Scheduled, DataRequestStatus.Fulfilling, DataRequestStatus.Completed,
            DataRequestStatus.Cancelled],
    };

    public static TheoryData<DataRequestKind, DataRequestStatus, Action> EveryReachableStatusAndAction()
    {
        var data = new TheoryData<DataRequestKind, DataRequestStatus, Action>();
        foreach (var (kind, statuses) in Reachable)
            foreach (var status in statuses)
                foreach (var action in Enum.GetValues<Action>())
                    data.Add(kind, status, action);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryReachableStatusAndAction))]
    public void Every_action_from_every_reachable_status_takes_exactly_the_ADR_arrow(
        DataRequestKind kind, DataRequestStatus from, Action action)
    {
        var request = Reach(kind, from);
        var version = request.Version;
        var expected = Arrows.TryGetValue((kind, from, action), out var to) ? to : (DataRequestStatus?)null;

        var result = Apply(request, action, ActionTime(request, action));

        result.IsSuccess.Should().Be(expected is not null, $"{kind} {from} --{action}--> is {(expected is null ? "not " : "")}an arrow");
        if (expected is { } next)
        {
            request.Status.Should().Be(next);
            request.Version.Should().Be(version + 1, "every transition is a versioned mutation");
        }
        else
        {
            request.Status.Should().Be(from, "a refused transition changes nothing");
            request.Version.Should().Be(version);
            result.Error!.Code.Should().Be("business_rule_violation", "an expected refusal is a Result, never an exception");
        }
    }

    [Fact]
    public void The_arrow_table_covers_ADR_0106_section_8_and_nothing_else()
    {
        Arrows.Should().HaveCount(13);
        Arrows.Keys.Select(key => key.Item1).Distinct().Should().BeEquivalentTo(Enum.GetValues<DataRequestKind>());
        Arrows.Values.Should().NotContain(DataRequestStatus.Requested).And.NotContain(DataRequestStatus.Scheduled,
            "no arrow re-enters an initial status");
        Arrows.Keys.Where(key => DataRequestCodes.IsTerminal(key.Item2)).Should().BeEmpty("every terminal status is final");
    }

    [Theory]
    [InlineData(DataRequestKind.Export, DataRequestStatus.Requested, false)]
    [InlineData(DataRequestKind.Erasure, DataRequestStatus.Scheduled, true)]
    [InlineData(DataRequestKind.Closure, DataRequestStatus.Scheduled, true)]
    public void A_person_files_each_kind_for_themselves_in_its_initial_status(DataRequestKind kind, DataRequestStatus initial, bool windowed)
    {
        var request = DataSubjectRequest.FileForSelf(NewId(), kind, Subject, Group(), [Current, Tombstone], At).Value!;

        request.Status.Should().Be(initial);
        request.Origin.Should().Be(DataRequestOrigin.Self);
        request.RequesterUserId.Should().Be(Subject);
        request.CreatedBy.Should().Be(Subject);
        request.ExecuteAt.Should().Be(windowed ? At + TimeSpan.FromHours(24) : null, "ADR-0106 § 11's 24-hour window");
        request.TenantId.Should().Be(Tenant);
        request.OrganizationId.Should().Be(Organization);
        request.SnapshotMembershipIds.Should().BeEquivalentTo([Current, Tombstone]);
        request.Progress.Should().BeEmpty();
        request.RetainedReport.Should().BeEmpty();
        request.Version.Should().Be(0);
    }

    [Fact]
    public void An_administrator_files_an_export_or_an_erasure_for_another_person_but_never_a_closure()
    {
        foreach (var kind in new[] { DataRequestKind.Export, DataRequestKind.Erasure })
        {
            var request = DataSubjectRequest.FileForSubject(NewId(), kind, Administrator, Subject, Group(), [Current], At).Value!;
            request.Origin.Should().Be(DataRequestOrigin.Administrator);
            request.RequesterUserId.Should().Be(Administrator);
            request.CreatedBy.Should().Be(Administrator, "the audit actor of an administrator request is the administrator");
            request.SubjectUserId.Should().Be(Subject);
        }

        DataSubjectRequest.FileForSubject(NewId(), DataRequestKind.Closure, Administrator, Subject, Group(), [Current], At)
            .IsFailure.Should().BeTrue("ADR-0106 § 3: an administrator never creates a closure");
        DataSubjectRequest.FileForSubject(NewId(), DataRequestKind.Export, Subject, Subject, Group(), [Current], At)
            .IsFailure.Should().BeTrue("a request for oneself is the self origin");
    }

    [Fact]
    public void A_closure_fan_out_erasure_starts_fulfilling_with_no_window_and_cannot_be_cancelled()
    {
        var request = DataSubjectRequest.FanOutErasure(NewId(), Subject, Group(), [Current], At).Value!;

        request.Kind.Should().Be(DataRequestKind.Erasure);
        request.Origin.Should().Be(DataRequestOrigin.ClosureFanout);
        request.Status.Should().Be(DataRequestStatus.Fulfilling);
        request.ExecuteAt.Should().BeNull();
        request.RequesterUserId.Should().BeNull();
        request.CreatedBy.Should().Be(UserId.SystemActor);
        request.Cancel(At.AddMinutes(1), Subject).IsFailure.Should().BeTrue("closure is irreversible once Begin commits");
        request.StartFulfilling(At.AddMinutes(1), UserId.SystemActor).IsFailure.Should().BeTrue();
        request.AwaitPrivilegedStep(At.AddMinutes(1), UserId.SystemActor).IsSuccess.Should().BeTrue();
        request.Complete([], At.AddMinutes(2), Administrator).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(DataRequestKind.Erasure)]
    [InlineData(DataRequestKind.Closure)]
    public void A_scheduled_request_executes_only_once_its_window_has_passed(DataRequestKind kind)
    {
        var request = DataSubjectRequest.FileForSelf(NewId(), kind, Subject, Group(), [Current], At).Value!;
        var due = request.ExecuteAt!.Value;

        request.StartFulfilling(due.AddTicks(-1), UserId.SystemActor).IsFailure.Should().BeTrue();
        request.Status.Should().Be(DataRequestStatus.Scheduled);
        request.StartFulfilling(due, UserId.SystemActor).IsSuccess.Should().BeTrue();
        request.Status.Should().Be(DataRequestStatus.Fulfilling);
    }

    [Fact]
    public void An_erasure_is_cancellable_only_inside_its_window_and_a_closure_until_its_Begin()
    {
        var erasure = DataSubjectRequest.FileForSelf(NewId(), DataRequestKind.Erasure, Subject, Group(), [Current], At).Value!;
        erasure.Cancel(erasure.ExecuteAt!.Value, Subject).IsFailure.Should().BeTrue("the window ends at the execution time");
        erasure.Cancel(erasure.ExecuteAt!.Value.AddTicks(-1), Subject).IsSuccess.Should().BeTrue();

        var closure = DataSubjectRequest.FileForSelf(NewId(), DataRequestKind.Closure, Subject, Group(), [Current], At).Value!;
        closure.Cancel(closure.ExecuteAt!.Value.AddHours(3), Subject).IsSuccess.Should().BeTrue(
            "a closure is cancellable until its Begin commits, however late");
    }

    [Theory]
    [InlineData(DataRequestRefusal.BlockedHold, true)]
    [InlineData(DataRequestRefusal.BlockedLastAdministrator, true)]
    [InlineData(DataRequestRefusal.BlockedWindow, false)]
    public void An_erasure_blocks_only_with_a_re_check_refusal(DataRequestRefusal refusal, bool admitted)
    {
        var request = Reach(DataRequestKind.Erasure, DataRequestStatus.Fulfilling);
        request.Block(refusal, ActionTime(request, Action.Block), UserId.SystemActor).IsSuccess.Should().Be(admitted);
        if (admitted) request.LastRefusal.Should().Be(refusal);
    }

    [Fact]
    public void Export_too_large_is_an_export_failure_only()
    {
        var erasure = Reach(DataRequestKind.Erasure, DataRequestStatus.Fulfilling);
        erasure.Fail(DataRequestFailureReason.ExportTooLarge, ActionTime(erasure, Action.Fail), UserId.SystemActor)
            .IsFailure.Should().BeTrue();
        erasure.Fail(DataRequestFailureReason.ExecutionFailed, ActionTime(erasure, Action.Fail), UserId.SystemActor)
            .IsSuccess.Should().BeTrue();

        var export = Reach(DataRequestKind.Export, DataRequestStatus.Fulfilling);
        export.Fail(DataRequestFailureReason.ExportTooLarge, ActionTime(export, Action.Fail), UserId.SystemActor).IsSuccess.Should().BeTrue();
        export.FailureReason.Should().Be(DataRequestFailureReason.ExportTooLarge);
        export.Fail((DataRequestFailureReason)99, At.AddDays(3), UserId.SystemActor).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void A_refusal_is_recorded_on_a_scheduled_request_only_and_repeats_are_no_ops()
    {
        var closure = DataSubjectRequest.FileForSelf(NewId(), DataRequestKind.Closure, Subject, Group(), [Current], At).Value!;
        closure.RecordRefusal(DataRequestRefusal.BlockedWindow, At.AddMinutes(1), Administrator).IsSuccess.Should().BeTrue();
        var version = closure.Version;
        closure.RecordRefusal(DataRequestRefusal.BlockedWindow, At.AddMinutes(2), Administrator).IsSuccess.Should().BeTrue();
        closure.Version.Should().Be(version, "recording the same refusal again changes nothing");
        closure.RecordRefusal(DataRequestRefusal.BlockedLastAdministrator, At.AddMinutes(3), Administrator).IsSuccess.Should().BeTrue();
        closure.LastRefusal.Should().Be(DataRequestRefusal.BlockedLastAdministrator);
        closure.Status.Should().Be(DataRequestStatus.Scheduled);

        var export = Reach(DataRequestKind.Export, DataRequestStatus.Fulfilling);
        export.RecordRefusal(DataRequestRefusal.BlockedHold, At.AddDays(1), Administrator).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Progress_records_each_participant_once_and_only_while_fulfilling()
    {
        var request = Reach(DataRequestKind.Erasure, DataRequestStatus.Fulfilling);
        var at = ActionTime(request, Action.AwaitPrivilegedStep);
        request.RecordParticipantCompleted("identity", at, UserId.SystemActor).IsSuccess.Should().BeTrue();
        var version = request.Version;
        request.RecordParticipantCompleted("identity", at.AddMinutes(1), UserId.SystemActor).IsSuccess.Should().BeTrue();
        request.Version.Should().Be(version, "a resumed job re-recording a participant changes nothing");
        request.RecordParticipantCompleted("tenancy", at.AddMinutes(1), UserId.SystemActor).IsSuccess.Should().BeTrue();
        request.Progress.Should().BeEquivalentTo(new Dictionary<string, DateTimeOffset>
        {
            ["identity"] = at,
            ["tenancy"] = at.AddMinutes(1),
        });
        foreach (var malformed in new[] { "", "Identity", "1identity", "identity-module", new string('a', 33) })
            request.RecordParticipantCompleted(malformed, at.AddMinutes(2), UserId.SystemActor).IsFailure.Should().BeTrue(malformed);

        request.AwaitPrivilegedStep(at.AddMinutes(3), UserId.SystemActor).IsSuccess.Should().BeTrue();
        request.RecordParticipantCompleted("audit", at.AddMinutes(4), UserId.SystemActor).IsFailure.Should().BeTrue(
            "progress closes when the request leaves fulfilling");
    }

    [Fact]
    public void The_retained_report_is_a_closed_kind_to_count_map()
    {
        var request = Reach(DataRequestKind.Erasure, DataRequestStatus.Fulfilling);
        var at = ActionTime(request, Action.AwaitPrivilegedStep);
        request.RecordRetained([new(RetainedReferenceKind.ConsentEvidence, 2), new(RetainedReferenceKind.AttributionReference, 7)],
            at, UserId.SystemActor).IsSuccess.Should().BeTrue();
        request.ResultJson.Should().Be("{\"attribution_reference\":7,\"consent_evidence\":2}", "sorted closed codes and counts, nothing else");
        request.RecordRetained([new(RetainedReferenceKind.ConsentEvidence, 1), new(RetainedReferenceKind.ConsentEvidence, 1)],
            at.AddMinutes(1), UserId.SystemActor).IsFailure.Should().BeTrue("a kind is reported once");
        request.AwaitPrivilegedStep(at.AddMinutes(2), UserId.SystemActor).IsSuccess.Should().BeTrue();
        request.Complete([new(RetainedReferenceKind.AuditActorReference, 3), new(RetainedReferenceKind.ConsentEvidence, 2)],
            at.AddMinutes(3), Administrator).IsSuccess.Should().BeTrue();
        request.RetainedReport.Should().BeEquivalentTo(new Dictionary<RetainedReferenceKind, long>
        {
            [RetainedReferenceKind.AuditActorReference] = 3,
            [RetainedReferenceKind.ConsentEvidence] = 2,
        });
        request.CompletedAt.Should().Be(at.AddMinutes(3));
        FluentActions.Invoking(() => new RetainedReferenceCount(RetainedReferenceKind.ExternalCopy, -1))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Every_legal_path_leaves_the_snapshot_exactly_as_filed()
    {
        foreach (var (kind, statuses) in Reachable)
            foreach (var status in statuses)
            {
                var request = Reach(kind, status, out var filed);
                Snapshot(request).Should().Be(Snapshot(filed), $"{kind} reaching {status} keeps its snapshot");
            }
    }

    [Fact]
    public void The_snapshot_members_have_no_setter_outside_the_aggregate()
    {
        string[] snapshot =
        [
            nameof(DataSubjectRequest.TenantId), nameof(DataSubjectRequest.OrganizationId), nameof(DataSubjectRequest.Kind),
            nameof(DataSubjectRequest.Origin), nameof(DataSubjectRequest.SubjectUserId), nameof(DataSubjectRequest.RequesterUserId),
            nameof(DataSubjectRequest.MembershipSnapshotJson), nameof(DataSubjectRequest.ExecuteAt),
        ];
        foreach (var name in snapshot)
        {
            var setter = typeof(DataSubjectRequest).GetProperty(name)!.GetSetMethod(nonPublic: true);
            setter.Should().NotBeNull();
            setter!.IsPrivate.Should().BeTrue($"{name} is set by a factory only");
        }

        typeof(DataSubjectRequest).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName).Select(method => method.Name).Distinct()
            .Should().BeEquivalentTo(["StartFulfilling", "AwaitPrivilegedStep", "Complete", "Fail", "Block", "Cancel",
                "RecordRefusal", "RecordParticipantCompleted", "RecordRetained"],
                "the transitions and the two records are the only mutators");
    }

    [Fact]
    public void Creation_refuses_an_empty_duplicated_or_unbounded_snapshot_and_unreal_people()
    {
        DataSubjectRequest.FileForSelf(NewId(), DataRequestKind.Export, Subject, Group(), [], At).IsFailure.Should().BeTrue();
        DataSubjectRequest.FileForSelf(NewId(), DataRequestKind.Export, Subject, Group(), [Current, Current], At).IsFailure.Should().BeTrue();
        DataSubjectRequest.FileForSelf(NewId(), DataRequestKind.Export, Subject, Group(),
            Enumerable.Range(0, 257).Select(_ => MembershipId.From(Guid.CreateVersion7())).ToArray(), At).IsFailure.Should().BeTrue();
        DataSubjectRequest.FileForSelf(NewId(), DataRequestKind.Export, UserId.SystemActor, Group(), [Current], At).IsFailure.Should().BeTrue();
        DataSubjectRequest.FileForSelf(DataSubjectRequestId.From(Guid.NewGuid()), DataRequestKind.Export, Subject, Group(), [Current], At)
            .IsFailure.Should().BeTrue("identifiers are UUIDv7");
        DataSubjectRequest.FileForSelf(NewId(), DataRequestKind.Export, Subject, Group(), [Current], At.ToOffset(TimeSpan.FromHours(3)))
            .IsFailure.Should().BeTrue("instants are UTC");
        DataSubjectRequest.FileForSelf(NewId(), (DataRequestKind)0, Subject, Group(), [Current], At).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void A_transition_refuses_a_stamp_earlier_than_the_last_one()
    {
        var request = DataSubjectRequest.FileForSelf(NewId(), DataRequestKind.Export, Subject, Group(), [Current], At).Value!;
        request.StartFulfilling(At.AddTicks(-1), UserId.SystemActor).IsFailure.Should().BeTrue();
        request.StartFulfilling(At, UserId.SystemActor).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void The_closed_codes_are_the_ADR_spellings()
    {
        DataRequestCodes.Kinds.Codes.Should().Equal("export", "erasure", "closure");
        DataRequestCodes.Statuses.Codes.Should().Equal("requested", "scheduled", "fulfilling", "awaiting_privileged_step",
            "completed", "blocked", "failed", "cancelled");
        DataRequestCodes.Origins.Codes.Should().Equal("self", "administrator", "closure_fanout");
        DataRequestCodes.Refusals.Codes.Should().Equal("blocked_window", "blocked_hold", "blocked_last_administrator");
        DataRequestCodes.FailureReasons.Codes.Should().Equal("export_too_large", "execution_failed");
        DataRequestCodes.ArtifactStatuses.Codes.Should().Equal("available", "revoked", "expired", "purged");
        DataRequestCodes.RetainedKinds.Codes.Should().Equal("consent_evidence", "invitation_record", "invitation_envelope",
            "audit_actor_reference", "audit_entity_reference", "audit_metadata_reference", "unscoped_audit_rows",
            "transport_evidence", "execution_evidence", "provider_event_record", "idempotency_cache", "attribution_reference",
            "federation_actor_reference", "request_receipt", "external_copy", "unserved_group");
        DataRequestCodes.NonTerminal.Should().BeEquivalentTo([DataRequestStatus.Requested, DataRequestStatus.Scheduled,
            DataRequestStatus.Fulfilling, DataRequestStatus.AwaitingPrivilegedStep]);
        DataRequestCodes.Statuses.TryParse("AwaitingPrivilegedStep", out _).Should().BeFalse("member names are not codes");
        DataRequestCodes.Statuses.TryParse("Completed", out _).Should().BeFalse("codes are case-sensitive");
        FluentActions.Invoking(() => DataRequestCodes.Kinds.Parse("delete")).Should().Throw<FormatException>();
        FluentActions.Invoking(() => DataRequestCodes.Kinds.ToCode((DataRequestKind)0)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Stored_documents_are_read_strictly()
    {
        FluentActions.Invoking(() => DataRequestDocuments.ReadMemberships("[]")).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => DataRequestDocuments.ReadMemberships("[\"" + Guid.NewGuid() + "\"]"))
            .Should().Throw<InvalidOperationException>("only UUIDv7 Membership identifiers");
        FluentActions.Invoking(() => DataRequestDocuments.ReadMemberships("{}")).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => DataRequestDocuments.ReadProgress("{\"identity\":\"yesterday\"}")).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => DataRequestDocuments.ReadResult("{\"anonymous\":1}"))
            .Should().Throw<InvalidOperationException>("nothing is reported outside the closed kinds");
        FluentActions.Invoking(() => DataRequestDocuments.ReadResult("{\"consent_evidence\":-1}")).Should().Throw<InvalidOperationException>();
        // jsonb re-spaces and reorders what it stores; the reader accepts that form.
        var current = Current.Value.ToString("D");
        DataRequestDocuments.ReadMemberships($"[\"{current}\"]").Should().Equal(Current);
        DataRequestDocuments.ReadProgress("{\"identity\": \"2026-11-22T09:00:00.0000000Z\"}")["identity"].Should().Be(At);
    }

    private static DataSubjectRequest Reach(DataRequestKind kind, DataRequestStatus status) => Reach(kind, status, out _);

    /// <summary>Walks the legal path to a status, from a freshly filed request whose snapshot copy is returned.</summary>
    private static DataSubjectRequest Reach(DataRequestKind kind, DataRequestStatus status, out DataSubjectRequest filed)
    {
        var id = NewId();
        filed = DataSubjectRequest.FileForSelf(id, kind, Subject, Group(), [Current, Tombstone], At).Value!;
        var request = DataSubjectRequest.FileForSelf(id, kind, Subject, Group(), [Current, Tombstone], At).Value!;
        Action[] path = (kind, status) switch
        {
            (_, DataRequestStatus.Requested or DataRequestStatus.Scheduled) => [],
            (_, DataRequestStatus.Cancelled) => [Action.Cancel],
            (_, DataRequestStatus.Fulfilling) => [Action.StartFulfilling],
            (DataRequestKind.Erasure, DataRequestStatus.AwaitingPrivilegedStep) => [Action.StartFulfilling, Action.AwaitPrivilegedStep],
            (DataRequestKind.Erasure, DataRequestStatus.Completed) => [Action.StartFulfilling, Action.AwaitPrivilegedStep, Action.Complete],
            (_, DataRequestStatus.Completed) => [Action.StartFulfilling, Action.Complete],
            (_, DataRequestStatus.Blocked) => [Action.StartFulfilling, Action.Block],
            (_, DataRequestStatus.Failed) => [Action.StartFulfilling, Action.Fail],
            _ => throw new InvalidOperationException("No legal path is declared for this status."),
        };
        foreach (var step in path)
            Apply(request, step, ActionTime(request, step)).IsSuccess.Should().BeTrue($"{kind} path to {status} at {step}");
        request.Status.Should().Be(status);
        return request;
    }

    private static DateTimeOffset ActionTime(DataSubjectRequest request, Action action) =>
        action == Action.StartFulfilling && request.ExecuteAt is { } due && request.Status == DataRequestStatus.Scheduled
            ? due
            : (request.UpdatedAt ?? request.CreatedAt).AddMinutes(1);

    private static LearnStack.SharedKernel.Results.Result<LearnStack.SharedKernel.Results.None> Apply(
        DataSubjectRequest request, Action action, DateTimeOffset at) => action switch
        {
            Action.StartFulfilling => request.StartFulfilling(at, UserId.SystemActor),
            Action.AwaitPrivilegedStep => request.AwaitPrivilegedStep(at, UserId.SystemActor),
            Action.Complete => request.Complete([], at, UserId.SystemActor),
            Action.Fail => request.Fail(DataRequestFailureReason.ExecutionFailed, at, UserId.SystemActor),
            Action.Block => request.Block(DataRequestRefusal.BlockedHold, at, UserId.SystemActor),
            _ => request.Cancel(at, Subject),
        };

    private static string Snapshot(DataSubjectRequest request) => string.Join("|", request.Id, request.TenantId,
        request.OrganizationId, request.Kind, request.Origin, request.SubjectUserId, request.RequesterUserId,
        request.MembershipSnapshotJson, request.ExecuteAt, request.CreatedAt, request.CreatedBy);

    private static DataRequestScopeGroup Group() => new(Tenant, Organization);

    private static DataSubjectRequestId NewId() => DataSubjectRequestId.From(Guid.CreateVersion7());
}
