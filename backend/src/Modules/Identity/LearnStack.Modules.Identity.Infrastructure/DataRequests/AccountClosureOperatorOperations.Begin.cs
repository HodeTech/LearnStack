using System.Globalization;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Identity.Infrastructure.DataRequests;

/// <summary>The closure's Begin (ADR-0107 § 7, <c>begin_closure</c>).</summary>
/// <remarks>
/// <para>
/// <b>One transaction, then the fan-out.</b> The request is read to find the person, with the raw provider subject the
/// identity provider's port is asked about; the person's User fence is taken, the participant groups and their Membership
/// counts are read under it and every participant tenant's fence is taken, ascending; the request is locked and read again
/// with the User's status; the closure is judged; and either the refusal is recorded or the User moves Active to Closing
/// and the request <c>scheduled</c> to <c>fulfilling</c>. The result row names the platform sentinel tenant and commits
/// with it. The fences end with that transaction, which is the nonadmitting transition they order, and only then is the
/// closure fanned out to the served groups, each in its own transaction.
/// </para>
/// <para>
/// <b>What could never finish is refused first.</b> A closure whose account the port could never delete
/// (<see cref="ClosureBeginOutcome.RefusedProvider"/>) and a person past the closure's bounds
/// (<see cref="ClosureBeginOutcome.RefusedTooLarge"/>) are refused before any write and before the person leaves Active:
/// no refusal code is recorded on the request, because neither is the person's to act on, and the scope ends by disposal
/// with its entry row the record. A person past the bounds takes no tenant fence.
/// </para>
/// <para>
/// <b>Judging.</b> <c>blocked_window</c> by the database clock, which the request guard asks again; <c>blocked_hold</c>
/// from the hold source for the closure; <c>blocked_last_administrator</c> when the person is the only eligible tenant-wide
/// administrator of a participant tenant that allows ordinary work. The last rule protects the transition from Active: a
/// person already Closing for another closure is no eligible administrator anywhere, so that closure's Begin judged it.
/// </para>
/// <para>
/// <b>Resuming.</b> A closure already <c>fulfilling</c> is not judged and not fenced: its User is Closing, which no
/// Membership is admitted for, so its participants cannot change. Its scope reads them and their lifecycle, changes nothing
/// and ends by disposal, its entry row the record, and the fan-out runs again and adopts what an earlier call started.
/// </para>
/// </remarks>
public sealed partial class AccountClosureOperatorOperations
{
    /// <summary>
    /// For each tenant of <c>@tenants</c>, whether the person and whether anybody else is an eligible tenant-wide
    /// administrator: the administrator predicate of the deferred complete-graph guard (<c>fn_identity_complete_graph_guard</c>
    /// with <c>fn_identity_admin_admission_state</c>), which the application role's <c>DataRequestStore</c> reads less the
    /// User status. On the platform connection the User status is readable, so an administrator whose User is Closing or
    /// Closed is not eligible here either; the Membership's deleter column is not read, because
    /// <c>ck_memberships_lifecycle</c> makes it NULL beside an Active status, and the User's deletion columns are not read,
    /// because <c>ck_users_no_soft_delete</c> makes them NULL always. A tenant with no eligible administrator has no row.
    /// It is exposed so that a test can run it over the same fixtures as the guard.
    /// </summary>
    public static string AdministratorStandingSql { get; } = $"""
        SELECT member.tenant_id, bool_or(member.user_id = @subject), bool_or(member.user_id <> @subject)
        FROM public.memberships AS member
        JOIN public.membership_role_assignments AS assignment
          ON assignment.tenant_id = member.tenant_id AND assignment.membership_id = member.id
        JOIN public.roles AS role ON role.tenant_id = assignment.tenant_id AND role.id = assignment.role_id
        JOIN public.users AS person ON person.id = member.user_id
        WHERE member.tenant_id = ANY(@tenants) AND member.organization_id IS NULL
          AND member.status = 'Active' AND member.deleted_at IS NULL
          AND role.built_in_key = 'TenantAdmin' AND role.permission_scope = 'Tenant'
          AND role.deleted_at IS NULL AND role.deleted_by IS NULL
          AND NOT EXISTS (SELECT 1 FROM (VALUES {IdentityFoundationManifest.Version1.KernelSqlValues}) AS kernel(scope,permission_key)
            WHERE NOT EXISTS (SELECT 1 FROM public.role_permission_grants AS grant_row
                WHERE grant_row.tenant_id = role.tenant_id AND grant_row.role_id = role.id
                  AND grant_row.permission_scope = kernel.scope AND grant_row.permission_key = kernel.permission_key))
          AND person.status = 'Active'
        GROUP BY member.tenant_id
        """;

