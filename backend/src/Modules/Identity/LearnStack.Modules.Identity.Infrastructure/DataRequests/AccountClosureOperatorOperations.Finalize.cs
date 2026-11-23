using System.Collections.Frozen;
using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging;

namespace LearnStack.Modules.Identity.Infrastructure.DataRequests;

/// <summary>The closure's Finalize (ADR-0107 § 7, ADR-0108 § 8, <c>finalize_closure</c>).</summary>
/// <remarks>
/// <para>
/// <b>Two transactions around the provider.</b> The first takes the person's User fence and every participant tenant's
/// fence, locks the closure and reads the person's account, finds every served group ready, redacts the person's audit rows
/// in every participant tenant and then the platform sentinel tenant, and commits with the intent row that carries the
/// summed counts, which is the redaction's MUST evidence. The fences end with that commit. The identity provider's account
/// is deleted next, before any other statement, so the transaction the checkpoint opened holds nothing while the call runs.
/// The second transaction takes the fences again, re-reads and re-judges everything the first judged, moves the User from
/// Closing to Closed with the raw provider subject removed, completes every participant erasure awaiting the privileged
/// step, this closure and every other open closure of the person, and commits with the result row.
/// </para>
/// <para>
/// <b>Ready.</b> A served group is ready when its newest erasure of the person (an open one first, of any origin) awaits
/// the privileged step, or completed at or after this closure's Begin, and its frozen Memberships include every Membership
/// the person holds in the group. The Begin moment is the closure's own update stamp: the guard's Begin arrow wrote it from
/// the database clock and no arrow writes the row again until this act. An erasure completed before the closure began, by
/// a person admitted again since, never stands in for a fan-out that did not run.
/// </para>
/// <para>
/// <b>Every attempt is recorded.</b> A refusal before the checkpoint changes nothing and ends by disposal, its entry row the
/// record. Every attempt that reaches the checkpoint writes its own intent row, a repeat's included, whose redaction then
/// finds nothing left to change; and every such attempt that ends without a fault writes its own result row saying how it
/// ended: finalized, or stopped at the provider, or a group found pending again, or another call's finalization found.
/// </para>
/// <para>
/// <b>The provider subject.</b> It is read once, under the fences, passed to the port, and never logged, audited, metered
/// or returned; the second transaction removes it from the row.
/// </para>
/// </remarks>
public sealed partial class AccountClosureOperatorOperations
{
    /// <summary>
    /// The most Memberships of the person one Finalize reads for its tenant passes: four for every group of the fence
    /// read's bound. A person beyond it is refused as a defect rather than redacted partially.
    /// </summary>
    internal const int MaximumMemberships = 4 * PlatformIdentityFences.MaximumGroups;

    /// <summary>The budget a result row recording a stop at the provider is written on, detached from the caller.</summary>
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(3);

    /// <summary>The audit log's four retained kinds, the only ones a redaction report may set (ADR-0107 § 6).</summary>
    private static readonly FrozenSet<RetainedReferenceKind> AuditKinds = new[]
    {
        RetainedReferenceKind.AuditActorReference, RetainedReferenceKind.AuditEntityReference,
        RetainedReferenceKind.AuditMetadataReference, RetainedReferenceKind.UnscopedAuditRows,
    }.ToFrozenSet();

    private static readonly string Underway = State(("status", "fulfilling"), ("account", "closing"));

    // The closure, locked, with its Begin moment (the update stamp) and its recorded result, by primary key.
    private const string LockClosure = """
        SELECT kind, subject_user_id, status, row_version, updated_at, result::text
        FROM data_subject_requests WHERE id = @id FOR UPDATE
        """;

    // The person's account by primary key, through the four granted columns: the status, the raw subject the provider
    // deletes, and the key the sentinel pass selects by. The User fence serializes every writer of the row.
    private const string ReadAccount = "SELECT status, provider_subject, subject_key FROM users WHERE id = @subject";

    private const string ReadRecorded = "SELECT result::text FROM data_subject_requests WHERE id = @id";

