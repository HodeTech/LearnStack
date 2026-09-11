using LearnStack.SharedKernel.Audit;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LearnStack.Infrastructure.Audit;

/// <summary>
/// The last MUST-class standalone write's outcome, held for the <c>audit</c> health check.
/// </summary>
/// <remarks>
/// <b>Lock-free, because the state is one immutable reference.</b> Both reporters replace
/// it wholesale and the reader takes it once, so a concurrent failure and success cannot
/// interleave into a snapshot where <c>IsHealthy</c> is false and no operation is named.
/// Two fields and no lock would allow exactly that, and the field an operator reads first
/// is the one that would be empty.
/// </remarks>
public sealed class AuditHealth : IAuditHealth
{
    private volatile State _state = State.Healthy;

    /// <inheritdoc />
    public bool IsHealthy => _state.IsHealthy;

    /// <inheritdoc />
    public string? FailingOperation => _state.Operation;

    /// <inheritdoc />
    public void ReportStandaloneWriteFailed(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        _state = new State(IsHealthy: false, operation);
    }

    /// <inheritdoc />
    public void ReportStandaloneWriteSucceeded() => _state = State.Healthy;

    private sealed record State(bool IsHealthy, string? Operation)
    {
        public static readonly State Healthy = new(IsHealthy: true, Operation: null);
    }
}

/// <summary>
/// The check registered as <c>audit</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registered, not yet mapped, and that is the shipped scope.</b>
/// <see href="../../../docs/decisions/0033-audit-durability-model.md">ADR-0033
/// Amendment 3</see> ships the check here and demand-gates the readiness surface that
/// reads it — and the act of ceasing to serve — to
/// <see href="../../../docs/roadmap/phase-11-production-hardening.md">Phase 11</see>.
/// <c>/healthz</c> stays a liveness probe: a process that cannot write audit rows is
/// still a process worth leaving alive, and stopping one is the single operational act
/// the next request cannot undo.
/// </para>
/// <para>
/// <b>Degraded is not a state this reports.</b> The rule has two answers — the last
/// MUST-class standalone write landed, or it did not — and a middle one would invite a
/// readiness surface to treat "operations are completing unrecorded" as tolerable.
/// </para>
/// </remarks>
public sealed class AuditHealthCheck(IAuditHealth health) : IHealthCheck
{
    private readonly IAuditHealth _health = health ?? throw new ArgumentNullException(nameof(health));

    /// <summary>The name this check is registered under.</summary>
    public const string Name = "audit";

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(_health.IsHealthy
            ? HealthCheckResult.Healthy(
                "The last MUST-class standalone audit write landed.")
            : HealthCheckResult.Unhealthy(
                $"A MUST-class audit row for '{_health.FailingOperation}' could not be "
                + "written standalone, and no later one has succeeded. Operations are "
                + "completing without the record ADR-0033 requires."));
}
