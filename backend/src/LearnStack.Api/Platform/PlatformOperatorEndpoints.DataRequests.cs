using System.ComponentModel.DataAnnotations;
using System.Globalization;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Platform;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Platform;

/// <summary>The optional ticket of a data-request act; the body is otherwise empty.</summary>
/// <param name="Ticket">An optional ticket reference, 1 to 64 characters of <c>[A-Za-z0-9._:/#-]</c>.</param>
internal sealed record DataRequestActBody(
    [property: StringLength(64, MinimumLength = 1)]
    [property: RegularExpression(TicketPattern.Value)]
    string? Ticket);

/// <summary>
/// What an erasure completion answers: the request's recorded retained kinds and, for the call that completed it, the
/// redaction's closed counts. A repeat of a completion that had landed answers <c>already_completed</c> and has no
/// counts.
/// </summary>
/// <param name="Outcome"><c>completed</c> or <c>already_completed</c>.</param>
/// <param name="RequestId">The request.</param>
/// <param name="TenantId">The request's tenant, whose audit trail holds the result row.</param>
/// <param name="CompletedAt">When the request completed, by the database clock.</param>
/// <param name="RowsSelected">The audit rows the redaction selected; null on a repeat.</param>
/// <param name="Redacted">The rows changed per redacted column; null on a repeat.</param>
/// <param name="Retained">The request's retained kinds and counts, the audit log's included.</param>
/// <param name="CorrelationId">The correlation id the result row carries.</param>
internal sealed record ErasureCompletionResponse(
    string Outcome, Guid RequestId, Guid TenantId, DateTimeOffset? CompletedAt, long? RowsSelected,
    IReadOnlyDictionary<string, long>? Redacted, IReadOnlyDictionary<string, long> Retained, string CorrelationId);

/// <summary>
/// The data-request routes of the operator surface (ADR-0107 § 8, ADR-0108): a metadata-only inspection of the requests
/// and artifacts that need a person, and the completion of an erasure's privileged step. Each runs the gate first.
/// </summary>
/// <remarks>
/// A completion redacts, completes and records in one transaction: a refused state answers <c>409</c> and writes
/// nothing but the scope's entry row, a MUST row or a redaction that cannot finish answers <c>503</c> with nothing
/// changed, and a commit whose outcome is unknown answers <c>503</c> with <c>errors.outcome</c>, after which a repeat
/// is safe and answers <c>already_completed</c> if it had landed.
/// </remarks>
internal static partial class PlatformOperatorEndpoints
{
    private const string DataRequestPrefix = "/data-requests";
    private static readonly string[] DataRequestInspectionParameters = ["state", "limit", "cursor", "ticket"];

    /// <summary>
    /// The closed list names of the inspection, in the order the document offers them; the first is the default.
    /// </summary>
    internal static readonly IReadOnlyList<(string Name, DataRequestInspectionState State)> DataRequestStates =
    [
        ("awaiting_privileged_step", DataRequestInspectionState.AwaitingPrivilegedStep),
        ("overdue_scheduled", DataRequestInspectionState.OverdueScheduled),
        ("stuck_fulfilling", DataRequestInspectionState.StuckFulfilling),
        ("expired_artifact", DataRequestInspectionState.ExpiredArtifact),
    ];

    private static void MapDataRequests(RouteGroupBuilder group)
    {
        DescribeInspection(
            group.MapGet(DataRequestPrefix, InspectDataRequestsAsync)
                .WithName("PlatformDataRequestInspect")
                .WithSummary("List the data requests and export artifacts that need an operator, metadata only.")
                .WithDescription(
                    "state is awaiting_privileged_step (erasures waiting for their audit redaction), overdue_scheduled "
                    + "(requests whose window ended and that nothing started), stuck_fulfilling (requests still fulfilling "
                    + string.Format(CultureInfo.InvariantCulture, "{0:0} hours", DataRequestLimits.StuckFulfillingAfter.TotalHours)
                    + " after they fell due) or expired_artifact (export artifacts past their expiry whose purge has not "
                    + "run). Oldest due first. Each item names the request, its kind, origin, status, tenant, organization, "
                    + "times and recorded refusal or failure code, and on expired_artifact the artifact; never the subject or "
                    + "any person data. A ticket is checked and recorded only on the platform scope's entry row."),
            typeof(InspectionResponse<DataRequestInspectionItem>));
        Describe(
            group.MapPost(DataRequestPrefix + "/{requestId:guid}/complete-erasure", CompleteErasureAsync)
                .WithName("PlatformErasureComplete")
                .WithSummary("Complete an erasure awaiting its privileged step: redact the subject's audit rows and complete the request.")
                .WithDescription(
                    "One transaction redacts ip_address, user_agent, actor_email and the subject's references in the audit "
                    + "states, moves the request from awaiting_privileged_step to completed and writes the result row in the "
                    + "request's tenant. actor_user_id, entity_id, metadata and the causation and correlation identifiers "
                    + "survive and are reported as retained kinds. A repeat after the act landed answers already_completed "
                    + "and writes no second row. A ticket is checked and recorded only on the platform scope's entry row, "
                    + "never on the tenant's row."),
            typeof(ErasureCompletionResponse));
    }

