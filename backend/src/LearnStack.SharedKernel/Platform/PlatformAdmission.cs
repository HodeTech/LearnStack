namespace LearnStack.SharedKernel.Platform;

/// <summary>
/// Proof that one operator may perform one registered operation now, minted only by the gate.
/// </summary>
/// <remarks>
/// <para>
/// It binds the operator, the operation, the optional ticket and the correlation id, so the
/// scope, the audit rows and the result method take them from here and no caller can supply
/// or forge them. It is short lived: the gate mints it at the act and nothing stores it.
/// </para>
/// <para>
/// The <see cref="Minter"/> census is closed to the operator gate by an architecture rule.
/// </para>
/// </remarks>
public sealed class PlatformAdmission
{
    private PlatformAdmission(PlatformOperatorWitness operatorWitness, PlatformOperation operation, string? ticket)
    {
        ArgumentNullException.ThrowIfNull(operatorWitness);
        ArgumentNullException.ThrowIfNull(operation);
        Operator = operatorWitness;
        Operation = operation;
        Ticket = ticket;
    }

    /// <summary>The authenticated operator.</summary>
    public PlatformOperatorWitness Operator { get; }

    /// <summary>The registered operation admitted.</summary>
    public PlatformOperation Operation { get; }

    /// <summary>The optional, grammar-checked ticket; it appears only in sentinel-tenant rows.</summary>
    public string? Ticket { get; }

    /// <inheritdoc />
    public override string ToString() => nameof(PlatformAdmission);

    /// <summary>The only constructor of an admission; the gate is its one subclass.</summary>
#pragma warning disable CA1034 // Nested type: the private constructor and the minter census close this set.
    public abstract class Minter
#pragma warning restore CA1034
    {
        /// <summary>Mints an admission after the gate has evaluated the grant.</summary>
        protected static PlatformAdmission Mint(
            PlatformOperatorWitness operatorWitness, PlatformOperation operation, string? ticket) =>
            new(operatorWitness, operation, ticket);
    }
}
