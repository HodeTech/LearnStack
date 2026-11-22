using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Platform;

/// <summary>The fence a subscriber replay presents, as the inspection showed it.</summary>
/// <param name="ReplayCount">The delivery's replay count; required.</param>
/// <param name="DeadLetteredAt">The instant it was dead-lettered, to the microsecond, as the inspection returned it; required.</param>
/// <param name="TerminalReason">The terminal reason the inspection showed; required.</param>
/// <param name="Ticket">An optional ticket reference, 1 to 64 characters of <c>[A-Za-z0-9._:/#-]</c>.</param>
internal sealed record DeliveryReplayBody(
    [property: Required] int? ReplayCount,
    [property: Required] DateTimeOffset? DeadLetteredAt,
    [property: Required][property: StringLength(32, MinimumLength = 1)] string? TerminalReason,
    [property: StringLength(64, MinimumLength = 1)]
    [property: RegularExpression(TicketPattern.Value)]
    string? Ticket);

/// <summary>The fence a producer reset presents, as the inspection showed it.</summary>
/// <param name="Attempts">The row's attempt count; required.</param>
/// <param name="DeadLetteredAt">The instant it was dead-lettered, to the microsecond, as the inspection returned it; required.</param>
/// <param name="Ticket">An optional ticket reference, 1 to 64 characters of <c>[A-Za-z0-9._:/#-]</c>.</param>
internal sealed record OutboxResetBody(
    [property: Required] int? Attempts,
    [property: Required] DateTimeOffset? DeadLetteredAt,
    [property: StringLength(64, MinimumLength = 1)]
    [property: RegularExpression(TicketPattern.Value)]
    string? Ticket);

/// <summary>What an applied recovery answers: the row it reset, the count it now carries and the correlation id.</summary>
internal sealed record RecoveryResponse(
    string Outcome, Guid TenantId, Guid EventId, string? SubscriptionId, int CountAfter, string CorrelationId);

/// <summary>The cursor envelope of Standard 04, for an inspection page.</summary>
internal sealed record InspectionPageInfo(string? NextCursor, string? PreviousCursor, bool HasNext, bool HasPrevious);

/// <summary>One page of an inspection.</summary>
internal sealed record InspectionResponse<T>(IReadOnlyList<T> Items, InspectionPageInfo PageInfo);

/// <summary>
/// The recovery routes of the operator surface (ADR-0103): a metadata-only inspection of dead-lettered work and
/// the two fenced transitions that act on what it shows. There is no dashboard and no job recovery.
/// </summary>
/// <remarks>
/// Each route runs the gate first. The acts are <c>POST</c>s that carry the fence read at inspection: a stale fence
/// answers <c>409</c> with the reason on the field <c>fence</c>, a state a reset may not act on answers <c>409</c> with
/// the reason on the field <c>state</c>, a MUST row that cannot be written answers <c>503</c> with nothing changed, and
/// a commit whose outcome is unknown answers <c>503</c> so the operator inspects before repeating.
/// </remarks>
internal static partial class PlatformOperatorEndpoints
{
    private const string WorkPrefix = "/asynchronous-work";
    private static readonly string[] InspectionParameters = ["state", "limit", "cursor", "ticket"];
    private const int DefaultPageSize = CursorPagination.DefaultLimit;
    private const int MaximumPageSize = CursorPagination.MaxLimit;