    /// <summary>
    /// The participant groups and the Memberships each holds, read under the person's User fence in one grouped statement
    /// through <c>ix_memberships_user_id</c>: what Begin judges the closure's bounds by. Exposed so that a test can ask for
    /// its plan.
    /// </summary>
    public static string ParticipantsSql => PlatformIdentityFences.ParticipantsSql;

    // The refusal record (ADR-0106 § 7): the request stays scheduled with its closed code. The guard's platform arm stamps
    // the update time and the system actor, and refuses a code equal to the one standing.
    private const string RecordRefusal = """
        UPDATE data_subject_requests SET last_refusal = @refusal, row_version = row_version + 1
        WHERE id = @id AND kind = 'closure' AND status = 'scheduled' AND row_version = @version
        """;

    // The nonadmitting transition: the users trigger exception admits it only for this session, under the fences.
    private const string CloseUser = "UPDATE users SET status = 'Closing' WHERE id = @subject AND status = 'Active'";

    // The closure's Begin arrow: the guard asks again for the window, the Closing User and every fence.
    private const string Fulfil = """
        UPDATE data_subject_requests SET status = 'fulfilling', row_version = row_version + 1
        WHERE id = @id AND kind = 'closure' AND status = 'scheduled' AND row_version = @version
        """;

    /// <inheritdoc />
    public async Task<ClosureBeginResult> BeginAsync(
        PlatformAdmission admission, Guid closureRequestId, CancellationToken cancellationToken = default)
    {
        RequireOperation(admission, PlatformOperations.ClosureBegin);
        if (closureRequestId == Guid.Empty)
        {
            throw new ArgumentException("A closure Begin names a request.", nameof(closureRequestId));
        }

        // Before anything is entered or written: a deployment that cannot fan out never moves a person to Closing.
        var fanOut = RequireFanOut();
        var commitSent = false;
        BeginDecision decision;
        try
        {
            decision = await BeginCoreAsync(admission, closureRequestId, () => commitSent = true, cancellationToken);
        }
        catch (Exception failure) when (commitSent && !IsFatal(failure))
        {
            // The commit was sent: it may have landed, so this is never "nothing changed, repeat it". A repeat after a
            // landed commit finds the closure fulfilling and resumes, which is how the operator finds out.
            throw new OperatorRecoveryUnavailableException(commitOutcomeUnknown: true, cause: failure);
        }
        catch (Exception failure) when (!commitSent && IsRetryable(failure, cancellationToken))
        {
            // Nothing committed, so a repeat is safe: a fence another act held past the bound, or a transient fault.
            throw new OperatorRecoveryUnavailableException(cause: failure);
        }

        if (decision.Result is { } ended)
        {
            return ended;
        }

        var groups = await FanOutAsync(fanOut, admission, decision, cancellationToken);
        return new ClosureBeginResult(decision.Outcome, StatusCode(DataRequestStatus.Fulfilling), Groups: groups);
    }

