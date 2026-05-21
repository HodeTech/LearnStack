namespace LearnStack.SharedKernel.Results;

/// <summary>
/// Empty value used as the payload of <c>Result&lt;Unit&gt;</c> when a
/// command/query succeeds without returning data. Use this rather than
/// <c>Result&lt;object?&gt;</c> with a null value (Standards 09 § Forbidden
/// bans the latter).
/// </summary>
public readonly record struct Unit
{
    /// <summary>
    /// The single canonical <see cref="Unit"/> value. All <see cref="Unit"/>
    /// instances are equal by definition; <see cref="Value"/> is just the
    /// idiomatic spelling at call sites.
    /// </summary>
    public static Unit Value => default;
}
