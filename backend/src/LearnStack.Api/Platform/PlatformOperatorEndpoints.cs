using LearnStack.Api.Authentication;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using LearnStack.Api.Common;
using LearnStack.Infrastructure.PlatformOperator;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Api.Platform;

/// <summary>The body of a killswitch toggle.</summary>
/// <param name="Enabled">The state to set; required.</param>
/// <param name="Ticket">An optional ticket reference, 1 to 64 characters of <c>[A-Za-z0-9._:/#-]</c>.</param>
internal sealed record KillswitchToggleRequest(
    [property: Required] bool? Enabled,
    [property: StringLength(64, MinimumLength = 1)]
    [property: RegularExpression(@"^[A-Za-z0-9._:/#-]{1,64}$")]
    string? Ticket);

/// <summary>What a toggle changed, with the correlation id the operator records.</summary>
internal sealed record KillswitchToggleResponse(string Key, bool Before, bool After, string CorrelationId);

/// <summary>
/// The platform operator surface: minimal endpoints under <c>/api/platform/v1</c>, present only
/// when an operator issuer is configured (ADR-0101 § 4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Absence is the default.</b> With no issuer there is no operator scheme and nothing here
/// is mapped, so the prefix answers 404 like any unknown route.
/// </para>
/// <para>
/// <b>Admission before work.</b> Every handler asks <see cref="IPlatformOperatorGate"/> before
/// it touches a connection. A malformed ticket is a 400; a missing permission or an unknown
/// operation is a 403; and neither opens a privileged connection or writes a row, only a
/// counter and a log (ADR-0101 § 11).
/// </para>
/// </remarks>
internal static partial class PlatformOperatorEndpoints
{
    internal const string Prefix = "/api/platform/v1";

    internal static WebApplication MapLearnStackPlatformOperator(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Services.GetService<PlatformOperatorSettings>() is null)
        {
            return app;
        }

        var group = app.MapGroup(Prefix)
            .RequireAuthorization(PlatformOperatorAuthentication.PolicyName)
            .WithMetadata(InfrastructureOperationMetadata.PlatformOperator);

        group.MapPut("/killswitches/{key}", ToggleKillswitchAsync)
            .WithName("PlatformKillswitchToggle")
            .WithSummary("Set a declared killswitch for every tenant.")
            .Produces<KillswitchToggleResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        MapOnboarding(group);
        return app;
    }

    // The body is read by the handler and not bound by the framework, so that a body that cannot be
    // read is answered only after admission: binding runs before any handler code, and an ungranted
    // operator would otherwise learn from a 400 or 415 that their request was inspected.
    private enum BodyState { Read, Missing, NotJson, Malformed }

    private const int MaximumBodyBytes = 4096;

    private static async Task<(TBody? Body, BodyState State)> ReadBodyAsync<TBody>(
        HttpRequest request, CancellationToken cancellationToken)
        where TBody : class
    {
        // HTTP/1.x with neither a length nor a transfer encoding has no body. HTTP/2 and HTTP/3 carry bodies
        // without either header, so for them only a declared zero length is an absent body.
        var undeclared = request.ContentLength is null && !request.Headers.ContainsKey("Transfer-Encoding")
            && (HttpProtocol.IsHttp10(request.Protocol) || HttpProtocol.IsHttp11(request.Protocol));
        if (request.ContentLength is 0 || undeclared)
        {
            return (null, BodyState.Missing);
        }

        if (!request.HasJsonContentType())
        {
            return (null, BodyState.NotJson);
        }

        var limit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = MaximumBodyBytes;
        }

        try
        {
            var body = await JsonSerializer.DeserializeAsync<TBody>(
                request.Body, JsonSerializerOptions.Web, cancellationToken);
            return body is null ? (null, BodyState.Malformed) : (body, BodyState.Read);
        }
        catch (JsonException)
        {
            return (null, BodyState.Malformed);
        }
    }

    private static async Task<IResult> ToggleKillswitchAsync(
        string key,
        HttpContext http,
        IPlatformOperatorGate gate,
        IKillswitchToggle toggle,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var witness = http.Features.Get<PlatformOperatorFeature>()?.Witness;
        if (witness is null)
        {
            return Problem(http, StatusCodes.Status401Unauthorized, "lockey_unauthorized");
        }

        // Admission first: the gate checks the grant before the ticket, so an operator without
        // the grant is answered 403 whatever the request carries, and a body that cannot be used is
        // answered 400 or 415 only to someone who may act at all.
        var (body, bodyState) = await ReadBodyAsync<KillswitchToggleRequest>(http.Request, cancellationToken);
        var admission = await gate.AdmitAsync(witness, PlatformOperations.KillswitchToggle, body?.Ticket, cancellationToken);
        if (!admission.IsAdmitted)
        {
            return admission.Refusal switch
            {
                PlatformRefusal.InvalidTicket => Problem(http, StatusCodes.Status400BadRequest, "lockey_validation_failed"),
                PlatformRefusal.InvalidWitness => Problem(http, StatusCodes.Status401Unauthorized, "lockey_unauthorized"),
                _ => Problem(http, StatusCodes.Status403Forbidden, "lockey_forbidden"),
            };
        }

        if (bodyState == BodyState.NotJson)
        {
            return Problem(http, StatusCodes.Status415UnsupportedMediaType, "lockey_unsupported_media_type");
        }

        if (body?.Enabled is not { } enabled)
        {
            return Problem(http, StatusCodes.Status400BadRequest, "lockey_validation_failed");
        }

        // The registry resolves the name; a switch it does not declare is answered as it always was,
        // without a scope entered or a row written.
        if (!KillswitchKeys.TryResolve(key, out var registryKey))
        {
            return Problem(http, StatusCodes.Status404NotFound, "lockey_not_found");
        }

        try
        {
            var result = await toggle.ToggleAsync(admission.Admission!, registryKey, enabled, cancellationToken);
            return result.Outcome == KillswitchToggleOutcome.Applied
                ? Results.Ok(new KillswitchToggleResponse(
                    key, result.Before, result.After, admission.Admission!.Operator.CorrelationId ?? http.TraceIdentifier))
                : Problem(http, StatusCodes.Status404NotFound, "lockey_not_found");
        }
        catch (AuditWriteFailedException failure)
        {
            // A MUST row that cannot be written refuses the act, and the refusal is a signal:
            // an error line and a counter an operator can alert on, and a Retry-After like
            // every other 503 the API answers.
            PlatformOperatorMetrics.AuditFailed(PlatformOperations.KillswitchToggle);
            LogAuditRefused(loggerFactory.CreateLogger(typeof(PlatformOperatorEndpoints).FullName!), failure);
            RetryAfter.Apply(http.Response, StatusCodes.Status503ServiceUnavailable);
            return Problem(http, StatusCodes.Status503ServiceUnavailable, "lockey_audit_unavailable");
        }
    }

    private static readonly Action<ILogger, Exception?> LogAuditRefused =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(7105, nameof(LogAuditRefused)),
            "A platform operator act was refused because its audit row could not be written.");

    private static IResult Problem(HttpContext http, int status, string key) =>
        Results.Json(
            ProblemDetailsFactory.For(new Error(new LocalizedMessage(key)), http),
            options: null,
            contentType: ProblemDetailsMediaType.Value,
            statusCode: status);
}