    private static readonly Regex SubscriptionPattern = new(
        @"\A[a-z][a-z0-9]*\.[a-z][a-z0-9-]*\.v[1-9][0-9]*\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static void MapRecovery(RouteGroupBuilder group)
    {
        DescribeInspection(
            group.MapGet(WorkPrefix + "/outbox-messages", InspectOutboxAsync)
                .WithName("PlatformOutboxInspect")
                .WithSummary("List dead-lettered or terminal-requested outbox rows, metadata only, with the fence a reset needs."),
            typeof(InspectionResponse<OutboxMessageInspection>));
        DescribeInspection(
            group.MapGet(WorkPrefix + "/event-deliveries", InspectDeliveriesAsync)
                .WithName("PlatformDeliveryInspect")
                .WithSummary("List dead-lettered or terminal-requested subscriber deliveries, metadata only, with the fence a replay needs."),
            typeof(InspectionResponse<EventDeliveryInspection>));
        DescribeInspection(
            group.MapGet(WorkPrefix + "/quarantined-jobs", InspectJobsAsync)
                .WithName("PlatformJobInspect")
                .WithSummary("List quarantined jobs with their reservation occupancy; nothing here releases or requeues one."),
            typeof(InspectionResponse<QuarantinedJobInspection>));
        Describe(
            group.MapPost(WorkPrefix + "/event-deliveries/{tenantId:guid}/{subscriptionId}/{eventId:guid}/replay", ReplayDeliveryAsync)
                .WithName("PlatformDeliveryReplay")
                .WithSummary("Reset one dead-lettered subscriber delivery under the fence the inspection returned.")
                .WithDescription(
                    "Send every fence field exactly as the inspection returned it: replayCount, deadLetteredAt (to the microsecond; a "
                    + "value rounded to milliseconds never matches) and terminalReason. A ticket is checked and not stored."),
            typeof(RecoveryResponse));
        Describe(
            group.MapPost(WorkPrefix + "/outbox-messages/{tenantId:guid}/{eventId:guid}/reset", ResetOutboxAsync)
                .WithName("PlatformOutboxReset")
                .WithSummary("Reset one retry-exhausted outbox row under the fence the inspection returned; this renews its attempt budget.")
                .WithDescription(
                    "Send every fence field exactly as the inspection returned it: attempts and deadLetteredAt (to the microsecond; a "
                    + "value rounded to milliseconds never matches). A ticket is checked and not stored."),
            typeof(RecoveryResponse));
    }

    private static void DescribeInspection(RouteHandlerBuilder route, Type success) => route
        .Produces(StatusCodes.Status200OK, success, "application/json")
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status500InternalServerError)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static Task<IResult> InspectOutboxAsync(
        [FromQuery] string? state, [FromQuery] string? limit, [FromQuery] string? cursor, [FromQuery] string? ticket,
        HttpContext http, IPlatformOperatorGate gate, IDeliveryInspection inspection, ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        InspectAsync(
            http, gate, ticket, state, limit, loggers, true,
            async (admission, parsedState, size) => await inspection.InspectOutboxAsync(admission, parsedState, cursor, size, cancellationToken),
            cancellationToken);

    private static Task<IResult> InspectDeliveriesAsync(
        [FromQuery] string? state, [FromQuery] string? limit, [FromQuery] string? cursor, [FromQuery] string? ticket,
        HttpContext http, IPlatformOperatorGate gate, IDeliveryInspection inspection, ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        InspectAsync(
            http, gate, ticket, state, limit, loggers, true,
            async (admission, parsedState, size) => await inspection.InspectDeliveriesAsync(admission, parsedState, cursor, size, cancellationToken),
            cancellationToken);

    private static Task<IResult> InspectJobsAsync(
        [FromQuery] string? state, [FromQuery] string? limit, [FromQuery] string? cursor, [FromQuery] string? ticket,
        HttpContext http, IPlatformOperatorGate gate, IDeliveryInspection inspection, ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        InspectAsync(
            http, gate, ticket, state, limit, loggers, false,
            async (admission, _, size) => await inspection.InspectJobsAsync(admission, cursor, size, cancellationToken),
            cancellationToken);

    /// <summary>
    /// The shared flow of an inspection: the gate first, so the query is answered only to an operator who may inspect,
    /// then the page parameters, then one page in an entered platform scope.
    /// </summary>
    private static async Task<IResult> InspectAsync<T>(
        HttpContext http,
        IPlatformOperatorGate gate,
        string? ticket,
        string? state,
        string? limit,
        ILoggerFactory loggers,
        bool acceptsState,
        Func<PlatformAdmission, DeliveryInspectionState, int, Task<InspectionPage<T>?>> page,
        CancellationToken cancellationToken)
    {
        var (refusal, admission) = await AdmitAsync(http, gate, PlatformOperations.DeliveryInspect, ticket, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var parsedState = DeliveryInspectionState.DeadLettered;
        if (acceptsState ? !TryState(state, out parsedState) : state is not null)
        {
            return InvalidParameter(http, "state");
        }

        if (!TryLimit(limit, out var size))
        {
            return InvalidParameter(http, "limit");
        }

        // A misspelt parameter would otherwise be ignored and answer a different list than the one asked for. The
        // name is not echoed: it is the caller's text.
        if (http.Request.Query.Keys.Any(key => !InspectionParameters.Contains(key, StringComparer.Ordinal)))
        {
            return InvalidParameter(http, "query");
        }

        try
        {
            var result = await page(admission!, parsedState, size);
            return result is null
                ? InvalidParameter(http, "cursor")
                : Results.Ok(new InspectionResponse<T>(
                    result.Items, new InspectionPageInfo(result.NextCursor, null, result.NextCursor is not null, false)));
        }
        catch (AuditWriteFailedException failure)
        {
            return AuditUnavailableProblem(http, loggers, PlatformOperations.DeliveryInspect, failure);
        }
        catch (OperatorRecoveryUnavailableException failure)
        {
            return UnavailableProblem(http, loggers, PlatformOperations.DeliveryInspect, failure);
        }
    }

    private static async Task<IResult> ReplayDeliveryAsync(
        Guid tenantId, string subscriptionId, Guid eventId, HttpContext http, IPlatformOperatorGate gate,
        IOperatorRecovery recovery, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var (body, refusal, admission) = await AdmitWithBodyAsync<DeliveryReplayBody>(
            http, gate, PlatformOperations.DeliveryReplay, request => request.Ticket, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        if (body is null || !IsOperatorRowId(tenantId) || eventId == Guid.Empty || !SubscriptionPattern.IsMatch(subscriptionId)
            || body.ReplayCount is not { } count || body.DeadLetteredAt is not { } deadLettered || body.TerminalReason is not { } reason)
        {
            return Problem(http, StatusCodes.Status400BadRequest, "lockey_validation_failed");
        }

        return await RecoverAsync(
            http, loggers, PlatformOperations.DeliveryReplay, admission!, tenantId, eventId, subscriptionId,
            () => recovery.ReplayDeliveryAsync(
                admission!, new DeliveryReplayRequest(tenantId, subscriptionId, eventId, count, deadLettered, reason), cancellationToken));
    }

    private static async Task<IResult> ResetOutboxAsync(
        Guid tenantId, Guid eventId, HttpContext http, IPlatformOperatorGate gate, IOperatorRecovery recovery,
        ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var (body, refusal, admission) = await AdmitWithBodyAsync<OutboxResetBody>(
            http, gate, PlatformOperations.DeliveryReset, request => request.Ticket, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        if (body is null || !IsOperatorRowId(tenantId) || eventId == Guid.Empty
            || body.Attempts is not { } attempts || body.DeadLetteredAt is not { } deadLettered)
        {
            return Problem(http, StatusCodes.Status400BadRequest, "lockey_validation_failed");
        }

        return await RecoverAsync(
            http, loggers, PlatformOperations.DeliveryReset, admission!, tenantId, eventId, null,
            () => recovery.ResetOutboxAsync(
                admission!, new OutboxResetRequest(tenantId, eventId, attempts, deadLettered), cancellationToken));
    }

    private static async Task<IResult> RecoverAsync(
        HttpContext http,
        ILoggerFactory loggers,
        PlatformOperation operation,
        PlatformAdmission admission,
        Guid tenantId,
        Guid eventId,
        string? subscriptionId,
        Func<Task<OperatorRecoveryResult>> transition)
    {
        try
        {
            var result = await transition();
            return result.Outcome switch
            {
                OperatorRecoveryOutcome.Applied => Results.Ok(new RecoveryResponse(
                    "applied", tenantId, eventId, subscriptionId, result.CountAfter ?? 0,
                    admission.Operator.CorrelationId ?? http.TraceIdentifier)),
                OperatorRecoveryOutcome.StaleFence => ResultProblem(http, Rule("fence", "lockey_stale_fence")),
                OperatorRecoveryOutcome.NotRecoverable => ResultProblem(http, Rule("state", "lockey_not_recoverable", result.Refusal)),
                _ => Problem(http, StatusCodes.Status404NotFound, "lockey_not_found"),
            };
        }
        catch (AuditWriteFailedException failure)
        {
            return AuditUnavailableProblem(http, loggers, operation, failure);
        }
        catch (OperatorRecoveryUnavailableException failure)
        {
            return UnavailableProblem(http, loggers, operation, failure);
        }
    }

    private static readonly Action<ILogger, string, bool, Exception?> LogRecoveryUnavailable =
        LoggerMessage.Define<string, bool>(
            LogLevel.Warning,
            new EventId(7110, nameof(LogRecoveryUnavailable)),
            "An operator owner (recovery, inspection, federation or data request) was unavailable for {Operation}; the outcome unknown: {CommitOutcomeUnknown}.");

    /// <summary>
    /// A 503 whose cause the operator can find: the fault is logged, and the response names only whether the commit's
    /// outcome is unknown. The status map is closed, so the reason travels on the field <c>outcome</c> of
    /// <c>dependency_unavailable</c> and the body's status agrees with the response's.
    /// </summary>
    private static IResult UnavailableProblem(
        HttpContext http, ILoggerFactory loggers, PlatformOperation operation, OperatorRecoveryUnavailableException failure)
    {
        LogRecoveryUnavailable(
            loggers.CreateLogger(typeof(PlatformOperatorEndpoints).FullName!), operation.AuditSlug, failure.CommitOutcomeUnknown,
            failure.InnerException);
        return ResultProblem(
            http,
            failure.CommitOutcomeUnknown
                ? new Error(
                    new LocalizedMessage("lockey_dependency_unavailable"),
                    new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
                    {
                        ["outcome"] = [new LocalizedMessage("lockey_outcome_unknown")],
                    })
                : new Error(new LocalizedMessage("lockey_dependency_unavailable")));
    }

    /// <summary>A 400 that names the query parameter at fault, as Standard 04 asks of a page parameter.</summary>
    private static IResult InvalidParameter(HttpContext http, string parameter) => ResultProblem(
        http,
        new Error(
            new LocalizedMessage("lockey_validation_failed"),
            new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
            {
                [parameter] = [new LocalizedMessage(ModelBindingProblemDetails.FieldMessageKey)],
            }));

    /// <summary>
    /// A 409 with the reason on the field it concerns; the status map is closed, so the code stays the business rule's.
    /// A refused state names the rule that refused it as the reason's <c>cause</c> parameter.
    /// </summary>
    private static Error Rule(string field, string reason, string? cause = null) => new(
        new LocalizedMessage("lockey_business_rule_violation"),
        new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
        {
            [field] = [new LocalizedMessage(
                reason, cause is null ? null : new Dictionary<string, string>(StringComparer.Ordinal) { ["cause"] = cause })],
        });

    private static async Task<(IResult? Refusal, PlatformAdmission? Admission)> AdmitAsync(
        HttpContext http, IPlatformOperatorGate gate, PlatformOperation operation, string? ticket, CancellationToken cancellationToken)
    {
        var witness = http.Features.Get<PlatformOperatorFeature>()?.Witness;
        if (witness is null)
        {
            return (Problem(http, StatusCodes.Status401Unauthorized, "lockey_unauthorized"), null);
        }

        var admission = await gate.AdmitAsync(witness, operation, ticket, cancellationToken);
        return admission.IsAdmitted ? (null, admission.Admission) : (RefusalProblem(http, admission.Refusal), null);
    }

    /// <summary>The body is read to find the ticket, and answered only after the gate has admitted the act.</summary>
    private static async Task<(TBody? Body, IResult? Refusal, PlatformAdmission? Admission)> AdmitWithBodyAsync<TBody>(
        HttpContext http, IPlatformOperatorGate gate, PlatformOperation operation, Func<TBody, string?> ticketOf,
        CancellationToken cancellationToken)
        where TBody : class
    {
        var witness = http.Features.Get<PlatformOperatorFeature>()?.Witness;
        if (witness is null)
        {
            return (null, Problem(http, StatusCodes.Status401Unauthorized, "lockey_unauthorized"), null);
        }

        var (body, bodyState) = await ReadBodyAsync<TBody>(http.Request, cancellationToken);
        var admission = await gate.AdmitAsync(witness, operation, body is null ? null : ticketOf(body), cancellationToken);
        if (!admission.IsAdmitted)
        {
            return (null, RefusalProblem(http, admission.Refusal), null);
        }

        return bodyState == BodyState.NotJson
            ? (null, Problem(http, StatusCodes.Status415UnsupportedMediaType, "lockey_unsupported_media_type"), null)
            : (body, null, admission.Admission);
    }

    private static bool TryState(string? text, out DeliveryInspectionState state)
    {
        state = DeliveryInspectionState.DeadLettered;
        switch (text)
        {
            case null or "dead_lettered":
                return true;
            case "terminal_requested":
                state = DeliveryInspectionState.TerminalRequested;
                return true;
            default:
                return false;
        }
    }

    private static bool TryLimit(string? text, out int size)
    {
        size = DefaultPageSize;
        if (text is null)
        {
            return true;
        }

        // Standard 04: a limit of zero or less is rejected, a larger one is clamped, never rejected. The digits are read
        // without a width, so a value past the range of an int is a larger limit and is clamped like any other.
        if (!System.Numerics.BigInteger.TryParse(
                text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || parsed < 1)
        {
            return false;
        }

        size = parsed > MaximumPageSize ? MaximumPageSize : (int)parsed;
        return true;
    }

    /// <summary>A tenant the row names: never the nil value and never the platform sentinel, which no event row can carry.</summary>
    private static bool IsOperatorRowId(Guid tenant) => tenant != Guid.Empty && tenant != TenantId.PlatformSentinel.Value;
}
