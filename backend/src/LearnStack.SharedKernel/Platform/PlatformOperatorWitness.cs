namespace LearnStack.SharedKernel.Platform;

/// <summary>
/// A validated operator principal, minted only by the operator authentication boundary.
/// </summary>
/// <remarks>
/// <para>
/// Nothing but a validated token produces one. The nested <see cref="Minter"/> is the only
/// way to construct a witness, and the closed census of its subclasses is an architecture
/// rule: the operator scheme handler, and from Step 3 the offline rebind tool, which
/// validates the same token through the same validator (ADR-0101 § 7).
/// </para>
/// <para>
/// A witness is evidence of authentication and nothing more. It carries no authority; the
/// gate evaluates a grant per act.
/// </para>
/// </remarks>
public sealed class PlatformOperatorWitness
{
    private PlatformOperatorWitness(
        string issuer,
        string subjectDigest,
        string acr,
        DateTimeOffset authenticatedAt,
        string executor,
        string? correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectDigest);
        ArgumentException.ThrowIfNullOrWhiteSpace(acr);
        ArgumentException.ThrowIfNullOrWhiteSpace(executor);
        Issuer = issuer;
        SubjectDigest = subjectDigest;
        Acr = acr;
        AuthenticatedAt = authenticatedAt;
        Executor = executor;
        CorrelationId = correlationId;
    }

    /// <summary>The pinned operator issuer that authenticated the token.</summary>
    public string Issuer { get; }

    /// <summary>The <see cref="PlatformOperatorSubject.Digest"/> of the operator.</summary>
    public string SubjectDigest { get; }

    /// <summary>The accepted <c>acr</c> value the token carried.</summary>
    public string Acr { get; }

    /// <summary>When the operator last authenticated (<c>auth_time</c>).</summary>
    public DateTimeOffset AuthenticatedAt { get; }

    /// <summary>The component that took the request, recorded as <c>metadata.operator.executor</c>.</summary>
    public string Executor { get; }

    /// <summary>The correlation id of the request, when one exists.</summary>
    public string? CorrelationId { get; }

    /// <inheritdoc />
    public override string ToString() => nameof(PlatformOperatorWitness);

    /// <summary>The only constructor of a witness; its subclass census is closed by an architecture rule.</summary>
#pragma warning disable CA1034 // Nested type: the private constructor and the minter census close this set.
    public abstract class Minter
#pragma warning restore CA1034
    {
        /// <summary>Mints a witness from an already validated operator principal.</summary>
        protected static PlatformOperatorWitness Mint(
            string issuer,
            string subjectDigest,
            string acr,
            DateTimeOffset authenticatedAt,
            string executor,
            string? correlationId) =>
            new(issuer, subjectDigest, acr, authenticatedAt, executor, correlationId);
    }
}