    // The users trigger exception admits it only for this session, from Closing, with every fence held.
    private const string CloseAccount = "UPDATE users SET status = 'Closed', provider_subject = NULL WHERE id = @subject AND status = 'Closing'";

    // A participant erasure's privileged step: Step 2's arm for the person's or an administrator's own erasure, the closure
    // arm for a fan-out one; either admits a result change in the audit log's kinds alone and stamps the completion time.
    private const string CompleteErasure = """
        UPDATE data_subject_requests SET status = 'completed', result = @result::jsonb, row_version = row_version + 1
        WHERE id = @id AND kind = 'erasure' AND status = 'awaiting_privileged_step' AND row_version = @version
        """;

    // A closure of the person, from fulfilling or never begun; the guard admits either only for a Closed person, under
    // every fence, with a result change in the audit log's kinds and the unserved groups alone.
    private const string CompleteClosure = """
        UPDATE data_subject_requests SET status = 'completed', result = @result::jsonb, row_version = row_version + 1
        WHERE id = @id AND kind = 'closure' AND status = @status AND row_version = @version
        """;

    /// <summary>
    /// For each of the person's groups, the newest erasure of the person there: the open one if there is one (at most one,
    /// by <c>ux_data_subject_requests_active</c>), else the newest terminal one, with whether it completed at or after the
    /// closure's Begin and whether its frozen Memberships include every Membership the person holds in the group. It reads
    /// the person's requests through <c>ix_data_subject_requests_subject_user_id</c> and each group's Memberships through
    /// <c>ix_memberships_user_id</c>, so its cost is the person's own rows. Exposed so that a test can ask for its plan.
    /// </summary>
    public static string ReadinessSql { get; } = """
        SELECT DISTINCT ON (r.tenant_id, r.organization_id)
            r.tenant_id, r.organization_id, r.id, r.status, r.row_version, r.result::text,
            COALESCE(r.completed_at >= @began, false),
            NOT EXISTS (SELECT 1 FROM public.memberships AS member
                WHERE member.user_id = r.subject_user_id AND member.tenant_id = r.tenant_id
                  AND member.organization_id IS NOT DISTINCT FROM r.organization_id
                  AND NOT r.membership_snapshot @> jsonb_build_array(member.id::text))
        FROM public.data_subject_requests AS r
        WHERE r.subject_user_id = @subject AND r.kind = 'erasure'
        ORDER BY r.tenant_id, r.organization_id,
            r.status IN ('scheduled', 'fulfilling', 'awaiting_privileged_step') DESC, r.created_at DESC, r.id DESC
        """;

    /// <summary>
    /// Every Membership the person holds, of any status, by tenant: the identifiers each tenant pass compares. Read through
    /// <c>ix_memberships_user_id</c> and bounded by <see cref="MaximumMemberships"/>. Exposed so that a test can ask for its plan.
    /// </summary>
    public static string MembershipsSql { get; } = """
        SELECT member.tenant_id, member.id FROM public.memberships AS member
        WHERE member.user_id = @subject
        ORDER BY member.tenant_id, member.id
        LIMIT @limit
        """;

    /// <summary>
    /// The person's other open closures, locked: at most one a group by <c>ux_data_subject_requests_active</c>, so bounded by
    /// the fence read's group bound, and read through <c>ix_data_subject_requests_subject_user_id</c>. Exposed so that a
    /// test can ask for its plan.
    /// </summary>
    public static string OpenClosuresSql { get; } = """
        SELECT id, status, row_version, result::text FROM public.data_subject_requests
        WHERE subject_user_id = @subject AND kind = 'closure' AND status IN ('scheduled', 'fulfilling') AND id <> @id
        ORDER BY id
        LIMIT @limit
        FOR UPDATE
        """;