    private static async Task<IResult> InspectDataRequestsAsync(
        [FromQuery] string? state, [FromQuery] string? limit, [FromQuery] string? cursor, [FromQuery] string? ticket,
        HttpContext http, IPlatformOperatorGate gate, IDataRequestOperatorOperations operations, ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var operation = PlatformOperations.DataRequestInspect;
        var (refusal, admission) = await AdmitAsync(http, gate, operation, ticket, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        if (!TryDataRequestState(state, out var parsedState))
        {
            return InvalidParameter(http, "state");
        }

        if (!TryLimit(limit, out var size))
        {
            return InvalidParameter(http, "limit");
        }

        // A misspelt parameter would otherwise be ignored and answer a different list; its name is the caller's text.
        if (http.Request.Query.Keys.Any(key => !DataRequestInspectionParameters.Contains(key, StringComparer.Ordinal)))
        {
            return InvalidParameter(http, "query");
        }

        try
        {
            var page = await operations.InspectAsync(
                admission!, new DataRequestInspectionQuery(parsedState, cursor, size), cancellationToken);
            return page is null
                ? InvalidParameter(http, "cursor")
                : Results.Ok(new InspectionResponse<DataRequestInspectionItem>(
                    page.Items, new InspectionPageInfo(page.NextCursor, null, page.NextCursor is not null, false)));
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

    private static async Task<IResult> CompleteErasureAsync(
        Guid requestId, HttpContext http, IPlatformOperatorGate gate, IDataRequestOperatorOperations operations,
        ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var operation = PlatformOperations.ErasureComplete;
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
            var result = await operations.CompleteErasureAsync(admission!, requestId, cancellationToken);
            return result.Outcome switch
            {
                ErasureCompletionOutcome.Completed or ErasureCompletionOutcome.AlreadyCompleted => Results.Ok(
                    new ErasureCompletionResponse(
                        result.Outcome == ErasureCompletionOutcome.Completed ? "completed" : "already_completed",
                        requestId, result.TenantId!.Value, result.CompletedAt, result.Report?.RowsSelected,
                        result.Report?.Redacted.ToDictionary(
                            entry => AuditRedactionCodes.Columns.ToCode(entry.Key), entry => entry.Value, StringComparer.Ordinal),
                        result.Retained ?? new Dictionary<string, long>(StringComparer.Ordinal),
                        admission!.Operator.CorrelationId ?? http.TraceIdentifier)),
                ErasureCompletionOutcome.NotFound => Problem(http, StatusCodes.Status404NotFound, "lockey_not_found"),
                ErasureCompletionOutcome.NotErasure => ResultProblem(http, Rule("kind", "lockey_data_request_not_erasure")),
                ErasureCompletionOutcome.NotAwaiting => ResultProblem(
                    http, Rule("status", "lockey_data_request_not_awaiting_privileged_step", result.Status)),
                _ => throw new InvalidOperationException("An erasure completion outcome the route does not answer."),
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

    private static bool TryDataRequestState(string? text, out DataRequestInspectionState state)
    {
        state = DataRequestStates[0].State;
        if (text is null)
        {
            return true;
        }

        foreach (var (name, value) in DataRequestStates)
        {
            if (string.Equals(name, text, StringComparison.Ordinal))
            {
                state = value;
                return true;
            }
        }

        return false;
    }
}
