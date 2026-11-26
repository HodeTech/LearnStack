using System.Collections.Frozen;
using System.Collections.Immutable;

namespace LearnStack.SharedKernel.DataProtection;

/// <summary>
/// One closed, bidirectional spelling table for an enum whose stored and wire spelling is a
/// lower snake_case code.
/// </summary>
/// <remarks>
/// <para>
/// The data-request vocabularies of
/// <see href="../../../../docs/decisions/0106-subject-data-requests-authority-and-status.md">ADR-0106</see>
/// and <see href="../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107</see>
/// are written as codes (<c>awaiting_privileged_step</c>, <c>audit_actor_reference</c>), and those
/// codes are what the database CHECKs, the receipts and the platform-side SQL name. The table
/// is written out pair by pair rather than derived from member names, so renaming a member cannot
/// silently rename a stored value.
/// </para>
/// <para>
/// Construction refuses an incomplete or ambiguous table: every defined member exactly once and
/// every code unique and well formed. A table that is wrong fails at type initialisation, not at
/// the first row that happens to carry the missing member.
/// </para>
/// </remarks>
public sealed class ClosedCodeTable<TEnum>
    where TEnum : struct, Enum
{
    private readonly FrozenDictionary<TEnum, string> _codes;
    private readonly FrozenDictionary<string, TEnum> _values;

    internal ClosedCodeTable(params (TEnum Value, string Code)[] pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var defined = Enum.GetValues<TEnum>();
        if (pairs.Length != defined.Length
            || pairs.Select(pair => pair.Value).Distinct().Count() != pairs.Length
            || !defined.All(value => pairs.Any(pair => EqualityComparer<TEnum>.Default.Equals(pair.Value, value)))
            || pairs.Select(pair => pair.Code).Distinct(StringComparer.Ordinal).Count() != pairs.Length
            || !pairs.All(pair => IsWellFormed(pair.Code)))
        {
            throw new InvalidOperationException("A closed code table must name every member exactly once with a unique code.");
        }

        _codes = pairs.ToFrozenDictionary(pair => pair.Value, pair => pair.Code);
        _values = pairs.ToFrozenDictionary(pair => pair.Code, pair => pair.Value, StringComparer.Ordinal);
        Codes = [.. pairs.Select(pair => pair.Code)];
    }

    /// <summary>Every code, in declaration order.</summary>
    public ImmutableArray<string> Codes { get; }

    /// <summary>The code of a defined member.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined member.</exception>
    public string ToCode(TEnum value) => _codes.TryGetValue(value, out var code)
        ? code
        : throw new ArgumentOutOfRangeException(nameof(value), "The value is not a member of the closed vocabulary.");

    /// <summary>Reads a code ordinally; no case folding and no member-name fallback.</summary>
    public bool TryParse(string? code, out TEnum value)
    {
        if (code is not null && _values.TryGetValue(code, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Reads a stored code; an unknown code is corrupt storage, not input.</summary>
    /// <exception cref="FormatException">The code is not in the closed vocabulary.</exception>
    public TEnum Parse(string code) => TryParse(code, out var value)
        ? value
        : throw new FormatException("The stored code is not a member of the closed vocabulary.");

    private static bool IsWellFormed(string code) =>
        code is { Length: > 0 and <= 64 }
        && code[0] is >= 'a' and <= 'z'
        && code[^1] is >= 'a' and <= 'z'
        && code.All(character => character is (>= 'a' and <= 'z') or '_');
}
