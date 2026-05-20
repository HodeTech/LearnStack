using System.Diagnostics.CodeAnalysis;

namespace LearnStack.SharedKernel.Results;

/// <summary>
/// Result-pattern error payload used by <see cref="Result{T}"/>.
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
    string Code,
    string Message,
    IReadOnlyDictionary<string, string[]>? Details = null)
{
    public static readonly Error None = new("none", string.Empty);
}
