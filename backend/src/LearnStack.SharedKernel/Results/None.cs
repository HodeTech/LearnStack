namespace LearnStack.SharedKernel.Results;

/// <summary>
/// Empty value used as the payload of <c>Result&lt;None&gt;</c> when a
/// command/query succeeds without returning data. Use this rather than
/// <c>Result&lt;object?&gt;</c> with a null value (Standards 09 § Forbidden
/// bans the latter).
/// </summary>
/// <remarks>
/// Named <c>None</c> rather than <c>Unit</c> because every MediatR handler file
/// imports both <c>MediatR</c> and this namespace, and <c>MediatR.Unit</c> would
/// make the reference ambiguous in each one. The rename landed in
/// <see href="../../../../docs/roadmap/phase-02a-kernel-tenancy.md">Phase 02a
/// Packet 3b</see>, before the first handler existed.
/// </remarks>
public readonly record struct None
{
    /// <summary>
    /// The single canonical <see cref="None"/> value. All <see cref="None"/>
    /// instances are equal by definition; <see cref="Value"/> is just the
    /// idiomatic spelling at call sites.
    /// </summary>
    public static None Value => default;
}
