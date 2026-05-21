using System.Diagnostics.CodeAnalysis;
using LearnStack.SharedKernel.Localization;

namespace LearnStack.SharedKernel.Results;

/// <summary>
/// Non-generic surface every <see cref="Result{T}"/> exposes. Pipeline
/// behaviors operate on this contract so they can construct the correct
/// concrete <c>Result&lt;TResponse&gt;</c> shape via the
/// <see cref="Result.FailFor{TResponse}(Error)"/> factory (ADR-0032 § Sub-decision 3).
/// </summary>
/// <remarks>
/// CA1716 (<c>Error</c> collides with VB's reserved keyword) is suppressed
/// for the same reason it is suppressed on the <see cref="Error"/> record
/// itself — LearnStack is C#-only per ADR-0032.
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification = "Result+Error pattern — C#-only codebase per ADR-0032; no VB consumer affected.")]
public interface IResultBase
{
    bool IsSuccess { get; }

    bool IsFailure { get; }

    LocalizedMessage? SuccessMessage { get; }

    Error? Error { get; }
}
