using LearnStack.Infrastructure.PlatformOperator;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Api.Platform;

/// <summary>What a closure's fan-out did, by participant group: closed counts, never an identifier.</summary>
/// <param name="Served">Groups whose tenant allows ordinary work and whose organization, if any, is Active.</param>
/// <param name="Started">Served groups whose fan-out erasure this call created.</param>
/// <param name="Adopted">Served groups whose erasure was already in flight or awaiting its operator step.</param>
/// <param name="Waiting">Served groups with an erasure past its window and not yet run; a repeated Begin picks them up.</param>
/// <param name="Failed">Served groups whose act did not report a start or an adoption; a repeated Begin retries them.</param>
/// <param name="Unserved">Groups of a tenant that does not allow ordinary work or of an organization not Active.</param>
internal sealed record ClosureGroupsResponse(int Served, int Started, int Adopted, int Waiting, int Failed, int Unserved);

/// <summary>
/// What a Begin that committed, or found its closure already begun, answers. <c>complete</c> is false while a served group
/// is waiting or failed: the closure stands, and the operator repeats Begin.
/// </summary>
/// <param name="Outcome"><c>begun</c> or <c>resumed</c>.</param>
/// <param name="RequestId">The closure request.</param>
/// <param name="Status">The request's stored status, <c>fulfilling</c>.</param>
/// <param name="Complete">Whether every served group is started or adopted.</param>
/// <param name="Groups">The per-group counts.</param>
/// <param name="CorrelationId">The correlation id the result row and the fan-out's rows carry.</param>
internal sealed record ClosureBeginResponse(
    string Outcome, Guid RequestId, string Status, bool Complete, ClosureGroupsResponse Groups, string CorrelationId);

/// <summary>Where a Finalize found the participant groups: closed counts, never an identifier.</summary>
/// <param name="Served">Groups whose tenant allows ordinary work and whose organization, if any, is Active; a group that is no longer served but still holds an open closure erasure is required as well, so ready and pending count required groups.</param>
/// <param name="Ready">Required groups whose erasure awaits the privileged step or completed during the closure.</param>
/// <param name="Pending">Required groups that are not ready.</param>
/// <param name="Unserved">Groups of a tenant that does not allow ordinary work or of an organization not Active.</param>
/// <param name="ErasuresCompleted">The participant erasures this call completed.</param>
/// <param name="ClosuresCompleted">The person's closure requests this call completed, this one included.</param>
internal sealed record ClosureFinalizeGroupsResponse(
    int Served, int Ready, int Pending, int Unserved, int ErasuresCompleted, int ClosuresCompleted);

/// <summary>One unserved group, which Phase 11's offboarding owns: a tenant and its organization, or none.</summary>
internal sealed record ClosureUnservedGroupResponse(Guid TenantId, Guid? OrganizationId);

/// <summary>This call's redaction passes, summed: closed counts per column and per retained kind.</summary>
/// <param name="Tenants">The participant tenants a pass ran over; the platform sentinel tenant's pass is in the totals.</param>
/// <param name="RowsSelected">The audit rows the passes selected.</param>
/// <param name="Redacted">The rows changed per redacted column; zero everywhere on a repeat.</param>
/// <param name="Retained">The retained references per closed kind.</param>
internal sealed record ClosureRedactionResponse(
    int Tenants, long RowsSelected, IReadOnlyDictionary<string, long> Redacted, IReadOnlyDictionary<string, long> Retained);

/// <summary>
/// What a Finalize that completed the closure, or found it completed, answers. A repeat of a finalization that had landed
/// answers <c>already_finalized</c> with the recorded result and no counts of its own.
/// </summary>
/// <param name="Outcome"><c>finalized</c> or <c>already_finalized</c>.</param>
/// <param name="RequestId">The closure request.</param>
/// <param name="Status">The request's stored status, <c>completed</c>.</param>
/// <param name="Groups">For the call that finalized, the per-group counts.</param>
/// <param name="UnservedGroups">For the call that finalized, the unserved groups by tenant and organization.</param>
/// <param name="Redaction">For the call that finalized, its redaction passes summed.</param>
/// <param name="Retained">The closure request's recorded result: the retained audit kinds and <c>unserved_group</c>.</param>
/// <param name="CorrelationId">The correlation id the intent and result rows carry.</param>
internal sealed record ClosureFinalizeResponse(
    string Outcome, Guid RequestId, string Status, ClosureFinalizeGroupsResponse? Groups,
    IReadOnlyList<ClosureUnservedGroupResponse> UnservedGroups, ClosureRedactionResponse? Redaction,
    IReadOnlyDictionary<string, long> Retained, string CorrelationId);

