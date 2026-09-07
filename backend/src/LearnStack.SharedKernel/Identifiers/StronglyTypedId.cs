namespace LearnStack.SharedKernel.Identifiers;

/// <summary>
/// What every layer means by "this identifier was actually supplied".
/// </summary>
/// <remarks>
/// <para>
/// <b>Two sentinels, and reading <c>.Value</c> on the first throws.</b> A Vogen id
/// nobody constructed is uninitialized, and touching <c>Value</c> raises from
/// inside the id type — so a validator that compares ids, or an aggregate factory
/// that stores one, turns a caller's malformed input into a <c>500</c> rather than
/// a refusal. The all-zero <see cref="Guid"/> is the second sentinel: it
/// constructs cleanly and means nothing.
/// </para>
/// <para>
/// <b>Here rather than once per module.</b> Every module's validators need this
/// answer and every module's aggregate factories throw on it, so a copy per module
/// is the same condition written several times — which is a condition that drifts
/// in one of them. The FluentValidation wrappers stay per module, because
/// <c>SharedKernel</c> takes no validation dependency; what is shared is the rule,
/// which is the part that can be wrong.
/// </para>
/// </remarks>
public static class StronglyTypedId
{
    /// <summary>Whether <paramref name="id"/> was constructed and is not the nil uuid.</summary>
    public static bool IsAssigned<TId>(TId id)
        where TId : struct, IStronglyTypedId<Guid>
        => id.IsInitialized() && id.Value != Guid.Empty;

    /// <summary>
    /// Whether <paramref name="id"/> is absent, or present and assigned.
    /// </summary>
    /// <remarks>
    /// For an optional identifier — absent is a scope, not a mistake — where only a
    /// supplied-but-meaningless value is a refusal.
    /// </remarks>
    public static bool IsAssignedWhenPresent<TId>(TId? id)
        where TId : struct, IStronglyTypedId<Guid>
        => id is not { } value || IsAssigned(value);
}
