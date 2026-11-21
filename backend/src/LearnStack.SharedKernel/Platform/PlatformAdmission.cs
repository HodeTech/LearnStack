namespace LearnStack.SharedKernel.Platform;

/// <summary>
/// Proof that one operator may perform one registered operation now, minted only by the gate.
/// </summary>
/// <remarks>
/// <para>
/// It binds the operator, the operation, the optional ticket and the correlation id, so the
/// scope, the audit rows and the result method take them from here and no caller can supply
/// or forge them. It is short lived and single use: the gate stamps the time it minted it,
/// the scope refuses one older than <see cref="MaximumAge"/>, and the scope consumes it on
/// entry so a retained admission cannot open a second privileged scope.
/// </para>
/// <para>
/// The <see cref="Minter"/> census is closed to the operator gate by an architecture rule.
/// </para>
/// </remarks>
public sealed class PlatformAdmission
{
    /// <summary>The longest an admission may wait between the gate and the scope entry.</summary>
    public static TimeSpan MaximumAge { get; } = TimeSpan.FromSeconds(30);

    private int _consumed;

    private PlatformAdmission(
        PlatformOperatorWitness operatorWitness, PlatformOperation operation, string? ticket, DateTimeOffset issuedAt)
    {
        ArgumentNullException.ThrowIfNull(operatorWitness);
        ArgumentNullException.ThrowIfNull(operation);
        Operator = operatorWitness;
        Operation = operation;
        Ticket = ticket;
        IssuedAt = issuedAt;
    }

    /// <summary>The authenticated operator.</summary>
    public PlatformOperatorWitness Operator { get; }

    /// <summary>The registered operation admitted.</summary>
    public PlatformOperation Operation { get; }

    /// <summary>The optional, grammar-checked ticket; it appears only in sentinel-tenant rows.</summary>
    public string? Ticket { get; }

    /// <summary>When the gate minted this admission.</summary>
    public DateTimeOffset IssuedAt { get; }

    /// <summary>
    /// Spends the admission; true for exactly one caller. The platform scope calls it on entry,
    /// so an admission that was kept, copied or replayed cannot open a second scope.
    /// </summary>
    public bool TryConsume() => Interlocked.Exchange(ref _consumed, 1) == 0;

    /// <inheritdoc />
    public override string ToString() => nameof(PlatformAdmission);

    /// <summary>The only constructor of an admission; the gate is its one subclass.</summary>
#pragma warning disable CA1034 // Nested type: the private constructor and the minter census close this set.
    public abstract class Minter
#pragma warning restore CA1034
    {
        /// <summary>Mints an admission after the gate has evaluated the grant.</summary>
        protected static PlatformAdmission Mint(
            PlatformOperatorWitness operatorWitness,
            PlatformOperation operation,
            string? ticket,
            DateTimeOffset issuedAt) =>
            new(operatorWitness, operation, ticket, issuedAt);
    }
}