    /// <inheritdoc />
    public async Task<ClosureFinalizeResult> FinalizeAsync(
        PlatformAdmission admission, Guid closureRequestId, CancellationToken cancellationToken = default)
    {
        RequireOperation(admission, PlatformOperations.ClosureFinalize);
        if (closureRequestId == Guid.Empty)
        {
            throw new ArgumentException("A closure Finalize names a request.", nameof(closureRequestId));
        }

        var commitSent = false;
        try
        {
            return await FinalizeCoreAsync(admission, closureRequestId, () => commitSent = true, cancellationToken);
        }
        catch (Exception failure) when (commitSent && !IsFatal(failure))
        {
            // The final commit was sent: it may have landed, so this is never "nothing changed, repeat it". A repeat after
            // a landed commit finds the closure completed and its person Closed, which is how the operator finds out.
            throw new OperatorRecoveryUnavailableException(commitOutcomeUnknown: true, cause: failure);
        }
        catch (Exception failure) when (!commitSent && IsRetryable(failure, cancellationToken))
        {
            // Nothing of the closure's state committed. A redaction that committed with its intent row is idempotent, and
            // a deleted account is found absent by the repeat, which is success.
            throw new OperatorRecoveryUnavailableException(cause: failure);
        }
    }

    private async Task<ClosureFinalizeResult> FinalizeCoreAsync(
        PlatformAdmission admission, Guid requestId, Action sendingCommit, CancellationToken cancellationToken)
    {
        await using var handle = await scope.EnterAsync(admission, cancellationToken);
        var id = new PlatformSqlParameter("id", requestId);
        var entity = requestId.ToString("D", CultureInfo.InvariantCulture);

        // 1. The request as it stands, unlocked: what it is, whose it is and where it stands.
        var found = await handle.QueryAsync(ReadRequest, [id], ClosureRequest.Read, cancellationToken);
        if (found.Count == 0)
        {
            return new ClosureFinalizeResult(ClosureFinalizeOutcome.NotFound);
        }

        var request = found[0];
        if (request.Kind != DataRequestKind.Closure)
        {
            return new ClosureFinalizeResult(ClosureFinalizeOutcome.NotClosure, StatusCode(request.Status));
        }

        if (Judge(request.Status, request.Account) is { } early)
        {
            // Nothing is written: the scope ends by disposal and its entry row is the record.
            return await EndedAsync(handle, id, early, cancellationToken);
        }

        // 2. The first transaction: the fences, the participants read under the User fence among them.
        var fences = await PlatformIdentityFences.AcquireAsync(handle, request.Subject, request.Tenant, cancellationToken);
        var (closure, account) = await LockAsync(handle, id, request.Subject, cancellationToken);
        if (Judge(closure.Status, account.Status) is { } judged)
        {
            // Another operator's Finalize committed while this one waited for the fences.
            return await EndedAsync(handle, id, judged, cancellationToken);
        }

        // 3. Ready: every served group's erasure has run and covers the group. The Begin moment is the closure's own stamp.
        var began = closure.BegunAt ?? throw Defect("fulfilling_closure_without_begin_stamp");
        var readiness = await ReadinessAsync(handle, request.Subject, began, fences.Groups, cancellationToken);
        if (readiness.Tally.Pending > 0)
        {
            return new ClosureFinalizeResult(ClosureFinalizeOutcome.GroupsPending, StatusCode(closure.Status), readiness.Tally);
        }

        var providerSubject = account.ProviderSubject is { Length: > 0 } held
            ? held
            : throw Defect("closing_account_without_provider_subject");

        // 4. The redaction, every participant tenant then the sentinel, on this transaction, and the checkpoint that
        //    commits it with its evidence. Nothing here is undone by what follows.
        var redaction = await RedactAsync(handle, request.Subject, account.Key, fences.Groups, cancellationToken);
        await handle.WriteIntentAsync(
            new PlatformResult(TenantId.PlatformSentinel, EntityType, entity, Underway, redaction.ToClosedJson()), cancellationToken);

        // 5. The provider, before any other statement: the new transaction holds nothing while it runs.
        var removal = await RemoveAccountAsync(providerSubject, cancellationToken);
        if (removal is not (AccountRemovalOutcome.Removed or AccountRemovalOutcome.Absent))
        {
            var (outcome, token) = removal switch
            {
                AccountRemovalOutcome.Refused => (ClosureFinalizeOutcome.ProviderRefused, "provider_refused"),
                AccountRemovalOutcome.Unavailable => (ClosureFinalizeOutcome.ProviderUnavailable, "provider_unavailable"),
                _ => (ClosureFinalizeOutcome.ProviderUncertain, "provider_uncertain"),
            };
            LogProviderStopped(logger, token);
            await RecordEndingAsync(handle, entity, token, State(("outcome", token), ("status", "fulfilling"), ("account", "closing")));
            return new ClosureFinalizeResult(outcome, StatusCode(DataRequestStatus.Fulfilling), Redaction: redaction);
        }

        var provider = removal == AccountRemovalOutcome.Removed ? "removed" : "absent";

        // 6. The second transaction: the fences again, because the first transaction's ended at the checkpoint, and every
        //    judgment again under them.
        var again = await PlatformIdentityFences.AcquireAsync(handle, request.Subject, request.Tenant, cancellationToken);
        var (current, person) = await LockAsync(handle, id, request.Subject, cancellationToken);
        if (current.Status == DataRequestStatus.Completed && person.Status == UserAccount.Closed)
        {
            // Another operator's Finalize finished the closure while this one called the provider.
            const string Token = "already_finalized";
            await RecordEndingAsync(handle, entity, Token,
                State(("outcome", Token), ("status", "completed"), ("account", "closed"), ("provider", provider)));
            return new ClosureFinalizeResult(
                ClosureFinalizeOutcome.AlreadyFinalized, StatusCode(DataRequestStatus.Completed), Redaction: redaction,
                Retained: Counts(current.Result));
        }

        if (current.Status != DataRequestStatus.Fulfilling || person.Status != UserAccount.Closing || current.Version != closure.Version
            || current.BegunAt != began || !again.Groups.SequenceEqual(fences.Groups))
        {
            // Nothing returns a User to Active, admits a Membership for a Closing one or writes a fulfilling closure but this
            // act, so a change between the two transactions is a broken invariant.
            throw Defect("closure_changed_between_transactions");
        }

        var ready = await ReadinessAsync(handle, request.Subject, began, again.Groups, cancellationToken);
        if (ready.Tally.Pending > 0)
        {
            const string Token = "groups_pending";
            await RecordEndingAsync(handle, entity, Token,
                State(("outcome", Token), ("status", "fulfilling"), ("account", "closing"), ("provider", provider),
                    ("groups", Tally(ready.Tally))));
            return new ClosureFinalizeResult(
                ClosureFinalizeOutcome.GroupsPending, StatusCode(DataRequestStatus.Fulfilling), ready.Tally, Redaction: redaction);
        }

        // 7. The account first, because the closure arrows require it Closed; then the participants' erasures, this closure
        //    and the person's other open closures; then the result row, all on this one transaction.
        var subject = new PlatformSqlParameter("subject", request.Subject.Value);
        if (await handle.ExecuteAsync(CloseAccount, [subject], cancellationToken) != 1)
        {
            throw Defect("account_not_closed");
        }

        foreach (var erasure in ready.Awaiting)
        {
            var completed = await handle.ExecuteAsync(
                CompleteErasure,
                [
                    new PlatformSqlParameter("id", erasure.Id), new PlatformSqlParameter("version", erasure.Version),
                    new PlatformSqlParameter("result", Merge(erasure.Result, redaction.Tenants[erasure.Group.TenantId], unserved: null)),
                ],
                cancellationToken);
            if (completed != 1)
            {
                throw Defect("erasure_not_completed");
            }
        }

        var unserved = ready.Tally.Unserved;
        var closureResult = Merge(current.Result, redaction.Total, unserved);
        await CompleteClosureAsync(handle, requestId, DataRequestStatus.Fulfilling, current.Version, closureResult, cancellationToken);
        var others = await handle.QueryAsync(
            OpenClosuresSql,
            [subject, id, new PlatformSqlParameter("limit", PlatformIdentityFences.MaximumGroups + 1)],
            reader => (Id: reader.GetGuid(0), Status: DataRequestCodes.Statuses.Parse(reader.GetString(1)), Version: reader.GetInt64(2),
                Result: reader.GetString(3)),
            cancellationToken);
        if (others.Count > PlatformIdentityFences.MaximumGroups)
        {
            throw Defect("open_closures_beyond_bound");
        }

        foreach (var other in others)
        {
            await CompleteClosureAsync(
                handle, other.Id, other.Status, other.Version, Merge(other.Result, redaction.Total, unserved), cancellationToken);
        }

        var groups = ready.Tally with { ErasuresCompleted = ready.Awaiting.Count, ClosuresCompleted = others.Count + 1 };
        await handle.WriteResultAsync(
            new PlatformResult(
                TenantId.PlatformSentinel, EntityType, entity, Underway,
                State(
                    ("outcome", "finalized"), ("status", "completed"), ("account", "closed"), ("provider", provider),
                    ("groups", Tally(groups)), ("retained", Document(closureResult)))),
            cancellationToken);

        // Past the point where the caller's abort matters: a cancelled commit leaves its outcome unknown.
        sendingCommit();
        await handle.CommitAsync(CancellationToken.None);
        return new ClosureFinalizeResult(
            ClosureFinalizeOutcome.Finalized, StatusCode(DataRequestStatus.Completed), groups,
            [.. again.Groups.Where(group => !ready.Served.Contains(group))], redaction, Counts(closureResult));
    }