    private async Task<BeginDecision> BeginCoreAsync(
        PlatformAdmission admission, Guid requestId, Action sendingCommit, CancellationToken cancellationToken)
    {
        await using var handle = await scope.EnterAsync(admission, cancellationToken);
        var id = new PlatformSqlParameter("id", requestId);

        // 1. The request as it stands, unlocked: what it is and whose it is.
        var found = await handle.QueryAsync(ReadRequest, [id], ClosureRequest.Read, cancellationToken);
        if (found.Count == 0)
        {
            return BeginDecision.Ended(new ClosureBeginResult(ClosureBeginOutcome.NotFound));
        }

        var request = found[0];
        if (request.Kind != DataRequestKind.Closure)
        {
            return BeginDecision.Ended(new ClosureBeginResult(ClosureBeginOutcome.NotClosure, StatusCode(request.Status)));
        }

        if (request.Status is not (DataRequestStatus.Scheduled or DataRequestStatus.Fulfilling))
        {
            return BeginDecision.Ended(new ClosureBeginResult(ClosureBeginOutcome.NotBeginnable, StatusCode(request.Status)));
        }

        if (request.Account == UserAccount.Closed)
        {
            return BeginDecision.Ended(
                new ClosureBeginResult(ClosureBeginOutcome.SubjectNotAdmitted, StatusCode(request.Status)));
        }

        if (request.Status == DataRequestStatus.Fulfilling)
        {
            // 2. A closure that began: no fence, no judgment, no write. Its scope ends by disposal.
            return await ResumeAsync(handle, request.Subject, request.Account, cancellationToken);
        }

        if (!ProviderAccepts(request.ProviderSubject))
        {
            // Finalize could never delete this account (the port is not configured, or refuses the subject before any
            // call), so the person is never moved to a Closing they could not leave. Nothing is written.
            return BeginDecision.Ended(new ClosureBeginResult(ClosureBeginOutcome.RefusedProvider, StatusCode(request.Status)));
        }

        // 3. The fences, the participants and their size read under the User fence among them.
        var fences = await PlatformIdentityFences.AcquireAsync(handle, request.Subject, request.Tenant, cancellationToken);
        if (!fences.WithinBounds)
        {
            // Every later step is bounded by the closure's size, so a person past it is refused while still Active; only the
            // User key was taken, and nothing is written.
            return BeginDecision.Ended(new ClosureBeginResult(ClosureBeginOutcome.RefusedTooLarge, StatusCode(request.Status)));
        }

        // 4. The request again, locked, and the User status, both under the fences: what was read above may have moved.
        var locked = await handle.QueryAsync(LockRequest, [id], LockedClosure.Read, cancellationToken);
        if (locked.Count != 1 || locked[0].Kind != DataRequestKind.Closure || locked[0].Subject != request.Subject)
        {
            // A request's kind and person are immutable and it is never deleted.
            throw new InvalidOperationException("The closure request changed its kind or person; the act is abandoned.");
        }

        var closure = locked[0];
        var account = ParseAccount((await handle.QueryAsync(
            ReadUserStatus, [new PlatformSqlParameter("subject", request.Subject.Value)], reader => reader.GetString(0),
            cancellationToken)).SingleOrDefault());
        if (closure.Status is not (DataRequestStatus.Scheduled or DataRequestStatus.Fulfilling))
        {
            // Cancelled by the person while this act waited for the fences.
            return BeginDecision.Ended(new ClosureBeginResult(ClosureBeginOutcome.NotBeginnable, StatusCode(closure.Status)));
        }

        if (account == UserAccount.Closed)
        {
            return BeginDecision.Ended(
                new ClosureBeginResult(ClosureBeginOutcome.SubjectNotAdmitted, StatusCode(closure.Status)));
        }

        var participants = await ClassifyAsync(handle, fences.Groups, cancellationToken);
        if (closure.Status == DataRequestStatus.Fulfilling)
        {
            // Another operator's Begin committed while this one waited: nothing is left to judge or write.
            return BeginDecision.FanOut(ClosureBeginOutcome.Resumed, request.Subject, participants);
        }

        // 5. The judgment, in the ADR's order; the first refusal stands.
        var refusal = await JudgeAsync(handle, request, closure, account, participants, cancellationToken);
        if (refusal is { } refused)
        {
            return await RefuseAsync(handle, requestId, closure, refused, sendingCommit, cancellationToken);
        }

        // 6. The transition: the User first, because the request's Begin arrow requires it Closing.
        var before = State(("status", StatusCode(DataRequestStatus.Scheduled)), ("account", Account(account)));
        if (account == UserAccount.Active)
        {
            var closed = await handle.ExecuteAsync(
                CloseUser, [new PlatformSqlParameter("subject", request.Subject.Value)], cancellationToken);
            if (closed != 1)
            {
                throw new InvalidOperationException("The fenced Active User did not move to Closing in exactly one row; the act is abandoned.");
            }
        }

        var fulfilled = await handle.ExecuteAsync(
            Fulfil, [id, new PlatformSqlParameter("version", closure.Version)], cancellationToken);
        if (fulfilled != 1)
        {
            throw new InvalidOperationException("The locked closure did not begin in exactly one row; the act is abandoned.");
        }

        // 7. The MUST row, under the platform sentinel: the closure spans tenants, so no tenant's trail holds it, and it
        //    carries the operator's ticket. Closed counts only, never a person or a tenant.
        await handle.WriteResultAsync(
            new PlatformResult(
                TenantId.PlatformSentinel, EntityType, requestId.ToString("D", CultureInfo.InvariantCulture), before,
                State(
                    ("status", StatusCode(DataRequestStatus.Fulfilling)), ("account", Account(UserAccount.Closing)),
                    ("served_groups", participants.Served.Count), ("unserved_groups", participants.Unserved))),
            cancellationToken);

        // Past the point where the caller's abort matters: a cancelled commit leaves its outcome unknown.
        sendingCommit();
        await handle.CommitAsync(CancellationToken.None);
        return BeginDecision.FanOut(
            account == UserAccount.Active ? ClosureBeginOutcome.Begun : ClosureBeginOutcome.Resumed, request.Subject, participants);
    }

