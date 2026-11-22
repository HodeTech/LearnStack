using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

/// <summary>
/// The data-request handler cores of ADR-0106 and ADR-0107 § 4 against in-memory ports: who reaches which request,
/// the creation fences and refusals, the conditional mutations, and the ticket and chunk rules.
/// </summary>
public sealed class DataRequestServiceTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly UserId Subject = UserId.From(Guid.CreateVersion7());
    private static readonly UserId Administrator = UserId.From(Guid.CreateVersion7());
    private static readonly MembershipId SubjectMembership = MembershipId.From(Guid.CreateVersion7());
    private static readonly MembershipId AdministratorMembership = MembershipId.From(Guid.CreateVersion7());

    [Fact]
    public async Task A_subject_files_for_themselves_under_their_User_fence_and_the_job_is_submitted_on_the_same_transaction()
    {
        var harness = new Harness();
        var result = await harness.Service.CreateExportAsync(harness.Caller(Subject, DataRequestRole.Subject), Subject, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var request = harness.Store.Requests.Should().ContainSingle().Subject;
        request.Origin.Should().Be(DataRequestOrigin.Self);
        request.SnapshotMembershipIds.Should().Equal(SubjectMembership);
        harness.Locks.Acquired.Should().ContainSingle().Which.UserIds.Should().Equal(Subject);
        harness.Locks.Acquired[0].TenantMode.Should().Be(IdentityTenantLockMode.Shared);
        harness.Scheduler.Submitted.Should().Equal(request.Id);
        harness.Audit.Designated.Should().Contain(request);
        harness.Rechecks.Should().Be(1, "authority is re-evaluated once the fences are held");
    }

    [Fact]
    public async Task An_administrator_files_for_a_member_never_for_themselves_and_an_unknown_Membership_is_absent()
    {
        var harness = new Harness();
        var caller = harness.Caller(Administrator, DataRequestRole.Administrator);

        Code(await harness.Service.CreateExportForMembershipAsync(caller, AdministratorMembership.Value, CancellationToken.None))
            .Should().Be("business_rule_violation");
        Code(await harness.Service.CreateExportForMembershipAsync(caller, Guid.CreateVersion7(), CancellationToken.None))
            .Should().Be("not_found");
        Code(await harness.Service.CreateExportForMembershipAsync(harness.Caller(Subject, DataRequestRole.Subject),
            SubjectMembership.Value, CancellationToken.None)).Should().Be("forbidden");
        harness.Store.Requests.Should().BeEmpty();

        (await harness.Service.CreateExportForMembershipAsync(caller, SubjectMembership.Value, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var request = harness.Store.Requests.Should().ContainSingle().Subject;
        request.Origin.Should().Be(DataRequestOrigin.Administrator);
        request.RequesterUserId.Should().Be(Administrator);
        request.SubjectUserId.Should().Be(Subject);
    }

    [Theory]
    [InlineData("eligibility")]
    [InlineData("index")]
    public async Task A_second_active_export_is_refused_as_request_already_active_whether_judged_or_caught_by_the_index(string path)
    {
        var harness = new Harness();
        if (path == "eligibility")
        {
            (await harness.Service.CreateExportAsync(harness.Caller(Subject, DataRequestRole.Subject), Subject, CancellationToken.None))
                .IsSuccess.Should().BeTrue();
        }
        else
        {
            harness.Store.RejectAdd = true;
        }

        var refused = await harness.Service.CreateExportAsync(harness.Caller(Subject, DataRequestRole.Subject), Subject, CancellationToken.None);

        Code(refused).Should().Be("business_rule_violation");
        refused.Error!.Details!["$"].Single().Key.Should().Be("lockey_request_already_active");
        harness.Scheduler.Submitted.Should().HaveCount(path == "eligibility" ? 1 : 0, "a refused creation submits nothing");
    }

    [Fact]
    public async Task A_dead_jobs_request_is_stored_failed_under_the_held_fences_before_the_one_active_rule_is_judged()
    {
        var harness = new Harness();
        await harness.Service.CreateExportAsync(harness.Caller(Subject, DataRequestRole.Subject), Subject, CancellationToken.None);
        var dead = harness.Store.Requests.Single();
        harness.Phases.Phase[dead.Id] = DataRequestJobPhase.Dead;
        var next = new Harness(harness.Store, harness.Phases);

        (await next.Service.CreateExportAsync(next.Caller(Subject, DataRequestRole.Subject), Subject, CancellationToken.None)).IsSuccess.Should().BeTrue();

        dead.Status.Should().Be(DataRequestStatus.Failed);
        dead.FailureReason.Should().Be(DataRequestFailureReason.ExecutionFailed);
        next.Locks.Acquired.Should().ContainSingle("the reconciler proves the held plan instead of taking a second one");
        next.Store.Requests.Should().HaveCount(2);
    }

    /// <summary>
    /// An administrator's export would withdraw the subject's own readable artifact when it stores its own, so it is
    /// refused while the subject holds one in the group, whoever filed it; once that artifact is revoked or expired the
    /// administrator may file. The subject's own new export still replaces their previous artifact.
    /// </summary>
    [Theory]
    [InlineData(DataRequestOrigin.Self)]
    [InlineData(DataRequestOrigin.Administrator)]
    public async Task An_administrator_export_is_refused_while_the_subject_holds_a_readable_artifact(DataRequestOrigin earlier)
    {
        var harness = new Harness();
        var completed = harness.Completed(earlier);
        var administrator = harness.Caller(Administrator, DataRequestRole.Administrator);

        var refused = await harness.Service.CreateExportForMembershipAsync(administrator, SubjectMembership.Value, CancellationToken.None);

        Code(refused).Should().Be("business_rule_violation");
        refused.Error!.Details!["$"].Single().Key.Should().Be("lockey_subject_export_available");
        harness.Store.Requests.Should().ContainSingle("nothing was filed").Which.Should().BeSameAs(completed);
        harness.Scheduler.Submitted.Should().BeEmpty();

        (await harness.Service.CreateExportAsync(harness.Caller(Subject, DataRequestRole.Subject), Subject, CancellationToken.None))
            .IsSuccess.Should().BeTrue("the subject's own export replaces their previous artifact");
        harness.Store.Requests.Remove(harness.Store.Requests[^1]);

        // Revoked or past its expiry, the artifact is no longer readable: the administrator may file.
        harness.Store.Artifacts[completed.Id] = harness.Store.Artifacts[completed.Id] with { Readable = false };
        (await harness.Service.CreateExportForMembershipAsync(administrator, SubjectMembership.Value, CancellationToken.None))
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_hold_refuses_creation_with_its_closed_code_and_files_nothing()
    {
        var harness = new Harness(hold: true);
        var refused = await harness.Service.CreateExportAsync(harness.Caller(Subject, DataRequestRole.Subject), Subject, CancellationToken.None);

        refused.Error!.Details!["$"].Single().Key.Should().Be("lockey_blocked_hold");
        harness.Store.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Authority_lost_while_waiting_for_the_fences_refuses_the_creation()
    {
        var harness = new Harness { RecheckResult = Result<None>.Fail(new Error(LearnStack.SharedKernel.Localization.LocalizedMessage.Of("lockey_forbidden"))) };
        Code(await harness.Service.CreateExportAsync(harness.Caller(Subject, DataRequestRole.Subject), Subject, CancellationToken.None))
            .Should().Be("forbidden");
        harness.Store.Requests.Should().BeEmpty();
        harness.Scheduler.Submitted.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_needs_the_current_version_and_reaches_only_the_subject_or_the_filing_administrator()
    {
        var harness = new Harness();
        await harness.Service.CreateExportForMembershipAsync(harness.Caller(Administrator, DataRequestRole.Administrator),
            SubjectMembership.Value, CancellationToken.None);
        var request = harness.Store.Requests.Single();
        var other = UserId.From(Guid.CreateVersion7());

        Code(await harness.Service.CancelAsync(harness.Caller(other, DataRequestRole.Administrator), request.Id.Value, 0, CancellationToken.None))
            .Should().Be("not_found", "another administrator did not file it");
        Code(await harness.Service.CancelAsync(harness.Caller(other, DataRequestRole.Subject), request.Id.Value, 0, CancellationToken.None))
            .Should().Be("not_found", "another person is not its subject");
        Code(await harness.Service.CancelAsync(harness.Caller(Subject, DataRequestRole.Subject), request.Id.Value, 5, CancellationToken.None))
            .Should().Be("concurrency_conflict");

        var cancelled = await harness.Service.CancelAsync(harness.Caller(Administrator, DataRequestRole.Administrator), request.Id.Value, 0,
            CancellationToken.None);
        cancelled.Value!.Version.Should().Be(1);
        request.Status.Should().Be(DataRequestStatus.Cancelled);
        Code(await harness.Service.CancelAsync(harness.Caller(Subject, DataRequestRole.Subject), request.Id.Value, 1, CancellationToken.None))
            .Should().Be("business_rule_violation", "a terminal request accepts no transition");
    }

    /// <summary>
    /// A subject revokes only an artifact about them: another person's request, named with that artifact's real version,
    /// answers exactly as an absent one and purges nothing; the owner's own revocation purges.
    /// </summary>
    [Fact]
    public async Task A_subject_revokes_only_an_artifact_about_them_and_another_persons_is_not_found_and_purges_nothing()
    {
        var harness = new Harness();
        var request = harness.Completed();
        var artifact = harness.Store.Artifacts[request.Id].Artifact;
        var other = UserId.From(Guid.CreateVersion7());

        Code(await harness.Service.RevokeAsync(harness.Caller(other, DataRequestRole.Subject), request.Id.Value, artifact.Version,
            CancellationToken.None)).Should().Be("not_found", "another person's artifact is no more reachable than an absent request");
        Code(await harness.Service.RevokeAsync(harness.Caller(Administrator, DataRequestRole.Administrator), request.Id.Value,
            artifact.Version, CancellationToken.None)).Should().Be("not_found", "an administrator revokes only an export they filed");
        harness.Artifacts.Purged.Should().BeEmpty("a refused revocation deletes nothing");
        harness.Audit.Designated.Should().NotContain(artifact, "no artifact is named for the audit row of a request the caller does not reach");

        (await harness.Service.RevokeAsync(harness.Caller(Subject, DataRequestRole.Subject), request.Id.Value, artifact.Version,
            CancellationToken.None)).IsSuccess.Should().BeTrue();
        harness.Artifacts.Purged.Should().Equal(artifact.Id.Value);
    }

    [Fact]
    public async Task Only_the_subject_mints_a_ticket_in_a_bound_session_over_a_readable_artifact_and_only_its_digest_is_stored()
    {
        var harness = new Harness();
        var request = harness.Completed(origin: DataRequestOrigin.Administrator);
        var subject = harness.Caller(Subject, DataRequestRole.Subject);

        Code(await harness.Download.MintTicketAsync(harness.Caller(Administrator, DataRequestRole.Administrator), request.Id.Value,
            CancellationToken.None)).Should().Be("forbidden", "an administrator never gets a ticket");
        Code(await harness.Download.MintTicketAsync(harness.Caller(Administrator, DataRequestRole.Subject), request.Id.Value,
            CancellationToken.None)).Should().Be("not_found", "the self path reaches no request about another person");
        harness.Sessions.Binding = null;
        Code(await harness.Download.MintTicketAsync(subject, request.Id.Value, CancellationToken.None)).Should().Be("forbidden");
        harness.Sessions.Binding = Binding('a');
        harness.Store.Artifacts[request.Id] = harness.Store.Artifacts[request.Id] with { Readable = false };
        (await harness.Download.MintTicketAsync(subject, request.Id.Value, CancellationToken.None)).Error!.Details!["artifact"].Single().Key
            .Should().Be("lockey_data_export_unavailable");
        harness.Store.Artifacts[request.Id] = harness.Store.Artifacts[request.Id] with { Readable = true };

        var grant = await harness.Download.MintTicketAsync(subject, request.Id.Value, CancellationToken.None);

        grant.IsSuccess.Should().BeTrue();
        var ticket = harness.Store.Tickets.Should().ContainSingle().Subject;
        ticket.Digest.Should().Be(DataExportTicketCodec.TryDigest(grant.Value!.Ticket));
        ticket.SessionBindingDigest.Should().Be(Binding('a'));
        ticket.ExpiresAt.Should().Be(At + DataRequestLimits.TicketLifetime);
        grant.Value.ChunkCount.Should().Be(2);
        grant.Value.ToString().Should().NotContain(grant.Value.Ticket);
        harness.Audit.Designated.Should().Contain(ticket);
    }

    /// <summary>
    /// Ticket and MUST audit rows are permanent, so minting is bounded and a bound reached is its own refusal,
    /// <c>data_export_ticket_limit</c>, never the <c>data_export_unavailable</c> of an artifact that is gone: at most
    /// three open tickets per session binding (a consumed or expired one frees a slot, and a ticket left open by a
    /// credential since refreshed does not block the new one), and at most twenty ever per artifact, which is final.
    /// </summary>
    [Fact]
    public async Task Minting_is_bounded_per_session_binding_and_per_artifact_and_a_bound_is_refused_as_ticket_limit()
    {
        var harness = new Harness();
        var request = harness.Completed();
        var subject = harness.Caller(Subject, DataRequestRole.Subject);
        var tickets = new List<string>();
        for (var index = 0; index < DataRequestLimits.MaximumOpenTicketsPerBinding; index++)
        {
            tickets.Add((await harness.Download.MintTicketAsync(subject, request.Id.Value, CancellationToken.None)).Value!.Ticket);
        }

        var refused = await harness.Download.MintTicketAsync(subject, request.Id.Value, CancellationToken.None);
        Code(refused).Should().Be("business_rule_violation");
        refused.Error!.Details!["artifact"].Single().Key.Should().Be("lockey_data_export_ticket_limit",
            "a bound reached is told apart from an artifact that is gone");
        harness.Store.Tickets.Should().HaveCount(3, "a refused mint stores nothing");

        // A refreshed credential is another binding: the orphaned open tickets of the old one do not block it.
        harness.Sessions.Binding = Binding('b');
        (await harness.Download.MintTicketAsync(subject, request.Id.Value, CancellationToken.None)).IsSuccess.Should().BeTrue();
        harness.Sessions.Binding = Binding('a');

        // Reading the last chunk consumes the first ticket: one slot of its binding is free again.
        (await harness.Download.ReadChunkAsync(subject, request.Id.Value, tickets[0], 1, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await harness.Download.MintTicketAsync(subject, request.Id.Value, CancellationToken.None)).IsSuccess.Should().BeTrue();
        Code(await harness.Download.MintTicketAsync(subject, request.Id.Value, CancellationToken.None)).Should().Be("business_rule_violation");

        // Past their fifteen minutes the open tickets no longer count, but every minted ticket does, up to the hard bound.
        var bindings = 0;
        while (harness.Store.Tickets.Count < DataRequestLimits.MaximumTicketsPerArtifact)
        {
            harness.Sessions.Binding = (++bindings).ToString("x64", System.Globalization.CultureInfo.InvariantCulture);
            (await harness.Download.MintTicketAsync(subject, request.Id.Value, CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        harness.Store.Now = () => At + DataRequestLimits.TicketLifetime;
        harness.Sessions.Binding = Binding('f');
        var final = await harness.Download.MintTicketAsync(subject, request.Id.Value, CancellationToken.None);
        final.Error!.Details!["artifact"].Single().Key.Should().Be("lockey_data_export_ticket_limit",
            "twenty tickets ever per artifact is final, whatever their state or binding");
        harness.Store.Tickets.Should().HaveCount(DataRequestLimits.MaximumTicketsPerArtifact);
    }

    [Fact]
    public async Task A_ticket_is_refused_as_unavailable_when_the_ring_no_longer_holds_the_artifacts_key()
    {
        var harness = new Harness();
        var request = harness.Completed();
        harness.Artifacts.Ring.Clear();

        var refused = await harness.Download.MintTicketAsync(harness.Caller(Subject, DataRequestRole.Subject), request.Id.Value,
            CancellationToken.None);

        refused.Error!.Details!["artifact"].Single().Key.Should().Be("lockey_data_export_unavailable",
            "a download that could never be served is refused before any ticket exists");
        harness.Store.Tickets.Should().BeEmpty();
    }

    [Fact]
    public async Task A_chunk_reads_only_through_its_own_session_and_the_final_chunk_consumes_the_ticket()
    {
        var harness = new Harness();
        var request = harness.Completed();
        var subject = harness.Caller(Subject, DataRequestRole.Subject);
        var ticket = (await harness.Download.MintTicketAsync(subject, request.Id.Value, CancellationToken.None)).Value!.Ticket;

        harness.Sessions.Binding = Binding('b');
        Code(await harness.Download.ReadChunkAsync(subject, request.Id.Value, ticket, 0, CancellationToken.None))
            .Should().Be("not_found", "another session's credential does not bind this ticket");
        harness.Sessions.Binding = Binding('a');
        var forged = (ticket[0] == 'A' ? 'B' : 'A') + ticket[1..];
        Code(await harness.Download.ReadChunkAsync(subject, request.Id.Value, forged, 0, CancellationToken.None)).Should().Be("not_found");
        Code(await harness.Download.ReadChunkAsync(subject, request.Id.Value, ticket + "=", 0, CancellationToken.None)).Should().Be("validation_failed");
        Code(await harness.Download.ReadChunkAsync(subject, request.Id.Value, ticket, 2, CancellationToken.None)).Should().Be("not_found");
        Code(await harness.Download.ReadChunkAsync(harness.Caller(Subject, DataRequestRole.Administrator), request.Id.Value, ticket, 0,
            CancellationToken.None)).Should().Be("forbidden");

        var first = await harness.Download.ReadChunkAsync(subject, request.Id.Value, ticket, 0, CancellationToken.None);
        first.Value!.Index.Should().Be(0);
        harness.Store.Tickets.Single().ConsumedAt.Should().BeNull();
        harness.Store.TicketSaves.Should().Be(0);
        var last = await harness.Download.ReadChunkAsync(subject, request.Id.Value, ticket, 1, CancellationToken.None);
        last.Value!.Count.Should().Be(2);
        harness.Store.Tickets.Single().ConsumedAt.Should().Be(At);
        harness.Store.TicketSaves.Should().Be(1);
        Code(await harness.Download.ReadChunkAsync(subject, request.Id.Value, ticket, 0, CancellationToken.None))
            .Should().Be("not_found", "a consumed cursor reads nothing more");
        harness.Audit.Designated.Should().Contain(harness.Store.Tickets.Single());
    }

    [Fact]
    public async Task An_expired_ticket_never_reaches_the_plaintext_and_a_failed_consumption_clears_the_decrypted_chunk()
    {
        var harness = new Harness();
        var request = harness.Completed();
        var subject = harness.Caller(Subject, DataRequestRole.Subject);
        var ticket = (await harness.Download.MintTicketAsync(subject, request.Id.Value, CancellationToken.None)).Value!.Ticket;
        harness.Clock.Now = At + DataRequestLimits.TicketLifetime;

        Code(await harness.Download.ReadChunkAsync(subject, request.Id.Value, ticket, 1, CancellationToken.None)).Should().Be("not_found");
        harness.Artifacts.Served.Should().BeEmpty("an expired ticket never reaches the plaintext");

        harness.Clock.Now = At;
        harness.Store.FailConsumption = true;
        await FluentActions.Awaiting(() => harness.Download.ReadChunkAsync(subject, request.Id.Value, ticket, 1, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
        harness.Artifacts.Served.Should().ContainSingle().Which.Should().OnlyContain(value => value == 0, "the decrypted chunk is zeroed");
    }

    [Fact]
    public async Task Receipts_show_a_dead_job_as_failed_an_overdue_artifact_as_expired_and_Memberships_only_to_an_administrator()
    {
        var harness = new Harness();
        await harness.Service.CreateExportAsync(harness.Caller(Subject, DataRequestRole.Subject), Subject, CancellationToken.None);
        var requested = harness.Store.Requests.Single();
        harness.Phases.Phase[requested.Id] = DataRequestJobPhase.Dead;

        var own = (await harness.Service.GetAsync(harness.Caller(Subject, DataRequestRole.Subject), requested.Id.Value, CancellationToken.None)).Value!;
        own.Status.Should().Be("failed");
        own.FailureReason.Should().Be("execution_failed");
        own.MembershipIds.Should().BeNull();
        requested.Status.Should().Be(DataRequestStatus.Requested, "displaying failed writes nothing");

        var admin = (await harness.Service.GetAsync(harness.Caller(Administrator, DataRequestRole.Administrator), requested.Id.Value,
            CancellationToken.None)).Value!;
        admin.MembershipIds.Should().Equal(SubjectMembership.Value);
        admin.RequestedByCaller.Should().BeFalse();

        var other = new Harness();
        var completed = other.Completed();
        other.Store.Artifacts[completed.Id] = other.Store.Artifacts[completed.Id] with { Readable = false };
        var view = (await other.Service.GetAsync(other.Caller(Subject, DataRequestRole.Subject), completed.Id.Value, CancellationToken.None)).Value!;
        view.Artifact!.Status.Should().Be("expired");
        view.Artifact.Readable.Should().BeFalse();
        Code(await other.Service.GetAsync(other.Caller(UserId.From(Guid.CreateVersion7()), DataRequestRole.Subject), completed.Id.Value,
            CancellationToken.None)).Should().Be("not_found");
    }

    /// <summary>
    /// Only a stored requested export can be displayed failed, so only its job phase is read: a completed export, or
    /// one stored failed or cancelled, never changes and costs no job read.
    /// </summary>
    [Fact]
    public async Task Receipts_read_the_job_phase_only_of_stored_requested_exports()
    {
        var harness = new Harness();
        var completed = harness.Completed();
        await harness.Service.CreateExportAsync(harness.Caller(Subject, DataRequestRole.Subject), Subject, CancellationToken.None);
        var requested = harness.Store.Requests.Single(request => request.Status == DataRequestStatus.Requested);
        var subject = harness.Caller(Subject, DataRequestRole.Subject);

        (await harness.Service.GetAsync(subject, completed.Id.Value, CancellationToken.None)).IsSuccess.Should().BeTrue();
        harness.Phases.Read.Should().BeEmpty("a completed export's receipt reads no job");
        (await harness.Service.ListAsync(subject, "identity.data_request.list_own", null, 20, CancellationToken.None)).IsSuccess.Should().BeTrue();
        harness.Phases.Read.Should().Equal([requested.Id], "the page reads the phase of its one requested export only");

        harness.Phases.Read.Clear();
        (await harness.Service.CancelAsync(subject, requested.Id.Value, requested.Version, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await harness.Service.GetAsync(subject, requested.Id.Value, CancellationToken.None)).Value!.Status.Should().Be("cancelled");
        harness.Phases.Read.Should().BeEmpty("a cancelled export never changes");
    }

    /// <summary>
    /// A host whose clock lags the one that filed the request still stores the dead export failed: the reconciler stamps
    /// its transitions no earlier than the request's own last stamp, which the aggregate and the guard both require.
    /// </summary>
    [Fact]
    public async Task The_reconciler_orders_its_transitions_after_the_request_under_a_lagging_clock()
    {
        var harness = new Harness();
        await harness.Service.CreateExportAsync(harness.Caller(Subject, DataRequestRole.Subject), Subject, CancellationToken.None);
        var dead = harness.Store.Requests.Single();
        harness.Phases.Phase[dead.Id] = DataRequestJobPhase.Dead;
        var lagging = new Harness(harness.Store, harness.Phases);
        lagging.Clock.Now = At - TimeSpan.FromMinutes(3);

        (await lagging.Service.CreateExportAsync(lagging.Caller(Subject, DataRequestRole.Subject), Subject, CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        dead.Status.Should().Be(DataRequestStatus.Failed);
        dead.UpdatedAt.Should().Be(At, "stamped at the request's own creation, not the lagging clock");
    }

    /// <summary>
    /// Standard 10: every mint and chunk read records one download measurement with its closed operation and outcome;
    /// a bound is <c>limit</c>, a key the ring no longer holds or a chunk the store cannot open <c>custody_failed</c>.
    /// </summary>
    [Fact]
    public async Task Each_mint_and_chunk_read_records_one_closed_download_outcome()
    {
        var measurements = new ConcurrentQueue<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meter) =>
            {
                if (instrument.Meter.Name == DataExportTelemetry.MeterName && instrument.Name == DataExportTelemetry.DownloadCounterName)
                {
                    meter.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var labels = new List<string>();
            foreach (var tag in tags)
            {
                labels.Add(tag.Key + "=" + tag.Value);
            }

            measurements.Enqueue(value + ":" + string.Join(",", labels.Order(StringComparer.Ordinal)));
        });
        listener.Start();
        var harness = new Harness();
        var request = harness.Completed();
        var subject = harness.Caller(Subject, DataRequestRole.Subject);

        var ticket = (await harness.Download.MintTicketAsync(subject, request.Id.Value, CancellationToken.None)).Value!.Ticket;
        await harness.Download.MintTicketAsync(subject, request.Id.Value, CancellationToken.None);
        await harness.Download.MintTicketAsync(subject, request.Id.Value, CancellationToken.None);
        await harness.Download.MintTicketAsync(subject, request.Id.Value, CancellationToken.None);
        await harness.Download.MintTicketAsync(harness.Caller(Administrator, DataRequestRole.Administrator), request.Id.Value,
            CancellationToken.None);
        await harness.Download.ReadChunkAsync(subject, request.Id.Value, ticket, 0, CancellationToken.None);
        await harness.Download.ReadChunkAsync(subject, request.Id.Value, ticket, 7, CancellationToken.None);
        harness.Artifacts.Unopenable = true;
        await harness.Download.ReadChunkAsync(subject, request.Id.Value, ticket, 1, CancellationToken.None);
        harness.Artifacts.Ring.Clear();
        await harness.Download.MintTicketAsync(subject, request.Id.Value, CancellationToken.None);
        listener.Dispose();

        measurements.Should().Equal(
            "1:operation=mint,outcome=served", "1:operation=mint,outcome=served", "1:operation=mint,outcome=served",
            "1:operation=mint,outcome=limit", "1:operation=mint,outcome=refused",
            "1:operation=chunk,outcome=served", "1:operation=chunk,outcome=refused", "1:operation=chunk,outcome=custody_failed",
            "1:operation=mint,outcome=custody_failed");
    }

    [Fact]
    public async Task A_page_cursor_is_bound_to_its_operation_actor_and_group()
    {
        var harness = new Harness();
        for (var index = 0; index < 3; index++)
        {
            var request = harness.Completed();
            request.Status.Should().Be(DataRequestStatus.Completed);
        }

        var caller = harness.Caller(Subject, DataRequestRole.Subject);
        var first = (await harness.Service.ListAsync(caller, "identity.data_request.list_own", null, 2, CancellationToken.None)).Value!;
        first.Items.Should().HaveCount(2);
        first.PageInfo.HasNext.Should().BeTrue();
        var second = (await harness.Service.ListAsync(caller, "identity.data_request.list_own", first.PageInfo.NextCursor, 2, CancellationToken.None)).Value!;
        second.Items.Should().ContainSingle();
        first.Items.Concat(second.Items).Select(item => item.RequestId).Should().OnlyHaveUniqueItems().And.BeInDescendingOrder();
        Code(await harness.Service.ListAsync(caller, "identity.data_request.list", first.PageInfo.NextCursor, 2, CancellationToken.None))
            .Should().Be("validation_failed", "another operation's cursor is refused");
        Code(await harness.Service.ListAsync(harness.Caller(Administrator, DataRequestRole.Subject), "identity.data_request.list_own",
            first.PageInfo.NextCursor, 2, CancellationToken.None)).Should().Be("validation_failed", "another actor's cursor is refused");
    }

    [Fact]
    public async Task Eligibility_judges_only_the_announced_group_refuses_a_closure_and_excludes_the_executing_request()
    {
        var store = new Store();
        var context = new Context(Subject);
        var eligibility = new DataRequestEligibility(store, new NoHoldSource(), context);
        var group = new DataRequestScopeGroup(Tenant, null);

        await FluentActions.Awaiting(() => eligibility.EvaluateAsync(new DataRequestEligibilityQuery(DataRequestKind.Closure,
            DataRequestOrigin.Self, Subject, group, DataRequestEligibilityStage.Creation), CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>(
            "the closure's structure is P03-10 Step 3's");
        await FluentActions.Awaiting(() => eligibility.EvaluateAsync(new DataRequestEligibilityQuery(DataRequestKind.Export,
            DataRequestOrigin.Self, Subject, new DataRequestScopeGroup(Tenant, OrganizationId.From(Guid.CreateVersion7())),
            DataRequestEligibilityStage.Creation), CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
        (await eligibility.EvaluateAsync(new DataRequestEligibilityQuery(DataRequestKind.Export, DataRequestOrigin.Self,
            UserId.From(Guid.CreateVersion7()), group, DataRequestEligibilityStage.Creation), CancellationToken.None)).Reason
            .Should().Be(DataRequestIneligibility.NoMembership);

        var request = DataSubjectRequest.FileForSelf(DataSubjectRequestId.From(Guid.CreateVersion7()), DataRequestKind.Export, Subject, group,
            [SubjectMembership], At).Value!;
        store.Requests.Add(request);
        (await eligibility.EvaluateAsync(new DataRequestEligibilityQuery(DataRequestKind.Export, DataRequestOrigin.Self, Subject, group,
            DataRequestEligibilityStage.Creation), CancellationToken.None)).Reason.Should().Be(DataRequestIneligibility.RequestAlreadyActive);
        (await eligibility.EvaluateAsync(new DataRequestEligibilityQuery(DataRequestKind.Export, DataRequestOrigin.Self, Subject, group,
            DataRequestEligibilityStage.Execution, request.Id.Value), CancellationToken.None)).IsEligible.Should().BeTrue();
        (await new DataRequestEligibility(store, new FixedHold(), context).EvaluateAsync(new DataRequestEligibilityQuery(DataRequestKind.Export,
            DataRequestOrigin.Self, Subject, group, DataRequestEligibilityStage.Execution, request.Id.Value), CancellationToken.None))
            .Reason.Should().Be(DataRequestIneligibility.BlockedHold);
    }

    /// <summary>
    /// ADR-0106 § 11, ADR-0107 § 2: an erasure is filed scheduled, its execution time the end of the 24-hour window, and its
    /// job is submitted on the creation's transaction due exactly then; the fences are the export's.
    /// </summary>
    [Fact]
    public async Task A_subject_files_an_erasure_scheduled_for_the_end_of_its_window_and_its_job_is_due_then()
    {
        var harness = new Harness();

        var result = await harness.Service.CreateErasureAsync(harness.Caller(Subject, DataRequestRole.Subject), Subject, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var request = harness.Store.Requests.Should().ContainSingle().Subject;
        request.Kind.Should().Be(DataRequestKind.Erasure);
        request.Status.Should().Be(DataRequestStatus.Scheduled);
        request.ExecuteAt.Should().Be(At + DataRequestLimits.CancellationWindow);
        harness.Scheduler.Fulfillments.Should().ContainSingle().Which.Should().Be(new DataRequestFulfillment(request.Id.Value,
            DataRequestKind.Erasure, DataRequestStatus.Scheduled, request.Group, At + DataRequestLimits.CancellationWindow));
        harness.Locks.Acquired.Should().ContainSingle().Which.UserIds.Should().Equal(Subject);
        harness.Locks.Acquired[0].TenantMode.Should().Be(IdentityTenantLockMode.Shared);
        harness.Audit.Designated.Should().Contain(request);

        var second = await harness.Service.CreateErasureAsync(harness.Caller(Subject, DataRequestRole.Subject), Subject, CancellationToken.None);
        second.Error!.Details!["$"].Single().Key.Should().Be("lockey_request_already_active", "one active erasure per subject and group");
        (await harness.Service.CreateExportAsync(harness.Caller(Subject, DataRequestRole.Subject), Subject, CancellationToken.None))
            .IsSuccess.Should().BeTrue("the one-active rule is per kind");
    }

    /// <summary>
    /// ADR-0106 § 3: an administrator erases only a person whose Membership of the group is Revoked; a working member is
    /// revoked first by the separately audited act. Never for themselves, never as the subject's own role.
    /// </summary>
    [Fact]
    public async Task An_administrator_erases_only_a_person_whose_Membership_of_the_group_is_revoked()
    {
        var harness = new Harness();
        var caller = harness.Caller(Administrator, DataRequestRole.Administrator);

        var working = await harness.Service.CreateErasureForMembershipAsync(caller, SubjectMembership.Value, CancellationToken.None);
        Code(working).Should().Be("business_rule_violation");
        working.Error!.Details!["$"].Single().Key.Should().Be("lockey_subject_membership_current");
        Code(await harness.Service.CreateErasureForMembershipAsync(caller, AdministratorMembership.Value, CancellationToken.None))
            .Should().Be("business_rule_violation", "an administrator never files for themselves");
        Code(await harness.Service.CreateErasureForMembershipAsync(caller, Guid.CreateVersion7(), CancellationToken.None))
            .Should().Be("not_found");
        Code(await harness.Service.CreateErasureForMembershipAsync(harness.Caller(Subject, DataRequestRole.Subject),
            SubjectMembership.Value, CancellationToken.None)).Should().Be("forbidden");
        harness.Store.Requests.Should().BeEmpty();
        harness.Scheduler.Submitted.Should().BeEmpty();

        harness.Store.Current.Remove(Subject);
        (await harness.Service.CreateErasureForMembershipAsync(caller, SubjectMembership.Value, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var request = harness.Store.Requests.Should().ContainSingle().Subject;
        request.Kind.Should().Be(DataRequestKind.Erasure);
        request.Origin.Should().Be(DataRequestOrigin.Administrator);
        request.RequesterUserId.Should().Be(Administrator);
        request.SubjectUserId.Should().Be(Subject);
    }

    /// <summary>
    /// ADR-0106 § 7: a refusal at creation is a response and writes nothing: the subject as the tenant's only eligible
    /// tenant-wide administrator, and a hold. An export is never refused for the administrator rule.
    /// </summary>
    [Theory]
    [InlineData("sole_administrator", "lockey_blocked_last_administrator")]
    [InlineData("hold", "lockey_blocked_hold")]
    public async Task An_erasure_refused_at_creation_answers_its_closed_code_and_files_nothing(string variant, string reason)
    {
        var harness = new Harness(hold: variant == "hold");
        if (variant == "sole_administrator")
        {
            harness.Store.SoleAdministrators.Add(Subject);
        }

        var refused = await harness.Service.CreateErasureAsync(harness.Caller(Subject, DataRequestRole.Subject), Subject, CancellationToken.None);

        Code(refused).Should().Be("business_rule_violation");
        refused.Error!.Details!["$"].Single().Key.Should().Be(reason);
        harness.Store.Requests.Should().BeEmpty();
        harness.Scheduler.Submitted.Should().BeEmpty();
        if (variant == "sole_administrator")
        {
            (await harness.Service.CreateExportAsync(harness.Caller(Subject, DataRequestRole.Subject), Subject, CancellationToken.None))
                .IsSuccess.Should().BeTrue("the administrator rule is the erasure's alone");
        }
    }

    /// <summary>
    /// ADR-0106 § 8: a scheduled erasure whose job died is shown failed at once, and stored failed by the next creation once
    /// its execution time has passed; before then the aggregate and the guard admit no start, so it keeps its slot.
    /// </summary>
    [Fact]
    public async Task A_dead_erasure_job_is_shown_failed_and_stored_failed_only_once_its_window_has_ended()
    {
        var harness = new Harness();
        await harness.Service.CreateErasureAsync(harness.Caller(Subject, DataRequestRole.Subject), Subject, CancellationToken.None);
        var dead = harness.Store.Requests.Single();
        harness.Phases.Phase[dead.Id] = DataRequestJobPhase.Dead;

        var view = (await harness.Service.GetAsync(harness.Caller(Subject, DataRequestRole.Subject), dead.Id.Value, CancellationToken.None)).Value!;
        view.Status.Should().Be("failed");
        view.FailureReason.Should().Be("execution_failed");
        view.ExecuteAt.Should().Be(At + DataRequestLimits.CancellationWindow);

        var early = new Harness(harness.Store, harness.Phases);
        early.Clock.Now = At.AddHours(1);
        var refused = await early.Service.CreateErasureAsync(early.Caller(Subject, DataRequestRole.Subject), Subject, CancellationToken.None);
        refused.Error!.Details!["$"].Single().Key.Should().Be("lockey_request_already_active");
        dead.Status.Should().Be(DataRequestStatus.Scheduled, "a scheduled erasure cannot start, so cannot fail, before its window ends");

        var due = new Harness(harness.Store, harness.Phases);
        due.Clock.Now = At + DataRequestLimits.CancellationWindow + TimeSpan.FromMinutes(1);
        (await due.Service.CreateErasureAsync(due.Caller(Subject, DataRequestRole.Subject), Subject, CancellationToken.None))
            .IsSuccess.Should().BeTrue();
        dead.Status.Should().Be(DataRequestStatus.Failed);
        dead.FailureReason.Should().Be(DataRequestFailureReason.ExecutionFailed);
        due.Store.Requests.Should().HaveCount(2);
    }

    /// <summary>ADR-0106 § 7: an erasure of an organization group is never refused for the tenant-wide administrator rule.</summary>
    [Fact]
    public async Task Eligibility_asks_the_administrator_rule_only_of_a_tenant_wide_erasure()
    {
        var branch = OrganizationId.From(Guid.CreateVersion7());
        var store = new Store();
        store.SoleAdministrators.Add(Subject);
        var tenantWide = new DataRequestEligibility(store, new NoHoldSource(), new Context(Subject));
        var organization = new DataRequestEligibility(store, new NoHoldSource(), new Context(Subject, branch));

        (await tenantWide.EvaluateAsync(new DataRequestEligibilityQuery(DataRequestKind.Erasure, DataRequestOrigin.Self, Subject,
            new DataRequestScopeGroup(Tenant, null), DataRequestEligibilityStage.Creation), CancellationToken.None))
            .Reason.Should().Be(DataRequestIneligibility.BlockedLastAdministrator);
        (await organization.EvaluateAsync(new DataRequestEligibilityQuery(DataRequestKind.Erasure, DataRequestOrigin.Self, Subject,
            new DataRequestScopeGroup(Tenant, branch), DataRequestEligibilityStage.Creation), CancellationToken.None))
            .IsEligible.Should().BeTrue("an organization's group holds no tenant-wide administrator Membership");
        (await tenantWide.EvaluateAsync(new DataRequestEligibilityQuery(DataRequestKind.Export, DataRequestOrigin.Self, Subject,
            new DataRequestScopeGroup(Tenant, null), DataRequestEligibilityStage.Creation), CancellationToken.None))
            .IsEligible.Should().BeTrue("an export removes nobody");
    }

    private static string Binding(char fill) => new(fill, 64);

    private static string? Code<T>(Result<T> result) => result.IsFailure ? result.Error!.Code : null;

    /// <summary>The cores over in-memory ports, in the tenant-wide group, as one request's scope.</summary>
    private sealed class Harness
    {
        public Harness(Store? store = null, Phases? phases = null, bool hold = false)
        {
            Store = store ?? new Store();
            Phases = phases ?? new Phases();
            var context = new Context(null);
            var reconciler = new DataRequestFailureReconciler(Store, Phases, Locks, context, Clock);
            Service = new DataRequestService(Store, Store, Phases, reconciler,
                new DataRequestEligibility(Store, hold ? new FixedHold() : new NoHoldSource(), context), Scheduler, Artifacts,
                Locks, context, Audit, new Ids(), Clock);
            Download = new DataExportDownloadService(Store, Store, Artifacts, Sessions, context, Audit, new Ids(), Clock);
        }

        public Store Store { get; }
        public Phases Phases { get; }
        public Locks Locks { get; } = new();
        public RecordingScheduler Scheduler { get; } = new();
        public ArtifactCustody Artifacts { get; } = new();
        public SessionBinding Sessions { get; } = new() { Binding = Binding('a') };
        public RecordingAudit Audit { get; } = new();
        public FixedClock Clock { get; } = new();
        public DataRequestService Service { get; }
        public DataExportDownloadService Download { get; }
        public int Rechecks { get; private set; }
        public Result<None> RecheckResult { get; init; } = Result<None>.Ok(None.Value);

        public DataRequestCaller Caller(UserId actor, DataRequestRole role) => new(actor, role, _ =>
        {
            Rechecks++;
            return Task.FromResult(RecheckResult);
        });

        /// <summary>A completed export about the subject with a readable two-chunk artifact.</summary>
        public DataSubjectRequest Completed(DataRequestOrigin origin = DataRequestOrigin.Self)
        {
            var group = new DataRequestScopeGroup(Tenant, null);
            var request = (origin == DataRequestOrigin.Self
                ? DataSubjectRequest.FileForSelf(DataSubjectRequestId.From(Guid.CreateVersion7()), DataRequestKind.Export, Subject, group,
                    [SubjectMembership], At)
                : DataSubjectRequest.FileForSubject(DataSubjectRequestId.From(Guid.CreateVersion7()), DataRequestKind.Export, Administrator,
                    Subject, group, [SubjectMembership], At)).Value!;
            request.StartFulfilling(At, UserId.SystemActor).IsSuccess.Should().BeTrue();
            var artifact = DataExportArtifact.Create(DataExportArtifactId.From(Guid.CreateVersion7()), request, 200_000, "v1", At,
                UserId.SystemActor).Value!;
            request.Complete([], At, UserId.SystemActor).IsSuccess.Should().BeTrue();
            Store.Requests.Add(request);
            Store.Artifacts[request.Id] = new DataRequestArtifactRow(artifact, true);
            return request;
        }
    }

    private sealed class Context(UserId? actor, OrganizationId? organization = null) : ITenantContext
    {
        public bool IsResolved => true;
        public TenantContextOrigin? Origin => TenantContextOrigin.HostAndClaim;
        public TenantId TenantId => Tenant;
        public OrganizationId? OrganizationId => organization;
        public UserId? UserId => actor;
        public string? CorrelationId => null;
        public string? ModuleName => null;
    }

    private sealed class Store : IDataRequestStore, IDataRequestReader, IDataExportTicketStore
    {
        public List<DataSubjectRequest> Requests { get; } = [];
        public Dictionary<DataSubjectRequestId, DataRequestArtifactRow> Artifacts { get; } = [];
        public List<DataExportTicket> Tickets { get; } = [];
        public bool RejectAdd { get; set; }
        public bool FailConsumption { get; set; }
        public int TicketSaves { get; private set; }

        public BackgroundJobId RequireCurrentJob() => throw new InvalidOperationException("No job in a request.");

        public Task<DataSubjectRequest?> FindLockedAsync(DataSubjectRequestId requestId, CancellationToken cancellationToken) =>
            Task.FromResult(Requests.SingleOrDefault(request => request.Id == requestId));

        public Task<IReadOnlyList<DataExportArtifactId>> FindAvailableArtifactsAsync(UserId subject, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveAsync(DataSubjectRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<MembershipId>> ReadSubjectMembershipsAsync(UserId subject, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MembershipId>>(subject == Subject ? [SubjectMembership]
                : subject == Administrator ? [AdministratorMembership] : []);

        public Task<UserId?> FindMembershipSubjectAsync(MembershipId membership, CancellationToken cancellationToken) =>
            Task.FromResult(membership == SubjectMembership ? Subject : membership == AdministratorMembership ? Administrator : (UserId?)null);

        public Task<DataSubjectRequest?> FindActiveLockedAsync(UserId subject, DataRequestKind kind, CancellationToken cancellationToken) =>
            Task.FromResult(Requests.FirstOrDefault(request => request.SubjectUserId == subject && request.Kind == kind && !request.IsTerminal));

        public Task AddAsync(DataSubjectRequest aggregate, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The cores file through the mapped one-active refusal.");

        public Task<bool> TryAddAsync(DataSubjectRequest request, CancellationToken cancellationToken)
        {
            if (RejectAdd)
            {
                return Task.FromResult(false);
            }

            Requests.Add(request);
            return Task.FromResult(true);
        }

        public Task<DataRequestArtifactRow?> FindArtifactLockedAsync(DataSubjectRequestId requestId, CancellationToken cancellationToken) =>
            Task.FromResult(Artifacts.GetValueOrDefault(requestId));

        public Task AddAsync(DataExportTicket ticket, CancellationToken cancellationToken = default)
        {
            Tickets.Add(ticket);
            return Task.CompletedTask;
        }

        public Task<DataExportTicket?> FindLockedAsync(DataExportArtifactId artifactId, string digest, CancellationToken cancellationToken) =>
            Task.FromResult(Tickets.SingleOrDefault(ticket => ticket.ArtifactId == artifactId && ticket.Digest == digest));

        public Task UpdateAsync(DataExportTicket ticket, CancellationToken cancellationToken = default)
        {
            if (FailConsumption)
            {
                throw new InvalidOperationException("Synthetic consumption failure.");
            }

            TicketSaves++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DataRequestReceiptRow>> ListAsync(UserId? subject, Guid? before, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DataRequestReceiptRow>>(Requests
                .Where(request => (subject is null ? request.Kind != DataRequestKind.Closure : request.SubjectUserId == subject)
                    && (before is null || request.Id.Value.CompareTo(before.Value) < 0))
                .OrderByDescending(request => request.Id.Value).Take(take)
                .Select(request => DataRequestReceiptRow.From(request) is var row && subject is null ? row : row with { MembershipSnapshotJson = null })
                .ToList());

        public Task<DataExportTicketCounts> CountAsync(DataExportArtifactId artifactId, string sessionBindingDigest,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DataExportTicketCounts(
                Tickets.Count(ticket => ticket.ArtifactId == artifactId && ticket.SessionBindingDigest == sessionBindingDigest
                    && ticket.ConsumedAt is null && ticket.ExpiresAt > Now()),
                Tickets.Count(ticket => ticket.ArtifactId == artifactId)));

        public Task<bool> HoldsReadableArtifactAsync(UserId subject, CancellationToken cancellationToken) =>
            Task.FromResult(Artifacts.Values.Any(row => row.Readable && row.Artifact.SubjectUserId == subject));

        /// <summary>The people who hold an Active or Suspended Membership of the group; both fixtures do by default.</summary>
        public HashSet<UserId> Current { get; } = [Subject, Administrator];

        /// <summary>The people the group's tenant has as its only eligible tenant-wide administrator.</summary>
        public HashSet<UserId> SoleAdministrators { get; } = [];

        public Task<bool> HoldsCurrentMembershipAsync(UserId subject, CancellationToken cancellationToken) =>
            Task.FromResult(Current.Contains(subject));

        public Task<bool> IsSoleEligibleTenantAdministratorAsync(UserId subject, CancellationToken cancellationToken) =>
            Task.FromResult(SoleAdministrators.Contains(subject));

        public Task<bool> HoldsErasureFencesAsync(UserId subject, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Only the erasure job's participant observes the fences.");

        public Func<DateTimeOffset> Now { get; set; } = () => At;

        public Task<DataSubjectRequest?> FindAsync(DataSubjectRequestId requestId, CancellationToken cancellationToken) =>
            Task.FromResult(Requests.SingleOrDefault(request => request.Id == requestId));

        public Task<IReadOnlyDictionary<DataSubjectRequestId, DataRequestArtifactRow>> ReadArtifactsAsync(
            IReadOnlyCollection<DataSubjectRequestId> requestIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<DataSubjectRequestId, DataRequestArtifactRow>>(
                Artifacts.Where(entry => requestIds.Contains(entry.Key)).ToDictionary(entry => entry.Key, entry => entry.Value));
    }

    private sealed class Phases : IDataRequestJobPhaseReader
    {
        public Dictionary<DataSubjectRequestId, DataRequestJobPhase> Phase { get; } = [];

        /// <summary>Every request whose phase a page or receipt read, in order.</summary>
        public List<DataSubjectRequestId> Read { get; } = [];

        public Task<DataRequestJobPhase> ReadAsync(DataSubjectRequestId requestId, DataRequestKind kind, CancellationToken cancellationToken) =>
            Task.FromResult(kind is DataRequestKind.Export or DataRequestKind.Erasure
                ? Phase.GetValueOrDefault(requestId, DataRequestJobPhase.Pending) : DataRequestJobPhase.Unavailable);

        public Task<IReadOnlyDictionary<DataSubjectRequestId, DataRequestJobPhase>> ReadManyAsync(
            IReadOnlyCollection<DataSubjectRequestId> requestIds, DataRequestKind kind, CancellationToken cancellationToken)
        {
            Read.AddRange(requestIds);
            return Task.FromResult<IReadOnlyDictionary<DataSubjectRequestId, DataRequestJobPhase>>(
                requestIds.Distinct().ToDictionary(id => id, id => Phase.GetValueOrDefault(id, DataRequestJobPhase.Pending)));
        }
    }

    /// <summary>
    /// Each core call stands for its own request transaction, so the latest plan is the current one; a fresh harness
    /// proves a single call acquires exactly once.
    /// </summary>
    private sealed class Locks : IIdentityMutationLocks
    {
        private IdentityMutationLockPlan? _current;

        public List<IdentityMutationLockPlan> Acquired { get; } = [];

        public Task AcquireAsync(IdentityMutationLockPlan plan, CancellationToken cancellationToken = default)
        {
            _current = plan;
            Acquired.Add(plan);
            return Task.CompletedTask;
        }

        public void RequireCurrent(IdentityMutationLockPlan plan)
        {
            if (!ReferenceEquals(plan, _current))
            {
                throw new IdentityMutationLockException();
            }
        }
    }

    private sealed class RecordingScheduler : IDataRequestFulfillmentScheduler
    {
        public List<DataSubjectRequestId> Submitted { get; } = [];

        public List<DataRequestFulfillment> Fulfillments { get; } = [];

        public ValueTask SubmitAsync(DataRequestFulfillment fulfillment, CancellationToken cancellationToken)
        {
            Submitted.Add(DataSubjectRequestId.From(fulfillment.RequestId));
            Fulfillments.Add(fulfillment);
            return ValueTask.CompletedTask;
        }

        public ValueTask SchedulePurgeAsync(Guid artifactId, DateTimeOffset dueAt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ArtifactCustody : IDataExportArtifactStore
    {
        public List<byte[]> Served { get; } = [];

        public HashSet<string> Ring { get; } = new(StringComparer.Ordinal) { "v1" };

        /// <summary>The store answers every chunk as absent, as it does for a chunk it cannot open.</summary>
        public bool Unopenable { get; set; }

        public bool CanOpen(string keyVersion) => Ring.Contains(keyVersion);

        public Task<DataExportArtifactMetadata> PutAsync(DataExportArtifactPut put, IAsyncEnumerable<ReadOnlyMemory<byte>> plaintextChunks,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DataExportArtifactMetadata?> ReadMetadataAsync(DataExportArtifactLocator artifact, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DataExportPlaintextChunk?> ReadChunkAsync(DataExportArtifactLocator artifact, int index, CancellationToken cancellationToken)
        {
            if (Unopenable)
            {
                return Task.FromResult<DataExportPlaintextChunk?>(null);
            }

            var bytes = Enumerable.Repeat((byte)(index + 1), 16).ToArray();
            Served.Add(bytes);
            return Task.FromResult<DataExportPlaintextChunk?>(new DataExportPlaintextChunk(index, 2, bytes));
        }

        /// <summary>Every artifact the service asked the store to purge.</summary>
        public List<Guid> Purged { get; } = [];

        public Task<DataExportArtifactStatus?> PurgeAsync(DataExportArtifactLocator artifact, DataExportArtifactStatus outcome,
            CancellationToken cancellationToken)
        {
            Purged.Add(artifact.ArtifactId);
            return Task.FromResult<DataExportArtifactStatus?>(outcome);
        }
    }

    private sealed class SessionBinding : IDataExportSessionBinding
    {
        public string? Binding { get; set; }

        public string? Capture(UserId actor, TenantId tenant, OrganizationId? organization) => Binding;
    }

    private sealed class RecordingAudit : IAuditSubject
    {
        public List<object> Designated { get; } = [];

        public void Designate<TId>(IAggregateRoot<TId> aggregate) where TId : struct, IStronglyTypedId<Guid> => Designated.Add(aggregate);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now { get; set; } = At;
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class Ids : IGuidFactory
    {
        public Guid NewUuidV7() => Guid.CreateVersion7();
        public Guid NewUuidV4() => Guid.NewGuid();
    }

    private sealed class FixedHold : IDataRequestHoldSource
    {
        public Task<bool> HasHoldAsync(DataRequestHoldQuery query, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