    /// <summary>
    /// The judgment of where the closure stands: nothing to say while it is <c>fulfilling</c> with its person Closing, an
    /// earlier finalization, a closure never begun or cancelled, or a state no admitted path writes.
    /// </summary>
    private ClosureFinalizeResult? Judge(DataRequestStatus status, UserAccount account) => status switch
    {
        DataRequestStatus.Fulfilling when account == UserAccount.Closing => null,
        DataRequestStatus.Fulfilling => throw Defect("fulfilling_closure_account_not_closing"),
        DataRequestStatus.Completed when account == UserAccount.Closed =>
            new ClosureFinalizeResult(ClosureFinalizeOutcome.AlreadyFinalized, StatusCode(status)),
        DataRequestStatus.Completed => throw Defect("completed_closure_account_not_closed"),
        _ => new ClosureFinalizeResult(ClosureFinalizeOutcome.NotFinalizable, StatusCode(status)),
    };

    /// <summary>An ending before the checkpoint: nothing is written, and an earlier finalization answers what it recorded.</summary>
    private static async Task<ClosureFinalizeResult> EndedAsync(
        IPlatformAdminScopeHandle handle, PlatformSqlParameter id, ClosureFinalizeResult ended, CancellationToken cancellationToken)
    {
        if (ended.Outcome != ClosureFinalizeOutcome.AlreadyFinalized)
        {
            return ended;
        }

        var recorded = await handle.QueryAsync(ReadRecorded, [id], reader => reader.GetString(0), cancellationToken);
        return ended with { Retained = Counts(recorded.Single()) };
    }