    private async Task<BeginDecision> ResumeAsync(
        IPlatformAdminScopeHandle handle, UserId subject, UserAccount account, CancellationToken cancellationToken)
    {
        if (account != UserAccount.Closing)
        {
            // The request guard admits a closure's Begin only for a Closing User, and nothing returns a User to Active.
            throw new InvalidOperationException("A fulfilling closure's person is not Closing; the act is abandoned.");
        }

        var read = await PlatformIdentityFences.ReadParticipantsAsync(handle, subject, cancellationToken);
        if (!read.WithinBounds)
        {
            // Begin refused such a person while Active, and a Closing User is admitted to no Membership.
            throw new InvalidOperationException("A begun closure's person is past the closure's bounds; the act is abandoned.");
        }

        var participants = await ClassifyAsync(handle, read.Groups, cancellationToken);
        return BeginDecision.FanOut(ClosureBeginOutcome.Resumed, subject, participants);
    }

    /// <summary>The first refusal that stands, or none.</summary>
    private async Task<DataRequestRefusal?> JudgeAsync(
        IPlatformAdminScopeHandle handle, ClosureRequest request, LockedClosure closure, UserAccount account,
        ClosureParticipants participants, CancellationToken cancellationToken)
    {
        if (!closure.Due)
        {
            return DataRequestRefusal.BlockedWindow;
        }

        // The closure's own hold query: the person and the request's originating group, read from the row. Eligibility's
        // execution stage is not asked, because a closure's execution re-check spans tenants and is this act's.
        if (await holds.HasHoldAsync(
                new DataRequestHoldQuery(DataRequestKind.Closure, request.Subject, request.Group), cancellationToken))
        {
            return DataRequestRefusal.BlockedHold;
        }

        if (account == UserAccount.Active && participants.OperableTenants.Count > 0)
        {
            var standings = await handle.QueryAsync(
                AdministratorStandingSql,
                [
                    new PlatformSqlParameter("subject", request.Subject.Value),
                    new PlatformSqlParameter("tenants", participants.OperableTenants.Select(tenant => tenant.Value).ToArray()),
                ],
                reader => (Subject: reader.GetBoolean(1), Other: reader.GetBoolean(2)),
                cancellationToken);
            if (standings.Any(standing => standing.Subject && !standing.Other))
            {
                return DataRequestRefusal.BlockedLastAdministrator;
            }
        }

        return null;
    }

