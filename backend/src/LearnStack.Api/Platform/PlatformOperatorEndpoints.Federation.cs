using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using LearnStack.Infrastructure.PlatformOperator;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Platform;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Platform;

/// <summary>The optional ticket of a federation act; the body is otherwise empty.</summary>
/// <param name="Ticket">An optional ticket reference, 1 to 64 characters of <c>[A-Za-z0-9._:/#-]</c>.</param>
internal sealed record FederationActBody(
    [property: StringLength(64, MinimumLength = 1)]
    [property: RegularExpression(TicketPattern.Value)]
    string? Ticket);

/// <summary>One page of the federation report; <c>MappersUnread</c> counts healthy-looking brokers whose mappers were not read.</summary>
internal sealed record FederationReportResponse(
    IReadOnlyList<FederationBrokerReportItem> Items, InspectionPageInfo PageInfo, int Scanned, int Ignored, int MappersUnread);

/// <summary>What an applied or repeated federation act answers; the account review is the operator's to start.</summary>
internal sealed record FederationActResponse(
    string Outcome, string Alias, Guid? TenantId, Guid? ConnectionId, bool AccountReviewOwed, string CorrelationId);

/// <summary>
/// The federation routes of the operator surface (ADR-0099 Amendment 10): a report of the identity-provider brokers a
/// tenant's federation left orphaned, unreadable or nonconforming, a remove and a disable. Each runs the gate first. A
/// remove or a disable writes an intent row before the provider call and a completion row after it; a refusal that
/// precedes the intent writes no tenant row.
/// </summary>
internal static partial class PlatformOperatorEndpoints
{
    private const string FederationPrefix = "/federation/brokers";