    /// <summary>The closure locked and the person's account read, both under the fences this transaction holds.</summary>
    private static async Task<(FinalizingClosure Closure, ClosingAccount Account)> LockAsync(
        IPlatformAdminScopeHandle handle, PlatformSqlParameter id, UserId subject, CancellationToken cancellationToken)
    {
        var locked = await handle.QueryAsync(LockClosure, [id], FinalizingClosure.Read, cancellationToken);
        if (locked.Count != 1 || locked[0].Kind != DataRequestKind.Closure || locked[0].Subject != subject)
        {
            // A request's kind and person are immutable and it is never deleted.
            throw new InvalidOperationException("The closure request changed its kind or person; the act is abandoned.");
        }

        var accounts = await handle.QueryAsync(
            ReadAccount, [new PlatformSqlParameter("subject", subject.Value)], ClosingAccount.Read, cancellationToken);
        return (locked[0], accounts.Count == 1
            ? accounts[0]
            : throw new InvalidOperationException("A closure's person has no readable User; the act is abandoned."));
    }

    /// <summary>
    /// Where each participant group stands: the served groups that are ready and the participant erasures awaiting the
    /// privileged step, which the second transaction completes. An unserved group needs no erasure; one that reached the
    /// privileged step before its tenant or organization stopped allowing ordinary work is completed with the others, since
    /// its job ran and its tenant is redacted.
    /// </summary>
    private async Task<Readiness> ReadinessAsync(
        IPlatformAdminScopeHandle handle, UserId subject, DateTimeOffset began, IReadOnlyList<ClosureParticipantGroup> groups,
        CancellationToken cancellationToken)
    {
        var participants = await ClassifyAsync(handle, groups, cancellationToken);
        var newest = (await handle.QueryAsync(
                ReadinessSql,
                [new PlatformSqlParameter("subject", subject.Value), new PlatformSqlParameter("began", began)],
                GroupErasure.Read,
                cancellationToken))
            .ToDictionary(erasure => erasure.Group);
        var served = participants.Served.ToHashSet();
        var ready = participants.Served.Count(group => newest.TryGetValue(group, out var erasure) && erasure.Covers
            && (erasure.Status == DataRequestStatus.AwaitingPrivilegedStep
                || (erasure.Status == DataRequestStatus.Completed && erasure.CompletedDuringClosure)));
        var awaiting = groups.Where(group => newest.TryGetValue(group, out var erasure)
                && erasure.Status == DataRequestStatus.AwaitingPrivilegedStep)
            .Select(group => newest[group]).ToList();
        return new Readiness(
            new ClosureFinalizeTally(served.Count, ready, served.Count - ready, groups.Count - served.Count), served, awaiting);
    }

