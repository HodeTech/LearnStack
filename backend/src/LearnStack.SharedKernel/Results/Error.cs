using System.Diagnostics.CodeAnalysis;
using LearnStack.SharedKernel.Localization;

namespace LearnStack.SharedKernel.Results;

/// <summary>
/// Result-pattern error payload used by <see cref="Result{T}"/>.
/// <see cref="Message"/> is the localised payload the frontend resolves;
/// <see cref="Code"/> is the stable machine-readable identifier API
/// consumers route on (Standards 04 § Problem Details and Standards 09
/// § Forbidden — codes do not get localized, only resolved messages do).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Code"/> is derived from <c>Message.Key</c> by stripping the
/// invariant <see cref="LocalizedMessage.RequiredPrefix"/>: a key of
/// <c>"lockey_validation_failed"</c> projects as <c>"validation_failed"</c>.
/// This keeps the two contracts in sync by construction — there is no way
/// to ship an <see cref="Error"/> whose Code disagrees with the
/// localization key the frontend resolves.
/// </para>
/// <para>
/// CA1716 (avoid reserved language keywords as type names) is intentionally
/// suppressed: the project's Result+Error pattern follows the FluentResults /
/// Ardalis.Result lineage where the type is canonically named <c>Error</c>.
/// LearnStack is C#-only — there is no VB consumer to which the "Error"
/// keyword collision would surface. Per ADR-0032 § Error Model.
/// </para>
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification = "Result+Error pattern — C#-only codebase per ADR-0032; no VB consumer affected.")]
public sealed record Error(
    LocalizedMessage Message,
    IReadOnlyDictionary<string, IReadOnlyList<LocalizedMessage>>? Details = null)
{
    /// <summary>
    /// Stable machine-readable code derived from <see cref="Message"/>'s
    /// <c>Key</c> with the <see cref="LocalizedMessage.RequiredPrefix"/>
    /// stripped. Used by <c>Result.ToActionResult()</c> and Problem Details
    /// writers as the RFC 7807 <c>code</c> field — never localized, never
    /// changes per locale (Standards 09 § Forbidden).
    /// </summary>
    public string Code => Message.Key[LocalizedMessage.RequiredPrefix.Length..];
}
