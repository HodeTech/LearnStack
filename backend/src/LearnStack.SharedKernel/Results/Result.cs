using System.Diagnostics.CodeAnalysis;
using LearnStack.SharedKernel.Localization;

namespace LearnStack.SharedKernel.Results;

/// <summary>
/// Result-pattern wrapper. Returned by every MediatR command/query handler
/// per ADR-0032 § Error Model. Construction is funnelled through the
/// <see cref="Ok"/> / <see cref="Fail"/> factories; the primary
/// constructor is <c>internal</c> so callers cannot bypass the success-
/// must-carry-value rule via positional record syntax.
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
public sealed record Result<T> : IResultBase
{
    internal Result(bool isSuccess, T? value, Error? error, LocalizedMessage? successMessage = null)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
        SuccessMessage = successMessage;
    }

    /// <summary>
    /// True when the operation succeeded. The <see cref="MemberNotNullWhenAttribute"/>
    /// pairs teach flow analysis that a successful result carries a
    /// <see cref="Value"/> and no <see cref="Error"/>, so consumers can dereference
    /// either one after a single check without <c>!</c> and without a justification
    /// comment. Both this type and <see cref="IResultBase"/> carry the annotations:
    /// they do not flow from an interface to its implementation, so a caller typed
    /// to <c>Result&lt;T&gt;</c> would get nothing from the interface's copy alone.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess { get; }

    /// <inheritdoc cref="IResultBase.IsFailure" />
    [MemberNotNullWhen(false, nameof(Value))]
    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => !IsSuccess;

    public T? Value { get; }

    public Error? Error { get; }

    public LocalizedMessage? SuccessMessage { get; }

    /// <summary>
    /// Constructs a success result. Throws when <paramref name="value"/> is
    /// <c>null</c>: Standards 09 § Forbidden bans <c>IsSuccess = true</c>
    /// with <c>Value = null</c> — if a payload-less success shape is needed,
    /// model it as <c>Result&lt;None&gt;</c>.
    /// </summary>
    public static Result<T> Ok(T value, LocalizedMessage? message = null)
    {
        if (value is null)
        {
            throw new ArgumentNullException(
                nameof(value),
                "Result<T>.Ok cannot wrap a null value. Use Result<None> for payload-less success per Standards 09 § Forbidden.");
        }

        return new Result<T>(isSuccess: true, value: value, error: null, successMessage: message);
    }

    public static Result<T> Fail(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(isSuccess: false, value: default, error: error);
    }
}