/// <summary>
/// The account-closure routes of the operator surface (ADR-0107 §§ 7 and 8): the Begin and the Finalize of one person's
/// global closure. Each runs the gate first, under <c>platform.data_request.write</c>.
/// </summary>
/// <remarks>
/// <para>
/// A Begin judges and moves the person and the request in one platform transaction and then fans the closure out: a
/// refusal answers <c>409</c> with the closed code it recorded on the request, a closure that could never finish (a person
/// past the closure's bounds, or an account the provider's port could never delete) answers <c>409</c> on
/// <c>errors.participants</c> or <c>errors.provider</c> with nothing recorded, a fan-out that did not reach every served
/// group answers <c>200</c> with <c>complete: false</c>, and a commit whose outcome is unknown answers <c>503</c> with
/// <c>errors.outcome</c>, after which a repeat is safe and resumes.
/// </para>
/// <para>
/// A Finalize redacts and records its intent, deletes the provider account, then closes the account and completes the
/// requests: a closure not ready answers <c>409</c> with the closed tallies on <c>errors.groups</c>, a provider that did not
/// delete answers <c>503</c> with the provider's closed outcome on <c>errors.provider</c>, and a repeat resumes from where
/// any earlier call stopped.
/// </para>
/// </remarks>
internal static partial class PlatformOperatorEndpoints
{
    private static void MapClosure(RouteGroupBuilder group)
    {
        Describe(
            group.MapPost(DataRequestPrefix + "/{requestId:guid}/begin-closure", BeginClosureAsync)
                .WithName("PlatformClosureBegin")
                .WithSummary("Begin a person's global account closure: fence, judge, move the person to Closing and fan out.")
                .WithDescription(
                    "One platform transaction takes the person's User fence and every participant tenant's fence, refuses "
                    + "blocked_window (the request's execution time has not come by the database clock), blocked_hold (a hold "
                    + "source reports one) or blocked_last_administrator (the person is the only eligible tenant-wide "
                    + "administrator of a participant tenant that allows ordinary work) by recording the code on the request, "
                    + "or moves the person's User from Active to Closing and the request from scheduled to fulfilling, and "
                    + "writes the result row under the platform sentinel with the ticket. After that commit the closure is "
                    + "fanned out to every served participant group, each in its own transaction. A group whose tenant does "
                    + "not allow ordinary work or whose organization is not Active is unserved and is not fanned out. Before "
                    + "any write, and with no code recorded, it refuses a closure that could never finish: a person with "
                    + string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"Memberships in more than {ClosureLimits.MaximumParticipantGroups} participant groups, served or not, ")
                    + string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"or with more than {ClosureLimits.MaximumTenantMemberships} Memberships in one participant tenant, ")
                    + "and a person whose identity-provider account the account-removal client could never delete. "
                    + "complete is false while a served group is waiting or failed: repeat Begin, which resumes, judges "
                    + "nothing again and adopts what started. Closing cannot be undone."),
            typeof(ClosureBeginResponse));
        Describe(
            group.MapPost(DataRequestPrefix + "/{requestId:guid}/finalize-closure", FinalizeClosureAsync)
                .WithName("PlatformClosureFinalize")
                .WithSummary("Finalize a begun closure: redact the person's audit rows, delete the provider account, close the account.")
                .WithDescription(
                    "Allowed once every served participant group's newest erasure of the person awaits its privileged step or "
                    + "completed during the closure; a group whose tenant does not allow ordinary work or whose organization is "
                    + "not Active is unserved and needs none. The first platform transaction takes the person's fences, redacts "
                    + "the person's audit rows in every participant tenant and in the platform sentinel tenant, and commits with "
                    + "the intent row that carries the summed counts. The person's identity-provider account is then deleted; a "
                    + "missing account is success. The second transaction takes the fences again, moves the account from Closing "
                    + "to Closed with the raw provider subject removed, completes every participant erasure awaiting the "
                    + "privileged step, this closure and every other open closure of the person, and writes the result row under "
                    + "the platform sentinel with the ticket. The first transaction holds the person's User fence alone, so no "
                    + "participant tenant waits for the redaction; the second holds every participant tenant's fence too. A "
                    + "group made unserved after the Begin whose fan-out erasure is still open stays required until it awaits "
                    + "its privileged step. A provider that did not delete answers 503 and leaves the closure "
                    + "fulfilling; every step is idempotent, so a repeat resumes, and a repeat after the act landed answers "
                    + "already_finalized and writes nothing."),
            typeof(ClosureFinalizeResponse));
    }

    private static async Task<IResult> BeginClosureAsync(
        Guid requestId, HttpContext http, IPlatformOperatorGate gate, IAccountClosureOperatorOperations operations,
        ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var operation = PlatformOperations.ClosureBegin;
        var (body, refusal, admission) = await AdmitWithBodyAsync<DataRequestActBody>(
            http, gate, operation, request => request.Ticket, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        // A request identifier is a UUIDv7 the database assigned; anything else names no request and reaches nothing.
        if (body is null || !IsOperatorId(requestId))
        {
            return Problem(http, StatusCodes.Status400BadRequest, "lockey_validation_failed");
        }

        try
        {
            var result = await operations.BeginAsync(admission!, requestId, cancellationToken);
            PlatformOperatorMetrics.DataRequest(operation, ClosureOutcomeToken(result));
            return result.Outcome switch
            {
                ClosureBeginOutcome.Begun or ClosureBeginOutcome.Resumed => Results.Ok(new ClosureBeginResponse(
                    result.Outcome == ClosureBeginOutcome.Begun ? "begun" : "resumed", requestId, result.Status!,
                    result.Groups!.Complete,
                    new ClosureGroupsResponse(
                        result.Groups.Served, result.Groups.Started, result.Groups.Adopted, result.Groups.Waiting,
                        result.Groups.Failed, result.Groups.Unserved),
                    admission!.Operator.CorrelationId ?? http.TraceIdentifier)),
                ClosureBeginOutcome.RefusedWindow or ClosureBeginOutcome.RefusedHold
                    or ClosureBeginOutcome.RefusedLastAdministrator => ResultProblem(
                        http, Rule("refusal", "lockey_data_request_closure_refused", result.Refusal)),
                ClosureBeginOutcome.NotFound => Problem(http, StatusCodes.Status404NotFound, "lockey_not_found"),
                ClosureBeginOutcome.NotClosure => ResultProblem(http, Rule("kind", "lockey_data_request_not_closure")),
                ClosureBeginOutcome.NotBeginnable => ResultProblem(
                    http, Rule("status", "lockey_data_request_closure_not_beginnable", result.Status)),
                ClosureBeginOutcome.SubjectNotAdmitted => ResultProblem(
                    http, Rule("account", "lockey_data_request_closure_account_closed")),
                ClosureBeginOutcome.RefusedTooLarge => ResultProblem(
                    http, Rule("participants", "lockey_data_request_closure_too_large")),
                ClosureBeginOutcome.RefusedProvider => ResultProblem(http, Rule("provider", "lockey_account_removal_unavailable")),
                _ => throw new InvalidOperationException("A closure Begin outcome the route does not answer."),
            };
        }
        catch (AuditWriteFailedException failure)
        {
            PlatformOperatorMetrics.DataRequest(operation, "audit_unavailable");
            return AuditUnavailableProblem(http, loggers, operation, failure);
        }
        catch (OperatorRecoveryUnavailableException failure)
        {
            PlatformOperatorMetrics.DataRequest(operation, failure.CommitOutcomeUnknown ? "commit_unknown" : "unavailable");
            return UnavailableProblem(http, loggers, operation, failure);
        }
        catch (Exception) when (CountFailure(operation))
        {
            throw;
        }
    }

    private static async Task<IResult> FinalizeClosureAsync(
        Guid requestId, HttpContext http, IPlatformOperatorGate gate, IAccountClosureOperatorOperations operations,
        ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var operation = PlatformOperations.ClosureFinalize;
        var (body, refusal, admission) = await AdmitWithBodyAsync<DataRequestActBody>(
            http, gate, operation, request => request.Ticket, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        // A request identifier is a UUIDv7 the database assigned; anything else names no request and reaches nothing.
        if (body is null || !IsOperatorId(requestId))
        {
            return Problem(http, StatusCodes.Status400BadRequest, "lockey_validation_failed");
        }

        try
        {
            var result = await operations.FinalizeAsync(admission!, requestId, cancellationToken);
            PlatformOperatorMetrics.DataRequest(operation, FinalizeOutcomeToken(result.Outcome));
            return result.Outcome switch
            {
                ClosureFinalizeOutcome.Finalized or ClosureFinalizeOutcome.AlreadyFinalized => Results.Ok(new ClosureFinalizeResponse(
                    result.Outcome == ClosureFinalizeOutcome.Finalized ? "finalized" : "already_finalized", requestId, result.Status!,
                    result.Outcome == ClosureFinalizeOutcome.Finalized && result.Groups is { } groups
                        ? new ClosureFinalizeGroupsResponse(
                            groups.Served, groups.Ready, groups.Pending, groups.Unserved, groups.ErasuresCompleted, groups.ClosuresCompleted)
                        : null,
                    result.Outcome == ClosureFinalizeOutcome.Finalized
                        ? [.. (result.UnservedGroups ?? []).Select(group =>
                            new ClosureUnservedGroupResponse(group.TenantId.Value, group.OrganizationId?.Value))]
                        : [],
                    result.Outcome == ClosureFinalizeOutcome.Finalized && result.Redaction is { } redaction
                        ? new ClosureRedactionResponse(
                            redaction.Tenants.Count, redaction.Total.RowsSelected,
                            redaction.Total.Redacted.ToDictionary(
                                entry => AuditRedactionCodes.Columns.ToCode(entry.Key), entry => entry.Value, StringComparer.Ordinal),
                            redaction.Total.Retained.ToDictionary(
                                entry => DataRequestCodes.RetainedKinds.ToCode(entry.Kind), entry => entry.Count, StringComparer.Ordinal))
                        : null,
                    result.Retained ?? new Dictionary<string, long>(StringComparer.Ordinal),
                    admission!.Operator.CorrelationId ?? http.TraceIdentifier)),
                ClosureFinalizeOutcome.NotFound => Problem(http, StatusCodes.Status404NotFound, "lockey_not_found"),
                ClosureFinalizeOutcome.NotClosure => ResultProblem(http, Rule("kind", "lockey_data_request_not_closure")),
                ClosureFinalizeOutcome.NotFinalizable => ResultProblem(
                    http, Rule("status", "lockey_data_request_closure_not_finalizable", result.Status)),
                ClosureFinalizeOutcome.GroupsPending => ResultProblem(http, GroupsPending(result.Groups!)),
                ClosureFinalizeOutcome.ProviderRefused => ResultProblem(http, ProviderStopped("lockey_provider_refused")),
                ClosureFinalizeOutcome.ProviderUnavailable => ResultProblem(http, ProviderStopped("lockey_provider_unavailable")),
                ClosureFinalizeOutcome.ProviderUncertain => ResultProblem(http, ProviderStopped("lockey_provider_outcome_unknown")),
                _ => throw new InvalidOperationException("A closure Finalize outcome the route does not answer."),
            };
        }
        catch (AuditWriteFailedException failure)
        {
            PlatformOperatorMetrics.DataRequest(operation, "audit_unavailable");
            return AuditUnavailableProblem(http, loggers, operation, failure);
        }
        catch (OperatorRecoveryUnavailableException failure)
        {
            PlatformOperatorMetrics.DataRequest(operation, failure.CommitOutcomeUnknown ? "commit_unknown" : "unavailable");
            if (failure.InnerException is AuditWriteFailedException durable)
            {
                // A MUST row refused after the provider step answers a retryable 503, but the audit store's failure is still the
                // alert's signal: the act that failed at its last row had already deleted the account.
                PlatformOperatorMetrics.AuditFailed(operation);
                LogAuditRefused(loggers.CreateLogger(typeof(PlatformOperatorEndpoints).FullName!), durable);
            }

            return UnavailableProblem(http, loggers, operation, failure);
        }
        catch (Exception) when (CountFailure(operation))
        {
            throw;
        }
    }

    /// <summary>
    /// A 409 with the closed tallies on <c>errors.groups</c>: how many served groups are ready and pending and how many are
    /// unserved. No group is named, because a group's tenant and organization are the person's memberships.
    /// </summary>
    private static Error GroupsPending(ClosureFinalizeTally tally) => new(
        new LocalizedMessage("lockey_business_rule_violation"),
        new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
        {
            ["groups"] = [new LocalizedMessage(
                "lockey_data_request_closure_groups_pending",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["served"] = tally.Served.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["ready"] = tally.Ready.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["pending"] = tally.Pending.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["unserved"] = tally.Unserved.ToString(System.Globalization.CultureInfo.InvariantCulture),
                })],
        });

    /// <summary>
    /// A 503 whose reason is the provider's closed outcome on <c>errors.provider</c>: the redaction and its intent row
    /// committed, the closure stays <c>fulfilling</c>, and a repeat resumes. The status map is closed, so the reason travels
    /// on the field of <c>dependency_unavailable</c>.
    /// </summary>
    private static Error ProviderStopped(string reason) => new(
        new LocalizedMessage("lockey_dependency_unavailable"),
        new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
        {
            ["provider"] = [new LocalizedMessage(reason)],
        });

    private static string FinalizeOutcomeToken(ClosureFinalizeOutcome outcome) => outcome switch
    {
        ClosureFinalizeOutcome.Finalized => "finalized",
        ClosureFinalizeOutcome.AlreadyFinalized => "already_finalized",
        ClosureFinalizeOutcome.NotFound => "not_found",
        ClosureFinalizeOutcome.NotClosure => "not_closure",
        ClosureFinalizeOutcome.NotFinalizable => "not_finalizable",
        ClosureFinalizeOutcome.GroupsPending => "groups_pending",
        ClosureFinalizeOutcome.ProviderRefused => "provider_refused",
        ClosureFinalizeOutcome.ProviderUnavailable => "provider_unavailable",
        ClosureFinalizeOutcome.ProviderUncertain => "provider_uncertain",
        _ => throw new InvalidOperationException("A closure Finalize outcome the route does not count."),
    };

    private static string ClosureOutcomeToken(ClosureBeginResult result) => result.Outcome switch
    {
        ClosureBeginOutcome.Begun => result.Groups!.Complete ? "begun" : "begun_partial",
        ClosureBeginOutcome.Resumed => result.Groups!.Complete ? "resumed" : "resumed_partial",
        ClosureBeginOutcome.RefusedWindow => "blocked_window",
        ClosureBeginOutcome.RefusedHold => "blocked_hold",
        ClosureBeginOutcome.RefusedLastAdministrator => "blocked_last_administrator",
        ClosureBeginOutcome.NotFound => "not_found",
        ClosureBeginOutcome.NotClosure => "not_closure",
        ClosureBeginOutcome.NotBeginnable => "not_beginnable",
        ClosureBeginOutcome.SubjectNotAdmitted => "account_closed",
        ClosureBeginOutcome.RefusedTooLarge => "refused_too_large",
        ClosureBeginOutcome.RefusedProvider => "refused_provider",
        _ => throw new InvalidOperationException("A closure Begin outcome the route does not count."),
    };
}
