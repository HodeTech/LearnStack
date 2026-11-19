using System.Text.RegularExpressions;

namespace LearnStack.SharedKernel.Platform;

/// <summary>Why the gate refused an act.</summary>
public enum PlatformRefusal
{
    /// <summary>No refusal: the act is admitted.</summary>
    None = 0,

    /// <summary>The witness is absent or is not a current operator principal.</summary>
    InvalidWitness = 1,

    /// <summary>The operation is not one of the registered instances.</summary>
    UnknownOperation = 2,

    /// <summary>The ticket does not match its grammar.</summary>
    InvalidTicket = 3,

    /// <summary>The operator holds no grant for the operation's permission.</summary>
    MissingPermission = 4,
}

/// <summary>The gate's answer: an admission, or the reason it refused.</summary>
public sealed class PlatformAdmissionResult
{
    private PlatformAdmissionResult(PlatformAdmission? admission, PlatformRefusal refusal)
    {
        Admission = admission;
        Refusal = refusal;
    }

    /// <summary>The admission, when the act is admitted.</summary>
    public PlatformAdmission? Admission { get; }

    /// <summary>The refusal, or <see cref="PlatformRefusal.None"/>.</summary>
    public PlatformRefusal Refusal { get; }

    /// <summary>Whether the act is admitted.</summary>
    public bool IsAdmitted => Admission is not null;

    /// <summary>An admitted result.</summary>
    public static PlatformAdmissionResult Admit(PlatformAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        return new(admission, PlatformRefusal.None);
    }

    /// <summary>A refused result.</summary>
    public static PlatformAdmissionResult Refuse(PlatformRefusal refusal)
    {
        if (refusal == PlatformRefusal.None)
        {
            throw new ArgumentOutOfRangeException(nameof(refusal), "A refusal needs a cause.");
        }

        return new(null, refusal);
    }
}

/// <summary>
/// Decides, per act, whether an operator may perform one registered operation
/// (ADR-0101 § 7).
/// </summary>
/// <remarks>
/// Admission runs before any privileged credential or connection is resolved, so a refused
/// operator costs no bypass connection. The gate reads a grant on every call and never reads
/// a claim.
/// </remarks>
public interface IPlatformOperatorGate
{
    /// <summary>Admits or refuses <paramref name="operation"/> for <paramref name="operatorWitness"/>.</summary>
    ValueTask<PlatformAdmissionResult> AdmitAsync(
        PlatformOperatorWitness operatorWitness,
        PlatformOperation operation,
        string? ticket,
        CancellationToken cancellationToken = default);
}

/// <summary>The held grants of the operator population (ADR-0101 § 6).</summary>
public interface IPlatformOperatorGrantSource
{
    /// <summary>Whether the operator holds the Platform permission, evaluated now.</summary>
    ValueTask<bool> IsGrantedAsync(
        string subjectDigest, string permissionKey, CancellationToken cancellationToken = default);
}

/// <summary>The optional ticket grammar: 1 to 64 characters of <c>[A-Za-z0-9._:/#-]</c>.</summary>
public static partial class PlatformTicket
{
    /// <summary>Whether <paramref name="ticket"/> is absent or well formed.</summary>
    public static bool IsValid(string? ticket) => ticket is null || TicketPattern().IsMatch(ticket);

    [GeneratedRegex(@"^[A-Za-z0-9._:/#-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TicketPattern();
}
