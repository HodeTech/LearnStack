using System.Diagnostics.CodeAnalysis;

namespace LearnStack.SharedKernel.Results;

/// <summary>
/// Result-pattern wrapper. Returned by every MediatR command/query handler
/// per ADR-0032 § Error Model.
/// </summary>
/// <remarks>
/// CA1000 (do not declare static members on generic types) is intentionally
/// suppressed: <c>Result&lt;T&gt;.Ok(value)</c> / <c>Result&lt;T&gt;.Fail(error)</c>
/// are the canonical factory pattern for the Result type across the
/// FluentResults / Ardalis.Result ecosystem. The alternative (non-generic
/// <c>Result.Ok&lt;T&gt;(value)</c> helper) is awkward at call sites
/// because callers must repeat the type argument the inferrer already knows
/// from context. Per ADR-0032 § Error Model — every handler in the codebase
/// uses this shape.
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1000:Do not declare static members on generic types",
    Justification = "Result+Error factory pattern per ADR-0032 — canonical shape across FluentResults / Ardalis.Result lineage.")]
public sealed record Result<T>(bool IsSuccess, T? Value, Error? Error)
{
    public static Result<T> Ok(T value) => new(true, value, null);

    public static Result<T> Fail(Error error) => new(false, default, error);
}
