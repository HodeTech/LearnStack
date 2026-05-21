using System.Diagnostics.CodeAnalysis;
using LearnStack.SharedKernel.Localization;

namespace LearnStack.SharedKernel.Results;

/// <summary>
/// Non-generic surface every <see cref="Result{T}"/> exposes. Pipeline
/// behaviors operate on this contract so they can construct the correct
/// concrete <c>Result&lt;TResponse&gt;</c> shape via the
/// <see cref="Result.FailFor{T}(Error)"/> factory (ADR-0032 § Sub-decision 3).
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

/// <summary>
/// Static helpers for constructing <see cref="Result{T}"/> generically
/// when the type parameter is known only at runtime (e.g. inside
/// <c>ValidationBehavior</c>). Per ADR-0032 § Sub-decision 3.
/// </summary>
public static class Result
{
    public static Result<T> Ok<T>(T value, LocalizedMessage? message = null) =>
        Result<T>.Ok(value, message);

    public static Result<T> Fail<T>(Error error) => Result<T>.Fail(error);

    /// <summary>
    /// Reflection-friendly failure factory used by MediatR pipeline behaviors
    /// (<c>ValidationBehavior</c>, <c>AuthorizationBehavior</c>) that
    /// short-circuit a generic handler without knowing <c>T</c> at compile
    /// time. Per ADR-0032 § Sub-decision 3.
    /// </summary>
    public static Result<T> FailFor<T>(Error error) => Result<T>.Fail(error);
}

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
public sealed record Result<T>(
    bool IsSuccess,
    T? Value,
    Error? Error,
    LocalizedMessage? SuccessMessage = null) : IResultBase
{
    public bool IsFailure => !IsSuccess;

    public static Result<T> Ok(T value, LocalizedMessage? message = null) =>
        new(true, value, null, message);

    public static Result<T> Fail(Error error) => new(false, default, error);
}