    /// <summary>
    /// The redaction: one pass over every participant tenant, served or not, with every Membership the person holds there,
    /// then one over the platform sentinel tenant by the person's subject key; their reports summed.
    /// </summary>
    private static async Task<AuditRedactionAggregate> RedactAsync(
        IPlatformAdminScopeHandle handle, UserId subject, IdentitySubjectKey key, IReadOnlyList<ClosureParticipantGroup> groups,
        CancellationToken cancellationToken)
    {
        var memberships = await handle.QueryAsync(
            MembershipsSql,
            [new PlatformSqlParameter("subject", subject.Value), new PlatformSqlParameter("limit", MaximumMemberships + 1)],
            reader => (Tenant: TenantId.From(reader.GetGuid(0)), Id: reader.GetGuid(1)),
            cancellationToken);
        if (memberships.Count > MaximumMemberships)
        {
            throw new InvalidOperationException(
                "A closure's person holds more Memberships than one Finalize redacts by; the act is abandoned.");
        }

        var reports = new Dictionary<TenantId, AuditRedactionReport>();
        foreach (var tenant in groups.Select(group => group.TenantId).Distinct().OrderBy(tenant => tenant.Value))
        {
            var plan = AuditRedactionPlan.ForClosureTenant(
                tenant, subject, memberships.Where(member => member.Tenant == tenant).Select(member => member.Id));
            reports[tenant] = await handle.RedactAuditAsync(plan, cancellationToken);
        }

        var sentinel = await handle.RedactAuditAsync(AuditRedactionPlan.ForClosureSentinel(subject, key), cancellationToken);
        return new AuditRedactionAggregate(reports, sentinel);
    }

