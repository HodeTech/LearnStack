using System.Diagnostics.CodeAnalysis;
using LearnStack.SharedKernel.Localization;

namespace LearnStack.SharedKernel.Results;

/// <summary>
/// Result-pattern error payload used by <see cref="Result{T}"/>. The
/// <see cref="Message"/> carries the <c>lockey_</c> localization key the
/// frontend resolves; <see cref="Code"/> is a convenience projection of
/// <c>Message.Key</c> for routing logic (ADR-0032 § Sub-decision 6's
/// <c>ResultExtensions.ToActionResult()</c> matches on it).
/// </summary>
/// <remarks>
/// CA1716 (avoid reserved language keywords as type names) is intentionally
/// suppressed: the project's Result+Error pattern follows the FluentResults /
/// Ardalis.Result lineage where the type is canonically named <c>Error</c>.
/// LearnStack is C#-only — there is no VB consumer to which the "Error"
/// keyword collision would surface. Per ADR-0032 § Error Model.
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification = "Result+Error pattern — C#-only codebase per ADR-0032; no VB consumer affected.")]
public sealed record Error(
    LocalizedMessage Message,
    IReadOnlyDictionary<string, string[]>? Details = null)
{
    /// <summary>
    /// Localization-key projection of <see cref="Message"/>. Equivalent to
    /// <c>Message.Key</c>; surfaced as a top-level property so
    /// <c>Result.ToActionResult()</c> and Problem Details writers can read
    /// it without dereferencing the nested record.
    /// </summary>
    public string Code => Message.Key;
}