    private static readonly Regex DerivedAliasPattern = new(
        @"\Alsf-[0-9a-f]{32}\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static readonly string[] FederationReportParameters = ["limit", "cursor", "ticket"];

    private static void MapFederation(RouteGroupBuilder group)
    {
        DescribeInspection(
            group.MapGet(FederationPrefix, ReportFederationAsync)
                .WithName("PlatformFederationReport")
                .WithSummary("Report identity-provider brokers that are orphaned, unreadable or nonconforming; the realm is read first, then the rows."),
            typeof(FederationReportResponse));
        Describe(
            group.MapPost(FederationPrefix + "/{alias}/remove", RemoveFederationAsync)
                .WithName("PlatformFederationRemove")
                .WithSummary("Remove an orphaned or unreadable owned broker at the provider; the provider deletes its federated links.")
                .WithDescription(
                    "Refused while a live connection row names a broker that reads; an unreadable owned broker is removed even "
                    + "with a live row, because nothing else can. An absent broker is already removed. The tenant's "
                    + "intent and completion rows are written around the provider call; the delegated-account review is the "
                    + "operator's, by runbook. A ticket is checked and recorded only on the platform scope's entry row, never "
                    + "on a tenant's row."),
            typeof(FederationActResponse));
        Describe(
            group.MapPost(FederationPrefix + "/{alias}/disable", DisableFederationAsync)
                .WithName("PlatformFederationDisable")
                .WithSummary("Disable a nonconforming or unreadable owned broker at the provider only; the tenant's row is left to its next readback.")
                .WithDescription(
                    "Sets enabled to false and nothing else, on an instance whose owner marker is proven. A healthy broker with "
                    + "a live row is refused. A ticket is checked and recorded only on the platform scope's entry row, never on "
                    + "a tenant's row."),
            typeof(FederationActResponse));
    }

    private static async Task<IResult> ReportFederationAsync(
        [FromQuery] string? limit, [FromQuery] string? cursor, [FromQuery] string? ticket, HttpContext http,
        IPlatformOperatorGate gate, IFederationBrokerOperations operations, ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var operation = PlatformOperations.FederationReport;
        var (refusal, admission) = await AdmitAsync(http, gate, operation, ticket, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        if (!TryLimit(limit, out var size))
        {
            return InvalidParameter(http, "limit");
        }

        if (http.Request.Query.Keys.Any(key => !FederationReportParameters.Contains(key, StringComparer.Ordinal)))
        {
            return InvalidParameter(http, "query");
        }

        try
        {
            var result = await operations.ReportAsync(admission!, cursor, size, cancellationToken);
            PlatformOperatorMetrics.Federation(operation, result.Outcome switch
            {
                FederationReportOutcome.Ok => "ok",
                FederationReportOutcome.InvalidCursor => "invalid_cursor",
                _ => "unavailable",
            });
            return result.Outcome switch
            {
                FederationReportOutcome.Ok => Results.Ok(new FederationReportResponse(
                    result.Items, new InspectionPageInfo(result.NextCursor, null, result.NextCursor is not null, false),
                    result.Scanned, result.Ignored, result.Unread)),
                FederationReportOutcome.InvalidCursor => InvalidParameter(http, "cursor"),
                _ => UnavailableProblem(http, loggers, operation, new OperatorRecoveryUnavailableException()),
            };
        }
        catch (AuditWriteFailedException failure)
        {
            return AuditUnavailableProblem(http, loggers, operation, failure);
        }
        catch (OperatorRecoveryUnavailableException failure)
        {
            // A database fault the owner translated: nothing was changed, and the cause is in the log.
            PlatformOperatorMetrics.Federation(operation, "unavailable");
            return UnavailableProblem(http, loggers, operation, failure);
        }
    }

    private static Task<IResult> RemoveFederationAsync(
        string alias, HttpContext http, IPlatformOperatorGate gate, IFederationBrokerOperations operations, ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        FederationActAsync(
            alias, PlatformOperations.FederationRemove, http, gate, loggers,
            (admission, cancel) => operations.RemoveAsync(admission, alias, cancel), cancellationToken);

    private static Task<IResult> DisableFederationAsync(
        string alias, HttpContext http, IPlatformOperatorGate gate, IFederationBrokerOperations operations, ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        FederationActAsync(
            alias, PlatformOperations.FederationDisable, http, gate, loggers,
            (admission, cancel) => operations.DisableAsync(admission, alias, cancel), cancellationToken);

    private static async Task<IResult> FederationActAsync(
        string alias,
        PlatformOperation operation,
        HttpContext http,
        IPlatformOperatorGate gate,
        ILoggerFactory loggers,
        Func<PlatformAdmission, CancellationToken, Task<FederationActResult>> act,
        CancellationToken cancellationToken)
    {
        var (body, refusal, admission) = await AdmitWithBodyAsync<FederationActBody>(
            http, gate, operation, request => request.Ticket, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        if (body is null || !DerivedAliasPattern.IsMatch(alias))
        {
            return Problem(http, StatusCodes.Status400BadRequest, "lockey_validation_failed");
        }

        try
        {
            var result = await act(admission!, cancellationToken);
            PlatformOperatorMetrics.Federation(operation, FederationOutcomeToken(result.Outcome));
            return result.Outcome switch
            {
                FederationActOutcome.Applied => Results.Ok(FederationResponse("applied", alias, result, admission!, http, true)),
                FederationActOutcome.AlreadyApplied => Results.Ok(FederationResponse("already_applied", alias, result, admission!, http, false)),
                FederationActOutcome.NotFound => Problem(http, StatusCodes.Status404NotFound, "lockey_not_found"),
                FederationActOutcome.NotOwned => ResultProblem(http, Rule("owner", "lockey_not_owned")),
                FederationActOutcome.LiveConnection => ResultProblem(http, Rule("connection", "lockey_live_connection")),
                FederationActOutcome.NotApplicable => ResultProblem(http, Rule("class", "lockey_not_applicable")),
                FederationActOutcome.Refused => ResultProblem(http, Rule("provider", "lockey_provider_refused")),
                FederationActOutcome.Unavailable => UnavailableProblem(http, loggers, operation, new OperatorRecoveryUnavailableException()),
                FederationActOutcome.Uncertain => UnavailableProblem(
                    http, loggers, operation, new OperatorRecoveryUnavailableException(commitOutcomeUnknown: true)),
                _ => throw new InvalidOperationException("A federation outcome the route does not answer."),
            };
        }
        catch (AuditWriteFailedException failure)
        {
            return AuditUnavailableProblem(http, loggers, operation, failure);
        }
        catch (OperatorRecoveryUnavailableException failure)
        {
            // A database fault the owner translated: nothing was changed, and the cause is in the log.
            PlatformOperatorMetrics.Federation(operation, "unavailable");
            return UnavailableProblem(http, loggers, operation, failure);
        }
    }

    private static FederationActResponse FederationResponse(
        string outcome, string alias, FederationActResult result, PlatformAdmission admission, HttpContext http, bool accountReviewOwed) =>
        new(outcome, alias, result.TenantId, result.ConnectionId, accountReviewOwed,
            admission.Operator.CorrelationId ?? http.TraceIdentifier);

    private static string FederationOutcomeToken(FederationActOutcome outcome) => outcome switch
    {
        FederationActOutcome.Applied => "applied",
        FederationActOutcome.AlreadyApplied => "already_applied",
        FederationActOutcome.NotFound => "not_found",
        FederationActOutcome.NotOwned => "not_owned",
        FederationActOutcome.LiveConnection => "live_connection",
        FederationActOutcome.NotApplicable => "not_applicable",
        FederationActOutcome.Refused => "refused",
        FederationActOutcome.Unavailable => "unavailable",
        FederationActOutcome.Uncertain => "uncertain",
        _ => throw new InvalidOperationException("A federation outcome the route does not count."),
    };
}