    /// <summary>
    /// The provider's deletion. A port that throws, the caller's leaving included, answers nothing about whether the deletion
    /// reached the provider: it is uncertain, and a repeat finds the account absent if it had landed.
    /// </summary>
    private async Task<AccountRemovalOutcome> RemoveAccountAsync(string providerSubject, CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await accountRemoval.RemoveAsync(providerSubject, cancellationToken);
            return Enum.IsDefined(outcome) ? outcome : AccountRemovalOutcome.Uncertain;
        }
        catch (Exception failure) when (!IsFatal(failure))
        {
            // The exception's type only: its text is the adapter's or the transport's, and nothing of it is the operator's.
            LogProviderThrew(logger, failure.GetType().Name);
            return AccountRemovalOutcome.Uncertain;
        }
    }

    /// <summary>
    /// Writes and commits the result row of an attempt that ended after its checkpoint without finalizing, on a budget
    /// detached from the caller. Nothing of the closure's state changes in that transaction, so a row that cannot be written
    /// changes no answer: the intent row stands alone, the failure is logged, and the act's answer is what it found.
    /// </summary>
    private async Task RecordEndingAsync(IPlatformAdminScopeHandle handle, string entity, string token, string after)
    {
        using var budget = new CancellationTokenSource(CompletionBudget);
        try
        {
            await handle.WriteResultAsync(
                new PlatformResult(TenantId.PlatformSentinel, EntityType, entity, Underway, after), budget.Token);
            await handle.CommitAsync(budget.Token);
        }
        catch (Exception failure) when (!IsFatal(failure))
        {
            LogEndingLost(logger, token, failure);
        }
    }

    private static async Task CompleteClosureAsync(
        IPlatformAdminScopeHandle handle, Guid id, DataRequestStatus status, long version, string result,
        CancellationToken cancellationToken)
    {
        var completed = await handle.ExecuteAsync(
            CompleteClosure,
            [
                new PlatformSqlParameter("id", id), new PlatformSqlParameter("status", StatusCode(status)),
                new PlatformSqlParameter("version", version), new PlatformSqlParameter("result", result),
            ],
            cancellationToken);
        if (completed != 1)
        {
            throw new InvalidOperationException("A locked closure of the person did not complete in exactly one row; the act is abandoned.");
        }
    }

    /// <summary>
    /// A request's recorded result with the redaction's retained audit kinds set, and for a closure its unserved groups. A
    /// kind the report leaves out keeps its recorded count, because a report's list is partial and an absent kind is not zero.
    /// </summary>
    private static string Merge(string recorded, AuditRedactionReport report, int? unserved)
    {
        var result = JsonNode.Parse(recorded)?.AsObject()
            ?? throw new InvalidOperationException("A request's recorded result is not a document; the act is abandoned.");
        foreach (var entry in report.Retained)
        {
            if (!AuditKinds.Contains(entry.Kind))
            {
                throw new InvalidOperationException("The redaction reported a retained kind the audit log does not hold; the act is abandoned.");
            }

            result[DataRequestCodes.RetainedKinds.ToCode(entry.Kind)] = entry.Count;
        }

        if (unserved is { } count)
        {
            result[DataRequestCodes.RetainedKinds.ToCode(RetainedReferenceKind.UnservedGroup)] = count;
        }

        return result.ToJsonString();
    }

    /// <summary>A recorded result as closed kind codes and counts; every key is parsed against the closed vocabulary.</summary>
    private static Dictionary<string, long> Counts(string recorded)
    {
        var result = JsonNode.Parse(recorded)?.AsObject()
            ?? throw new InvalidOperationException("A request's recorded result is not a document; the act is abandoned.");
        return result.ToDictionary(
            entry => DataRequestCodes.RetainedKinds.ToCode(DataRequestCodes.RetainedKinds.Parse(entry.Key)),
            entry => entry.Value!.GetValue<long>(),
            StringComparer.Ordinal);
    }

    private static JsonObject Document(string recorded) => JsonNode.Parse(recorded)!.AsObject();

    private static JsonObject Tally(ClosureFinalizeTally tally) => new()
    {
        ["served"] = tally.Served,
        ["ready"] = tally.Ready,
        ["pending"] = tally.Pending,
        ["unserved"] = tally.Unserved,
        ["erasures_completed"] = tally.ErasuresCompleted,
        ["closures_completed"] = tally.ClosuresCompleted,
    };

    /// <summary>Logs a broken invariant by its closed name and returns the exception that abandons the act.</summary>
    private InvalidOperationException Defect(string defect)
    {
        LogDefect(logger, defect);
        return new InvalidOperationException("A closure's Finalize found a state no admitted path writes; the act is abandoned.");
    }

    [LoggerMessage(EventId = 7121, Level = LogLevel.Warning,
        Message = "A closure's Finalize stopped at the identity provider ({Outcome}); the closure stays fulfilling and a repeat resumes it.")]
    private static partial void LogProviderStopped(ILogger logger, string outcome);

    [LoggerMessage(EventId = 7122, Level = LogLevel.Error,
        Message = "A closure's Finalize found {Defect}, a state no admitted path writes; the act is abandoned.")]
    private static partial void LogDefect(ILogger logger, string defect);

    [LoggerMessage(EventId = 7123, Level = LogLevel.Critical,
        Message = "A closure Finalize's result row recording {Outcome} could not be written; its intent row stands alone and the answer is unchanged.")]
    private static partial void LogEndingLost(ILogger logger, string outcome, Exception failure);

    [LoggerMessage(EventId = 7124, Level = LogLevel.Warning,
        Message = "The identity provider's account removal threw {ExceptionType}; the deletion is treated as uncertain.")]
    private static partial void LogProviderThrew(ILogger logger, string exceptionType);

    /// <summary>The closure as the act reads it locked, under every fence.</summary>
    private sealed record FinalizingClosure(
        DataRequestKind Kind, UserId Subject, DataRequestStatus Status, long Version, DateTimeOffset? BegunAt, string Result)
    {
        internal static FinalizingClosure Read(DbDataReader reader) => new(
            DataRequestCodes.Kinds.Parse(reader.GetString(0)), UserId.From(reader.GetGuid(1)),
            DataRequestCodes.Statuses.Parse(reader.GetString(2)), reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4), reader.GetString(5));

        /// <summary>Never prints the person.</summary>
        public override string ToString() => nameof(FinalizingClosure);
    }

    /// <summary>The person's account under the fences: its status, the raw provider subject and the subject key.</summary>
    private sealed record ClosingAccount(UserAccount Status, string? ProviderSubject, IdentitySubjectKey Key)
    {
        // ck_users_subject_lifecycle keeps the raw subject on an Active User, Begin's arrow keeps it on Closing, and Closed
        // removes it; a Closed account is judged before the subject is ever needed.
        internal static ClosingAccount Read(DbDataReader reader) => new(
            ParseAccount(reader.GetString(0)), reader.IsDBNull(1) ? null : reader.GetString(1),
            IdentitySubjectKey.FromBytes(reader.GetFieldValue<byte[]>(2)));

        /// <summary>Never prints the subject or the key.</summary>
        public override string ToString() => nameof(ClosingAccount);
    }

    /// <summary>One group's newest erasure of the person, and whether it stands for the group.</summary>
    private sealed record GroupErasure(
        ClosureParticipantGroup Group, Guid Id, DataRequestStatus Status, long Version, string Result,
        bool CompletedDuringClosure, bool Covers)
    {
        internal static GroupErasure Read(DbDataReader reader) => new(
            new ClosureParticipantGroup(
                TenantId.From(reader.GetGuid(0)), reader.IsDBNull(1) ? null : OrganizationId.From(reader.GetGuid(1))),
            reader.GetGuid(2), DataRequestCodes.Statuses.Parse(reader.GetString(3)), reader.GetInt64(4), reader.GetString(5),
            reader.GetBoolean(6), reader.GetBoolean(7));

        /// <summary>Never prints the request's group.</summary>
        public override string ToString() => nameof(GroupErasure);
    }

    /// <summary>The tallies, the served groups and the participant erasures awaiting the privileged step.</summary>
    private sealed record Readiness(
        ClosureFinalizeTally Tally, IReadOnlySet<ClosureParticipantGroup> Served, IReadOnlyList<GroupErasure> Awaiting)
    {
        public override string ToString() => nameof(Readiness);
    }
}
