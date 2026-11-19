using LearnStack.SharedKernel.Platform;
using Microsoft.Extensions.Logging;

namespace LearnStack.Infrastructure.PlatformOperator;

/// <summary>
/// The operator gate: it checks the witness, the registered operation, the ticket grammar and
/// a grant evaluated now, then mints the admission (ADR-0101 § 7).
/// </summary>
/// <remarks>
/// <para>
/// <b>The one admission minter.</b> It derives from <see cref="PlatformAdmission.Minter"/>,
/// and an architecture rule closes that census to this class.
/// </para>
/// <para>
/// <b>Before any connection.</b> It resolves no privileged credential. A refused operator
/// costs no bypass connection, which is what C14 asks of a tenant or wrong-purpose credential.
/// The refusal is a counter and a Warning log carrying the operation slug and the subject
/// digest; no durable row is written (ADR-0101 § 11).
/// </para>
/// </remarks>
public sealed partial class PlatformOperatorGate(
    IPlatformOperatorGrantSource grants,
    ILogger<PlatformOperatorGate> logger) : PlatformAdmission.Minter, IPlatformOperatorGate
{
    private readonly IPlatformOperatorGrantSource _grants = grants ?? throw new ArgumentNullException(nameof(grants));
    private readonly ILogger<PlatformOperatorGate> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async ValueTask<PlatformAdmissionResult> AdmitAsync(
        PlatformOperatorWitness operatorWitness,
        PlatformOperation operation,
        string? ticket,
        CancellationToken cancellationToken = default)
    {
        if (operatorWitness is null)
        {
            return Refuse(PlatformRefusal.InvalidWitness, operation, null);
        }

        if (!PlatformOperations.IsRegistered(operation))
        {
            return Refuse(PlatformRefusal.UnknownOperation, null, operatorWitness);
        }

        if (!PlatformTicket.IsValid(ticket))
        {
            return Refuse(PlatformRefusal.InvalidTicket, operation, operatorWitness);
        }

        if (!await _grants.IsGrantedAsync(operatorWitness.SubjectDigest, operation.Permission.Key, cancellationToken))
        {
            return Refuse(PlatformRefusal.MissingPermission, operation, operatorWitness);
        }

        PlatformOperatorMetrics.Admitted(operation);
        return PlatformAdmissionResult.Admit(Mint(operatorWitness, operation, ticket));
    }

    private PlatformAdmissionResult Refuse(
        PlatformRefusal refusal, PlatformOperation? operation, PlatformOperatorWitness? operatorWitness)
    {
        PlatformOperatorMetrics.Refused(refusal, operation);
        LogRefused(_logger, refusal, operation?.AuditSlug ?? "unknown", operatorWitness?.SubjectDigest ?? "none");
        return PlatformAdmissionResult.Refuse(refusal);
    }

    [LoggerMessage(
        EventId = 7102,
        Level = LogLevel.Warning,
        Message = "Platform operator act refused: {Refusal} for {Operation} by operator {SubjectDigest}.")]
    private static partial void LogRefused(ILogger logger, PlatformRefusal refusal, string operation, string subjectDigest);
}
