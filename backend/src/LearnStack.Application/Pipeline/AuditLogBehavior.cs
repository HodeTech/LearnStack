using System.Runtime.ExceptionServices;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.Logging;

namespace LearnStack.Application.Pipeline;

/// <summary>
/// MediatR pipeline behavior — step 3 of the canonical 8-step order
/// (ADR-0032 § Sub-decision 2). Per
/// <see href="../../../docs/decisions/0016-audit-log-subsystem.md">ADR-0016</see>
/// this behavior wraps the inner pipeline with <c>try / catch</c>, writes a
/// failure-class audit entry on exception, and rethrows via
/// <see cref="ExceptionDispatchInfo"/> to preserve the original stack trace.
/// The L1 <c>IExceptionHandler</c> is the final catch site below the
/// framework.
/// </summary>
/// <remarks>
/// <para>
/// Phase 02a Packet 3 ships the <strong>shell</strong>: the catch / rethrow
/// contract is wired, but the audit-write path is a no-op until Packet 9
/// lights up <c>LearnStack.Infrastructure.Audit</c> (per the Phase 02a
/// roadmap). The shell preserves two guarantees that Packet 9 cannot retrofit
/// without churn:
/// </para>
/// <list type="bullet">
///   <item>Exception rethrow uses <see cref="ExceptionDispatchInfo.Throw"/> —
///   handlers and the L1 boundary see the original stack.</item>
///   <item>Pipeline order: AuditLog wraps TenantContext + Authorization +
///   Transaction + OutboxFlush + Handler. The architecture test
///   <c>MediatR_Pipeline_Order_Matches_Canonical_Sequence</c> asserts the
///   wrap order; a Packet 9 swap must not change it.</item>
/// </list>
/// <para>
/// When Packet 9 lights up <c>IAuditStore</c> + <c>IAuditStateCapture</c>,
/// this class moves to <c>LearnStack.Infrastructure.Audit</c> with the same
/// shape — only the no-op TODOs flip to real writes.
/// </para>
/// </remarks>
public sealed class AuditLogBehavior<TRequest, TResponse>(
    ILogger<AuditLogBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : IResultBase
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        try
        {
            var response = await next().ConfigureAwait(false);

            // TODO(2026-05-21, @platform): Phase 02a Packet 9 — on success,
            // resolve IAuditStateCapture for the request type and write the
            // success-class audit entry through IAuditStore. Per ADR-0016 +
            // Standards 18 audit-coverage matrix. The shell shape here keeps
            // the pipeline contract intact until the audit infrastructure
            // lands.

            return response;
        }
#pragma warning disable CA1031 // Do not catch general exception types — ADR-0016 binds the audit-then-rethrow contract here.
        // Cancellation = client disconnect = noise per Standards 09 §
        // Sentry vs OpenTelemetry table; the L1 handler already swallows
        // it, and an audit entry for "user pressed Stop" is not useful.
        // Skip the catch and rethrow naturally.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            // TODO(2026-05-21, @platform): Phase 02a Packet 9 — write the
            // failure-class audit entry to audit_log via IAuditStore. The
            // shell logs the audit-intent so we do not silently lose the
            // failure visibility while Packet 9 is pending.
            LogAuditIntent(logger, typeof(TRequest).Name, ex);

            ExceptionDispatchInfo.Capture(ex).Throw();
            throw; // unreachable; the line above is the rethrow.
        }
    }

    private static readonly Action<ILogger, string, Exception?> LogAuditIntent =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogAuditIntent)),
            "AuditLogBehavior shell captured exception during {RequestName}; audit-write deferred until Packet 9 lights up IAuditStore.");
}
