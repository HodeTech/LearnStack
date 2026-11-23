using LearnStack.SharedKernel.Audit;

namespace LearnStack.SharedKernel.Platform;

/// <summary>
/// Who acted and under which closed reason, as the audit rows of an operator act record it
/// (ADR-0101 § 9).
/// </summary>
/// <remarks>
/// <para>
/// Taken from an admission and from nothing else, so a row cannot name an operator who was not
/// admitted. It holds no ticket: the ticket appears only in rows a tenant cannot read, and an
/// attribution rides on rows a tenant can.
/// </para>
/// <para>
/// One definition of the <c>metadata.operator</c> shape, used by the platform scope's rows and by
/// the pipeline's rows alike, so the two cannot drift.
/// </para>
/// </remarks>
public sealed class PlatformOperatorAttribution
{
    private PlatformOperatorAttribution(string issuer, string subjectDigest, string acr, string executor, PlatformOperation operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectDigest);
        ArgumentException.ThrowIfNullOrWhiteSpace(acr);
        ArgumentException.ThrowIfNullOrWhiteSpace(executor);
        ArgumentNullException.ThrowIfNull(operation);
        Issuer = issuer;
        SubjectDigest = subjectDigest;
        Acr = acr;
        Executor = executor;
        Operation = operation;
        ReasonSlug = operation.ReasonSlug;
    }

    /// <summary>The pinned operator issuer.</summary>
    public string Issuer { get; }

    /// <summary>The operator's non-reversible subject handle.</summary>
    public string SubjectDigest { get; }

    /// <summary>The accepted <c>acr</c> the token carried.</summary>
    public string Acr { get; }

    /// <summary>The component that took the request.</summary>
    public string Executor { get; }

    /// <summary>The closed reason slug of the admitted operation.</summary>
    public string ReasonSlug { get; }

    /// <summary>
    /// The registered operation the act was admitted for, so a handler-side guard can tell one operator act from
    /// another by reference rather than by text (ADR-0102 Amendment 2: only <c>fan_out_closure</c> announces an
    /// organization). It is never written to a row.
    /// </summary>
    public PlatformOperation Operation { get; }

    /// <summary>The attribution of an admitted act.</summary>
    public static PlatformOperatorAttribution From(PlatformAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        var op = admission.Operator;
        return new(op.Issuer, op.SubjectDigest, op.Acr, op.Executor, admission.Operation);
    }

    /// <summary>
    /// The <c>operator</c> object of <c>metadata</c>, as JSON text: issuer, subject digest, kind,
    /// accepted <c>acr</c> and executor, and the ticket only when one is supplied.
    /// </summary>
    public string ToOperatorJson(string? ticket = null) =>
        "{"
        + "\"issuer\":" + AuditJson.Quote(Issuer)
        + ",\"subjectDigest\":" + AuditJson.Quote(SubjectDigest)
        + ",\"kind\":\"human\""
        + ",\"acr\":" + AuditJson.Quote(Acr)
        + ",\"executor\":" + AuditJson.Quote(Executor)
        + (ticket is null ? string.Empty : ",\"ticket\":" + AuditJson.Quote(ticket))
        + "}";

    /// <inheritdoc />
    public override string ToString() => nameof(PlatformOperatorAttribution);
}
