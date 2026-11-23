using LearnStack.Infrastructure.PlatformOperator;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Platform;

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

/// <summary>
/// The account-closure route of the operator surface (ADR-0107 §§ 7 and 8): the Begin of one person's global closure. It
/// runs the gate first, under <c>platform.data_request.write</c>.
/// </summary>
/// <remarks>
/// A Begin judges and moves the person and the request in one platform transaction and then fans the closure out: a
/// refusal answers <c>409</c> with the closed code it recorded on the request, a fan-out that did not reach every served
/// group answers <c>200</c> with <c>complete: false</c>, and a commit whose outcome is unknown answers <c>503</c> with
/// <c>errors.outcome</c>, after which a repeat is safe and resumes.
/// </remarks>
internal static partial class PlatformOperatorEndpoints
{
    private static void MapClosure(RouteGroupBuilder group) =>
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
                    + "not allow ordinary work or whose organization is not Active is unserved and is not fanned out. "
                    + "complete is false while a served group is waiting or failed: repeat Begin, which resumes, judges "
                    + "nothing again and adopts what started. Closing cannot be undone."),
            typeof(ClosureBeginResponse));

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
        _ => throw new InvalidOperationException("A closure Begin outcome the route does not count."),
    };
}
