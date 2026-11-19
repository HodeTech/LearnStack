using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Api.Platform;

/// <summary>The body of a killswitch toggle.</summary>
/// <param name="Enabled">The state to set; required.</param>
/// <param name="Ticket">An optional ticket reference, 1 to 64 characters of <c>[A-Za-z0-9._:/#-]</c>.</param>
internal sealed record KillswitchToggleRequest(bool? Enabled, string? Ticket);

/// <summary>What a toggle changed, with the correlation id the operator records.</summary>
internal sealed record KillswitchToggleResponse(string Key, bool Before, bool After, string? CorrelationId);

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
internal static class PlatformOperatorEndpoints
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
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return app;
    }

    private static async Task<IResult> ToggleKillswitchAsync(
        string key,
        KillswitchToggleRequest? body,
        HttpContext http,
        IPlatformOperatorGate gate,
        IKillswitchToggle toggle,
        CancellationToken cancellationToken)
    {
        var witness = http.Features.Get<PlatformOperatorFeature>()?.Witness;
        if (witness is null)
        {
            return Problem(http, StatusCodes.Status401Unauthorized, "lockey_unauthorized");
        }

        if (body?.Enabled is not { } enabled)
        {
            return Problem(http, StatusCodes.Status400BadRequest, "lockey_validation_failed");
        }

        var admission = await gate.AdmitAsync(witness, PlatformOperations.KillswitchToggle, body.Ticket, cancellationToken);
        if (!admission.IsAdmitted)
        {
            return admission.Refusal switch
            {
                PlatformRefusal.InvalidTicket => Problem(http, StatusCodes.Status400BadRequest, "lockey_validation_failed"),
                PlatformRefusal.InvalidWitness => Problem(http, StatusCodes.Status401Unauthorized, "lockey_unauthorized"),
                _ => Problem(http, StatusCodes.Status403Forbidden, "lockey_forbidden"),
            };
        }

        try
        {
            var result = await toggle.ToggleAsync(admission.Admission!, new KillswitchKey(key), enabled, cancellationToken);
            return result.Outcome == KillswitchToggleOutcome.Applied
                ? Results.Ok(new KillswitchToggleResponse(key, result.Before, result.After, admission.Admission!.Operator.CorrelationId))
                : Problem(http, StatusCodes.Status404NotFound, "lockey_not_found");
        }
        catch (AuditWriteFailedException)
        {
            return Problem(http, StatusCodes.Status503ServiceUnavailable, "lockey_audit_unavailable");
        }
    }

    private static IResult Problem(HttpContext http, int status, string key) =>
        Results.Json(
            ProblemDetailsFactory.For(new Error(new LocalizedMessage(key)), http),
            options: null,
            contentType: ProblemDetailsMediaType.Value,
            statusCode: status);
}
