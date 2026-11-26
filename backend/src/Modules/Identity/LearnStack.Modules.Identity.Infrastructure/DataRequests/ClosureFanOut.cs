using LearnStack.Infrastructure.PlatformOperator;
using LearnStack.Modules.Identity.Application.Contracts.OperatorActs;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Platform;
using Microsoft.Extensions.Logging;

namespace LearnStack.Modules.Identity.Infrastructure.DataRequests;

/// <summary>
/// The closure's fan-out (<c>fan_out_closure</c>, ADR-0107 § 7): one pipeline act per participant group, each admitted by
/// the gate on its own and run by the operator act executor in the group's own tenant and organization.
/// </summary>
/// <remarks>
/// <para>
/// <b>After Begin, never inside it.</b> Begin's owner calls this once its transaction has committed and released the
/// fences, so each group's act takes the person's User fence and its tenant's fence on its own application connection.
/// Nothing here opens a connection, reads the platform scope or writes a row: the executor runs
/// <see cref="FanOutClosureCommand"/> through the ordinary pipeline, which writes the group tenant's MUST row.
/// </para>
/// <para>
/// <b>One admission per act.</b> The gate is asked for <see cref="PlatformOperations.ClosureFanOut"/> with the operator's
/// witness and ticket before every group, so the grant is evaluated per act: an operator whose grant is withdrawn between
/// groups is refused from that group on. The ticket never reaches a tenant row.
/// </para>
/// <para>
/// <b>Every group is answered.</b> Groups run one after another in a deterministic order, at most
/// <see cref="MaximumGroups"/>. A refused admission, a refusal result and an exception are each that group's
/// <see cref="ClosureGroupOutcome.Failed"/>, which a repeated Begin retries; only the operator's cancellation stops the
/// loop, by throwing. The call is idempotent, because each act adopts what an earlier one started.
/// </para>
/// <para>
/// <b>Signals.</b> One log line per group carries the outcome and, for a failure, the exception type or the refusal's
/// closed code; never the person, the group or a ticket. The data-request counter is labelled with the operation and the
/// group's outcome.
/// </para>
/// </remarks>
public sealed partial class ClosureFanOut(
    IPlatformOperatorGate gate,
    PlatformOperatorActExecutor executor,
    ILogger<ClosureFanOut> logger) : IClosureFanOut
{
    /// <summary>
    /// The most participant groups one call fans out to: the closure's group bound, which Begin judges before the person
    /// leaves Active, so a longer list is a programming error of the caller (<see cref="IClosureFanOut"/>).
    /// </summary>
    public const int MaximumGroups = ClosureLimits.MaximumParticipantGroups;

    public async Task<IReadOnlyList<ClosureGroupReport>> FanOutAsync(
        PlatformOperatorWitness operatorWitness,
        string? ticket,
        ClosureFanOutRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operatorWitness);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Groups);
        var subject = request.SubjectUserId;
        if (!StronglyTypedId.IsAssigned(subject) || subject.Value.Version != 7 || subject == UserId.SystemActor)
        {
            throw new ArgumentException("A closure's fan-out names a real person.", nameof(request));
        }

        if (request.Groups.Count > MaximumGroups || request.Groups.Any(group => group is null
                || !StronglyTypedId.IsAssigned(group.TenantId) || group.TenantId == TenantId.PlatformSentinel
                || group.OrganizationId is { } organization && !StronglyTypedId.IsAssigned(organization))
            || request.Groups.Distinct().Count() != request.Groups.Count)
        {
            throw new ArgumentException(
                "A closure's fan-out names at most " + MaximumGroups.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " distinct participant groups.", nameof(request));
        }

        // Tenant, then the tenant-wide group before its organizations, then each organization: the same order whatever
        // order the caller listed them in.
        var groups = request.Groups.OrderBy(group => group.TenantId.Value)
            .ThenBy(group => group.OrganizationId is null ? 0 : 1)
            .ThenBy(group => group.OrganizationId?.Value ?? Guid.Empty)
            .ToArray();
        var reports = new List<ClosureGroupReport>(groups.Length);
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await RunAsync(operatorWitness, ticket, subject, group, cancellationToken).ConfigureAwait(false);
            PlatformOperatorMetrics.DataRequest(PlatformOperations.ClosureFanOut, Token(outcome));
            reports.Add(new ClosureGroupReport(group, outcome));
        }

        return reports;
    }

    /// <summary>One group's act: admitted, executed, and mapped to the group's closed outcome.</summary>
    private async Task<ClosureGroupOutcome> RunAsync(PlatformOperatorWitness operatorWitness, string? ticket, UserId subject,
        ClosureParticipantGroup group, CancellationToken cancellationToken)
    {
        try
        {
            var admitted = await gate.AdmitAsync(operatorWitness, PlatformOperations.ClosureFanOut, ticket, cancellationToken)
                .ConfigureAwait(false);
            if (admitted.Admission is not { } admission)
            {
                // The gate counted and logged the refusal by its cause; the group is not started.
                LogFailed(logger, "admission_" + admitted.Refusal.ToString());
                return ClosureGroupOutcome.Failed;
            }

            var result = await executor.ExecuteAsync(admission, group.TenantId, group.OrganizationId,
                new FanOutClosureCommand(subject, group.OrganizationId), cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                // The act's own MUST row records the refusal; the reason here is the closed key, never a detail.
                var reason = result.Error!.Details is { } details && details.Values.SelectMany(keys => keys).FirstOrDefault() is { } key
                    ? key.Key : result.Error.Message.Key;
                LogFailed(logger, reason);
                return ClosureGroupOutcome.Failed;
            }

            var outcome = result.Value;
            if (outcome is not (ClosureGroupOutcome.Started or ClosureGroupOutcome.Adopted or ClosureGroupOutcome.Waiting))
            {
                throw new InvalidOperationException("A closure's fan-out act answers started, adopted or waiting.");
            }

            var token = Token(outcome);
            LogFinished(logger, token);
            return outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A group that throws is that group's failure; the remaining groups still run (ADR-0107 § 7).
        catch (Exception failure)
#pragma warning restore CA1031
        {
            LogFailed(logger, failure.GetType().Name);
            return ClosureGroupOutcome.Failed;
        }
    }

    private static string Token(ClosureGroupOutcome outcome) => outcome switch
    {
        ClosureGroupOutcome.Started => "fan_out_started",
        ClosureGroupOutcome.Adopted => "fan_out_adopted",
        ClosureGroupOutcome.Waiting => "fan_out_waiting",
        _ => "fan_out_failed",
    };

    [LoggerMessage(EventId = 7541, Level = LogLevel.Information, Message = "Closure fan-out group finished ({Outcome}).")]
    private static partial void LogFinished(ILogger logger, string outcome);

    [LoggerMessage(EventId = 7542, Level = LogLevel.Warning,
        Message = "Closure fan-out group failed ({Reason}); a repeated Begin retries it.")]
    private static partial void LogFailed(ILogger logger, string reason);
}