    /// <summary>
    /// Records a refusal on the request with its MUST row, or, when the same code already stands, changes nothing and ends
    /// by disposal: the guard refuses recording the standing code again, and the request already shows it.
    /// </summary>
    private static async Task<BeginDecision> RefuseAsync(
        IPlatformAdminScopeHandle handle, Guid requestId, LockedClosure closure, DataRequestRefusal refusal,
        Action sendingCommit, CancellationToken cancellationToken)
    {
        var code = DataRequestCodes.Refusals.ToCode(refusal);
        var ended = new ClosureBeginResult(
            refusal switch
            {
                DataRequestRefusal.BlockedWindow => ClosureBeginOutcome.RefusedWindow,
                DataRequestRefusal.BlockedHold => ClosureBeginOutcome.RefusedHold,
                _ => ClosureBeginOutcome.RefusedLastAdministrator,
            },
            StatusCode(DataRequestStatus.Scheduled),
            code);
        if (closure.LastRefusal == refusal)
        {
            return BeginDecision.Ended(ended);
        }

        var recorded = await handle.ExecuteAsync(
            RecordRefusal,
            [
                new PlatformSqlParameter("id", requestId), new PlatformSqlParameter("version", closure.Version),
                new PlatformSqlParameter("refusal", code),
            ],
            cancellationToken);
        if (recorded != 1)
        {
            throw new InvalidOperationException("The locked closure did not record its refusal in exactly one row; the act is abandoned.");
        }

        await handle.WriteResultAsync(
            new PlatformResult(
                TenantId.PlatformSentinel, EntityType, requestId.ToString("D", CultureInfo.InvariantCulture),
                State(
                    ("status", StatusCode(DataRequestStatus.Scheduled)),
                    ("last_refusal", closure.LastRefusal is { } standing ? DataRequestCodes.Refusals.ToCode(standing) : null)),
                State(("status", StatusCode(DataRequestStatus.Scheduled)), ("last_refusal", code))),
            cancellationToken);
        sendingCommit();
        await handle.CommitAsync(CancellationToken.None);
        return BeginDecision.Ended(ended);
    }

    /// <summary>
    /// Hands the served groups to the fan-out and counts its reports. The Begin has committed: a group the fan-out did not
    /// report as started, adopted or waiting is failed, and a fan-out that throws fails every group, so the answer always
    /// says a repeat is owed rather than lose the committed Begin.
    /// </summary>
    private async Task<ClosureBeginTally> FanOutAsync(
        IClosureFanOut fanOut, PlatformAdmission admission, BeginDecision decision, CancellationToken cancellationToken)
    {
        var participants = decision.Participants!;
        var served = participants.Served;
        var outcomes = new Dictionary<ClosureParticipantGroup, ClosureGroupOutcome>();
        if (served.Count > 0)
        {
            try
            {
                var reports = await fanOut.FanOutAsync(
                    admission.Operator, admission.Ticket, new ClosureFanOutRequest(decision.Subject!.Value, served), cancellationToken);
                foreach (var report in reports ?? [])
                {
                    if (report is not null && served.Contains(report.Group) && Enum.IsDefined(report.Outcome))
                    {
                        outcomes.TryAdd(report.Group, report.Outcome);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception failure) when (!IsFatal(failure))
            {
                LogFanOutFailed(logger, failure);
                outcomes.Clear();
            }
        }

        int Count(ClosureGroupOutcome outcome) => outcomes.Values.Count(value => value == outcome);
        var started = Count(ClosureGroupOutcome.Started);
        var adopted = Count(ClosureGroupOutcome.Adopted);
        var waiting = Count(ClosureGroupOutcome.Waiting);
        return new ClosureBeginTally(
            served.Count, started, adopted, waiting, served.Count - started - adopted - waiting, participants.Unserved);
    }

    /// <summary>Where the platform transaction left the act: ended with a result, or due a fan-out.</summary>
    private sealed record BeginDecision(
        ClosureBeginResult? Result, ClosureBeginOutcome Outcome, UserId? Subject, ClosureParticipants? Participants)
    {
        internal static BeginDecision Ended(ClosureBeginResult result) => new(result, result.Outcome, null, null);

        internal static BeginDecision FanOut(ClosureBeginOutcome outcome, UserId subject, ClosureParticipants participants) =>
            new(null, outcome, subject, participants);

        /// <summary>Never prints the person.</summary>
        public override string ToString() => nameof(BeginDecision);
    }
}
